using System;
using System.Collections.Generic;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Hermes.App.Services;

/// <summary>
/// Thin façade over <see cref="AppNotificationManager"/>. Exists so the rest
/// of the app talks in domain terms ("a job finished") rather than the raw
/// toast XML / argument plumbing.
///
/// <para><b>Lifecycle.</b> Toast activation in a packaged WinAppSDK app
/// requires <see cref="AppNotificationManager.Register"/> to be called
/// <i>before</i> <see cref="Microsoft.Windows.AppLifecycle.AppInstance.GetActivatedEventArgs"/>
/// — otherwise cold-start activations from a toast click are dropped. Our
/// <c>Program.Main</c> calls <c>GetActivatedEventArgs</c> early as part of
/// single-instance redirection, so the actual <c>Register()</c> happens
/// in <see cref="RegisterEarly"/> from <c>Program.Main</c> before any of
/// that logic runs. The DI-resolvable instance just bridges the early-
/// registered event into a strongly-typed C# event for the UI layer.</para>
///
/// <para>The COM activator that delivers toast clicks back to us is
/// declared in <c>Package.appxmanifest</c> via
/// <c>windows.toastNotificationActivation</c> + <c>windows.comServer</c>
/// extensions with a stable CLSID.</para>
/// </summary>
public sealed class NotificationService
{
    /// <summary>Argument key used to route a toast click. We only ship one
    /// action shape today (<c>open-job</c>), but having an explicit key
    /// leaves room for future actions without ambiguity.</summary>
    private const string ActionKey = "action";

    private const string JobIdKey = "jobId";
    private const string ActionOpenJob = "open-job";

    /// <summary>
    /// Buffer of activations that arrived between <see cref="RegisterEarly"/>
    /// (from <c>Program.Main</c>) and the moment a live subscriber attaches
    /// via <see cref="AttachJobHandler"/>. The handler drains this on attach
    /// so a cold-start activation isn't dropped just because XAML hadn't
    /// finished spinning up when Windows fired the COM activator.
    /// </summary>
    private static readonly List<string> _pendingJobIds = [];
    private static readonly object _gate = new();
    private static bool _registered;

    /// <summary>Set inside <see cref="AttachJobHandler"/> — when present,
    /// every incoming activation is delivered directly to this handler
    /// instead of being buffered.</summary>
    private static Action<string>? _liveHandler;

    /// <summary>
    /// Subscribes to <see cref="AppNotificationManager.NotificationInvoked"/>
    /// then calls <see cref="AppNotificationManager.Register"/>. Idempotent:
    /// safe to call from a single-instance-redirected secondary process
    /// (which exits immediately afterward) without hosing the primary's
    /// registration.
    ///
    /// <para>MUST be called from <c>Program.Main</c> BEFORE the existing
    /// single-instance check (which calls <c>GetActivatedEventArgs</c>).
    /// Doing this later breaks cold-start toast activation per the
    /// WindowsAppSDK contract.</para>
    /// </summary>
    public static void RegisterEarly()
    {
        lock (_gate)
        {
            if (_registered) return;
            _registered = true;
        }

        // Toast registration can fail on machines whose COM activator
        // wiring (declared in Package.appxmanifest) doesn't resolve at
        // runtime — e.g. arch mismatch, missing WindowsAppSDK Singleton,
        // or stale loose-file deployment. Degrade gracefully: log the
        // failure and continue. The app starts; toasts no-op.
        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
            AppNotificationManager.Default.Register();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NotificationService] Register failed: {ex.GetType().Name}: {ex.Message}");
            lock (_gate) { _registerFailed = true; }
        }
    }

    private static bool _registerFailed;

    /// <summary>True if the early toast registration failed; callers can
    /// surface a one-time hint to the user but should otherwise carry on.</summary>
    public static bool RegisterFailed
    {
        get { lock (_gate) { return _registerFailed; } }
    }

    /// <summary>
    /// Attach the live handler that will receive future toast clicks AND
    /// any activations that arrived before the UI was ready. Call from
    /// <c>App.OnLaunched</c> after MainWindow is constructed so the
    /// handler can drive navigation immediately.
    /// </summary>
    public void AttachJobHandler(Action<string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        List<string> snapshot;
        lock (_gate)
        {
            _liveHandler = handler;
            snapshot = [.. _pendingJobIds];
            _pendingJobIds.Clear();
        }

        // Drain outside the lock so an exception in user code doesn't
        // deadlock subsequent notifications. Each item still goes through
        // the same path — caller is expected to marshal to the UI thread
        // before touching UI.
        foreach (var jobId in snapshot)
        {
            handler(jobId);
        }
    }

    /// <summary>
    /// Builds + shows a Windows app notification for a finished job. The
    /// click args are wired so <see cref="OnNotificationInvoked"/> can
    /// route the user back to the row.
    ///
    /// <para>App-notification delivery silently no-ops when the app is
    /// running elevated (admin) — documented Windows behaviour, not a bug
    /// we can paper over. The polling loop still drives the in-app row
    /// transition independently, so the user sees the finished state
    /// next time they open the page.</para>
    /// </summary>
    public void NotifyJobCompleted(string jobId, string displayName, string statusLabel, string? errorMessage)
    {
        // The status word goes in the body so screen readers and the
        // narrator-preview text both pick it up; the title carries the
        // job name so it's recognisable at a glance in Action Center.
        var titleText = $"Job finished: {displayName}";
        var bodyText = string.IsNullOrEmpty(errorMessage)
            ? $"Status: {statusLabel}"
            : $"{statusLabel} — {Truncate(errorMessage, 200)}";

        var builder = new AppNotificationBuilder()
            .AddArgument(ActionKey, ActionOpenJob)
            .AddArgument(JobIdKey, jobId)
            .AddText(titleText)
            .AddText(bodyText);

        AppNotificationManager.Default.Show(builder.BuildNotification());
    }

    /// <summary>
    /// Single entry point for <see cref="AppNotificationManager.NotificationInvoked"/>.
    /// Runs on a thread-pool callback — we do NO UI work here. The handler
    /// attached by <see cref="AttachJobHandler"/> is responsible for
    /// hopping to the UI dispatcher.
    /// </summary>
    private static void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        // Anything we don't recognise we ignore — keeps the door open for
        // future button-only activation arguments without re-spinning
        // this handler.
        if (!args.Arguments.TryGetValue(ActionKey, out var action) || action != ActionOpenJob) return;
        if (!args.Arguments.TryGetValue(JobIdKey, out var jobId) || string.IsNullOrWhiteSpace(jobId)) return;

        Action<string>? handler;
        lock (_gate)
        {
            handler = _liveHandler;
            if (handler is null)
            {
                _pendingJobIds.Add(jobId);
                return;
            }
        }
        handler(jobId);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
