using System.Diagnostics;
using System.IO;
using Hermes.ApiClient;
using Hermes.ApiClient.Models;

namespace Hermes.TrayApp;

/// <summary>
/// Owns the tray's lifecycle: the <see cref="NotifyIcon"/>, the polling
/// timer, the API client, and the shutdown CTS. Computation of what to
/// display lives in <see cref="StatusBucketer"/>; the menu UI lives in
/// <see cref="TrayMenuBuilder"/>; this class wires them together and
/// hosts the side-effectful bits (icon swap, click handling, app launch).
/// Inherits <see cref="ApplicationContext"/> (vs Form) because we never
/// want a window — the app exists only in the tray.
/// </summary>
public sealed class TrayAppContext : ApplicationContext
{
    private const int PollIntervalMs = 5_000;

    /// <summary>
    /// Open sessions inactive for longer than this get bucketed under "Stale" in
    /// the Sessions submenu and excluded from the headline "Open sessions" count.
    /// They're almost always terminals the user closed without /exit, so showing
    /// them in the main count is misleading without hiding them entirely
    /// (sometimes they really are paused work).
    /// </summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

    private readonly HermesConfig _config;
    private readonly HermesApiClient _client;
    private readonly IconRenderer _icons = new();
    private readonly NotifyIcon _notifyIcon;
    private readonly TrayMenuBuilder _menu;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly CancellationTokenSource _shutdownCts = new();

    private string _modelName;
    private bool _inFlight;

    public TrayAppContext()
    {
        _config = HermesConfig.Load();
        _client = new HermesApiClient(_config);
        _modelName = _config.ModelName;

        _menu = new TrayMenuBuilder(
            initialModelName:    _modelName,
            staleAfter:          StaleAfter,
            onOpenHermesApp:     OpenHermesApp,
            onRefreshNow:        () => _ = RefreshAsync(),
            onOpenConfigFolder:  () => OpenFolder(_config.ConfigDirectory),
            onOpenLogsFolder:    () => OpenFolder(Path.Combine(_config.ConfigDirectory, "logs")),
            onShowAbout:         ShowAbout,
            onExit:              ExitTray);

        _notifyIcon = new NotifyIcon
        {
            Icon = _icons.Get(IconRenderer.Status.Unknown, busy: false),
            Visible = true,
            Text = "Hermes — connecting…",
            ContextMenuStrip = _menu.Menu,
        };
        // Left-click on the icon also opens the menu — matches user muscle memory
        // from apps like Discord, Slack, etc.
        _notifyIcon.MouseClick += OnNotifyIconMouseClick;

        _pollTimer = new System.Windows.Forms.Timer { Interval = PollIntervalMs };
        _pollTimer.Tick += async (_, _) => await RefreshAsync();
        _pollTimer.Start();

        // Kick off an immediate first refresh + one-time model probe.
        _ = InitialLoadAsync();
    }

