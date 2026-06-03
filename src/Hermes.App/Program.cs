using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Hermes.App;

/// <summary>
/// Hand-rolled entry point so we can intercept activation BEFORE
/// <see cref="Application.Start"/> spins up the XAML pump. The standard
/// single-instance redirection pattern from the Windows App SDK docs:
/// <see href="https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/applifecycle/applifecycle-instancing"/>.
///
/// <para>Why we want this: the tray's "Open Hermes app" entry shells out
/// to <c>explorer.exe shell:AppsFolder\&lt;AUMID&gt;</c>, which would
/// otherwise spawn a NEW Hermes.App process every time. With this in place,
/// the second launch detects the existing key holder, asks Windows to
/// forward its activation, and exits — the original process's
/// <see cref="App.OnInstanceActivated"/> brings the window to the foreground.</para>
///
/// <para>The XAML-generated Main is suppressed via the
/// <c>DISABLE_XAML_GENERATED_MAIN</c> define in
/// <c>Hermes.App.csproj</c>.</para>
/// </summary>
public static class Program
{
    /// <summary>Stable key shared across all activations of this app —
    /// changes here would break single-instancing for users who already
    /// have the app open. Process-wide so it doesn't collide with the tray
    /// or any other Hermes process.</summary>
    private const string InstanceKey = "Hermes.App.MainInstance";

    [STAThread]
    public static int Main(string[] args)
    {
        // CsWinRT marshalling has to be initialised before any Windows
        // Runtime calls — including AppInstance.GetCurrent. The XAML-
        // generated Main does this for us; since we replaced it, we have
        // to do it ourselves first.
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (DecideRedirection())
        {
            // We handed off our activation to an existing instance — exit
            // immediately so we don't briefly flash a second app on the
            // taskbar. RedirectActivationToAsync has already returned by
            // this point.
            return 0;
        }

        Application.Start((p) =>
        {
            // Standard WinUI 3 sync-context setup (mirrors what XamlGeneratedMain does).
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        return 0;
    }

    /// <summary>Returns true if this invocation was forwarded to the
    /// existing primary instance (and the current process should exit).
    /// Returns false if we ARE the primary instance — caller should
    /// continue normal startup.</summary>
    private static bool DecideRedirection()
    {
        AppActivationArguments activationArgs =
            AppInstance.GetCurrent().GetActivatedEventArgs();
        AppInstance keyInstance = AppInstance.FindOrRegisterForKey(InstanceKey);

        if (keyInstance.IsCurrent)
        {
            // We're the primary. Wire up the activated event so future
            // launches (which will redirect to us) can bring the window
            // to the foreground.
            keyInstance.Activated += App.OnInstanceActivated;
            return false;
        }

        // Someone else holds the key — hand our activation to them. The
        // async API doesn't have a sync wrapper but we're on the entry
        // thread and have nothing else to do, so blocking is fine.
        keyInstance.RedirectActivationToAsync(activationArgs)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        return true;
    }
}