    private void OnNotifyIconMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            ShowContextMenuAtCursor();
    }

    private async Task InitialLoadAsync()
    {
        // Pull the model name from /v1/models if it differs from the .env hint.
        try
        {
            var models = await _client.GetModelsAsync(_shutdownCts.Token);
            var first = models?.Data?.FirstOrDefault()?.Id;
            if (!string.IsNullOrWhiteSpace(first) && first != _modelName)
            {
                _modelName = first;
                _menu.SetModelName(_modelName);
            }
        }
        catch
        {
            // Non-fatal — health poll below will reflect the real state.
        }

        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        // Re-entrancy guard — a slow gateway response shouldn't queue up
        // multiple concurrent polls on the UI thread.
        if (_inFlight) return;
        _inFlight = true;
        try
        {
            // Run the two requests in parallel — /health/detailed is fast (~5ms)
            // but /api/sessions has been seen to take 50–4000ms depending on
            // how warm the gateway is. We don't want sessions latency to delay
            // the status dot updating.
            var healthTask = SafeCallAsync(() => _client.GetDetailedHealthAsync(_shutdownCts.Token));
            var sessionsTask = SafeCallAsync(() => _client.GetSessionsAsync(25, true, _shutdownCts.Token));

            DetailedHealth? health;
            SessionList? sessions;
            string? errorMessage;
            try
            {
                (health, errorMessage) = await healthTask;
                (sessions, _)          = await sessionsTask;
                // We only treat a health failure as a tray-wide error.
                // A sessions failure (e.g. 401, timeout) just hides the session list.
            }
            catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
            {
                return; // shutting down
            }

            ApplySnapshot(health, sessions, errorMessage);
        }
        finally
        {
            _inFlight = false;
        }
    }

    private static async Task<(T? Value, string? Error)> SafeCallAsync<T>(Func<Task<T?>> call)
    {
        try
        {
            return (await call(), null);
        }
        catch (OperationCanceledException)
        {
            throw; // let the caller decide
        }
        catch (Exception ex)
        {
            return (default, ex.GetBaseException().Message);
        }
    }

    private void ApplySnapshot(DetailedHealth? health, SessionList? sessions, string? errorMessage)
    {
        var (liveOpen, staleOpen) = StatusBucketer.Bucket(
            sessions?.Data?.Where(s => s.IsOpen),
            StaleAfter);

        var view = StatusBucketer.Compute(
            health,
            sessionsAvailable: sessions is not null,
            liveOpen,
            staleOpen,
            errorMessage,
            _config.BaseAddress.ToString(),
            _modelName);

        _notifyIcon.Icon = _icons.Get(view.Status, view.Busy);
        // NotifyIcon.Text has a 127-char limit on modern Windows — trim defensively.
        _notifyIcon.Text = view.Tooltip.Length > 127 ? view.Tooltip[..127] : view.Tooltip;

        _menu.Apply(view);
        _menu.RebuildPlatformsSubmenu(health?.Platforms);
        _menu.RebuildSessionsSubmenu(sessions is not null, liveOpen, staleOpen);
    }

    private void ShowContextMenuAtCursor()
    {
        if (_notifyIcon.ContextMenuStrip is null) return;
        // Reflection trick avoids the visual glitch where left-click + ContextMenuStrip
        // sometimes shows the menu offscreen; this is the documented workaround.
        var method = typeof(NotifyIcon).GetMethod("ShowContextMenu",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method?.Invoke(_notifyIcon, null);
    }

    private void ShowAbout()
    {
        var ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
        var msg =
            $"Hermes Tray  v{ver}\n\n" +
            $"Gateway: {_config.BaseAddress}\n" +
            $"Config:  {_config.ConfigDirectory}\n" +
            $"Model:   {_modelName}\n" +
            $"API key: {(string.IsNullOrEmpty(_config.ApiKey) ? "(not set)" : "(loaded from .env)")}";
        MessageBox.Show(msg, "Hermes Tray", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                MessageBox.Show($"Folder not found:\n{path}", "Hermes Tray",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't open {path}:\n{ex.Message}", "Hermes Tray",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Launches the packaged Hermes WinUI app via its AUMID. Uses the shell
    /// "AppsFolder" protocol — equivalent to the user clicking it in Start.
    /// If the package isn't installed, surface a clear error rather than
    /// silently failing.
    /// </summary>
    private static void OpenHermesApp()
    {
        // Package family name from Hermes.App's Package.appxmanifest identity.
        const string Aumid = @"Hermes.App_hyqgre7ye1qte!App";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"shell:AppsFolder\\{Aumid}",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Couldn't launch Hermes app:\n{ex.Message}\n\n" +
                "Make sure the Hermes.App MSIX package is installed (run\n" +
                "`dotnet build src\\Hermes.App\\Hermes.App.csproj` once to register it).",
                "Hermes Tray",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ExitTray()
    {
        _shutdownCts.Cancel();
        _pollTimer.Stop();
        _notifyIcon.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _shutdownCts.Cancel();
            _pollTimer.Dispose();
            _notifyIcon.MouseClick -= OnNotifyIconMouseClick;
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _client.Dispose();
            _icons.Dispose();
            _shutdownCts.Dispose();
        }
        base.Dispose(disposing);
    }
}
