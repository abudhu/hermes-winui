using System.Diagnostics;
using System.IO;
using System.Reflection;
using Hermes.ApiClient;
using Hermes.ApiClient.Models;

namespace Hermes.TrayApp;

/// <summary>
/// Owns the tray's lifecycle: NotifyIcon, context menu, polling timer, and
/// the API client. ApplicationContext (vs Form) is used because we never want
/// a window — the app exists only in the tray.
/// </summary>
public sealed class TrayAppContext : ApplicationContext
{
    private const int PollIntervalMs = 5_000;

    private readonly HermesConfig _config;
    private readonly HermesApiClient _client;
    private readonly IconRenderer _icons = new();
    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly CancellationTokenSource _shutdownCts = new();

    // Menu items we mutate on each poll
    private readonly ToolStripMenuItem _headerItem;
    private readonly ToolStripMenuItem _gatewayItem;
    private readonly ToolStripMenuItem _modelItem;
    private readonly ToolStripMenuItem _runsItem;
    private readonly ToolStripMenuItem _sessionsCountItem;
    private readonly ToolStripMenuItem _sessionsItem;
    private readonly ToolStripMenuItem _platformsItem;

    private string _modelName;
    private bool _inFlight;

    public TrayAppContext()
    {
        _config = HermesConfig.Load();
        _client = new HermesApiClient(_config);
        _modelName = _config.ModelName;

        _headerItem        = MakeDisabledItem("● Hermes");
        _gatewayItem       = MakeDisabledItem("Gateway: …");
        _modelItem         = MakeDisabledItem($"Model: {_modelName}");
        _runsItem          = MakeDisabledItem("Active runs (in gateway): …");
        _sessionsCountItem = MakeDisabledItem("Open sessions: …");
        _sessionsItem      = new ToolStripMenuItem("Sessions") { Enabled = false };
        _platformsItem     = new ToolStripMenuItem("Platforms") { Enabled = false };

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _headerItem,
            _gatewayItem,
            _modelItem,
            new ToolStripSeparator(),
            _runsItem,
            _sessionsCountItem,
            new ToolStripSeparator(),
            _sessionsItem,
            _platformsItem,
            new ToolStripSeparator(),
            MakeAction("Refresh now",         (_, _) => _ = RefreshAsync()),
            MakeAction("Open Hermes folder",  (_, _) => OpenFolder(_config.ConfigDirectory)),
            MakeAction("Open logs folder",    (_, _) => OpenFolder(Path.Combine(_config.ConfigDirectory, "logs"))),
            new ToolStripSeparator(),
            MakeAction("About Hermes Tray…",  (_, _) => ShowAbout()),
            MakeAction("Exit",                (_, _) => ExitTray()),
        });

        _notifyIcon = new NotifyIcon
        {
            Icon = _icons.Get(IconRenderer.Status.Unknown, busy: false),
            Visible = true,
            Text = "Hermes — connecting…",
            ContextMenuStrip = menu,
        };
        // Left-click on the icon also opens the menu — matches user muscle memory
        // from apps like Discord, Slack, etc.
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ShowContextMenuAtCursor();
        };

        _pollTimer = new System.Windows.Forms.Timer { Interval = PollIntervalMs };
        _pollTimer.Tick += async (_, _) => await RefreshAsync();
        _pollTimer.Start();

        // Kick off an immediate first refresh + one-time model probe.
        _ = InitialLoadAsync();
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
                _modelItem.Text = $"Model: {_modelName}";
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
            DetailedHealth? health = null;
            SessionList? sessions = null;
            string? errorMessage = null;

            // Run the two requests in parallel — /health/detailed is fast (~5ms)
            // but /api/sessions has been seen to take 50–4000ms depending on
            // how warm the gateway is. We don't want sessions latency to delay
            // the status dot updating.
            var healthTask = SafeCallAsync(() => _client.GetDetailedHealthAsync(_shutdownCts.Token));
            var sessionsTask = SafeCallAsync(() => _client.GetSessionsAsync(25, true, _shutdownCts.Token));

            try
            {
                (health, var healthErr) = await healthTask;
                (sessions, _)           = await sessionsTask;
                // We only treat a health failure as a tray-wide error.
                // A sessions failure (e.g. 401, timeout) just hides the session list.
                errorMessage = healthErr;
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
        IconRenderer.Status status;
        bool busy;
        string headerText;
        string gatewayText;
        string runsText;
        string sessionsCountText;
        string tooltip;

        // Compute open sessions independently of health — even if /health/detailed
        // fails (e.g. transient timeout) we can still surface session info.
        var openSessions = sessions?.Data?.Where(s => s.IsOpen).ToList() ?? new List<SessionSummary>();
        var openCount = openSessions.Count;
        var sessionsAvailable = sessions is not null;

        if (health is null)
        {
            status = IconRenderer.Status.Down;
            busy = openCount > 0;
            headerText = "● Hermes (Unreachable)";
            gatewayText = "Gateway: " + (errorMessage ?? "no response");
            runsText = "Active runs (in gateway): —";
            sessionsCountText = sessionsAvailable ? $"Open sessions: {openCount}" : "Open sessions: —";
            tooltip = $"Hermes • unreachable\n{_config.BaseAddress}";
        }
        else
        {
            var anyPlatformError = health.Platforms is { Count: > 0 } &&
                health.Platforms.Values.Any(p => !string.Equals(p.State, "connected", StringComparison.OrdinalIgnoreCase));
            var gatewayRunning = string.Equals(health.GatewayState, "running", StringComparison.OrdinalIgnoreCase);

            // Busy if the gateway is currently servicing an agent turn, OR
            // any external session has had activity in the last 5 seconds.
            // The 5s window matches our poll interval — if it's longer the icon
            // would flash off-on between polls; shorter and we'd miss bursty
            // tool calls.
            busy = health.ActiveAgents > 0
                || openSessions.Any(s => s.SinceActive is TimeSpan ts && ts.TotalSeconds <= 5);

            status = (gatewayRunning, anyPlatformError) switch
            {
                (false, _)    => IconRenderer.Status.Degraded,
                (true, true)  => IconRenderer.Status.Degraded,
                (true, false) => IconRenderer.Status.Healthy,
            };

            headerText = status switch
            {
                IconRenderer.Status.Healthy  => "● Hermes",
                IconRenderer.Status.Degraded => "● Hermes (Degraded)",
                _                            => "● Hermes (Down)",
            };

            gatewayText = $"Gateway: {health.GatewayState}"
                + (health.Pid is int pid ? $"  (PID {pid})" : string.Empty);

            runsText = $"Active runs (in gateway): {health.ActiveAgents}"
                + (health.ActiveAgents > 0 ? "  •" : string.Empty);

            sessionsCountText = sessionsAvailable
                ? $"Open sessions: {openCount}" + (openCount > 0 ? "  •" : string.Empty)
                : "Open sessions: —";

            tooltip = $"Hermes • {health.GatewayState} • {_modelName} • runs:{health.ActiveAgents} sessions:{(sessionsAvailable ? openCount.ToString() : "?")}";
        }

        _notifyIcon.Icon = _icons.Get(status, busy);
        // NotifyIcon.Text has a 127-char limit on modern Windows — trim defensively.
        _notifyIcon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;

        _headerItem.Text = headerText;
        _gatewayItem.Text = gatewayText;
        _runsItem.Text = runsText;
        _sessionsCountItem.Text = sessionsCountText;

        RebuildPlatformsSubmenu(health?.Platforms);
        RebuildSessionsSubmenu(sessionsAvailable, openSessions);
    }

    private void RebuildSessionsSubmenu(bool available, IReadOnlyList<SessionSummary> openSessions)
    {
        _sessionsItem.DropDownItems.Clear();
        if (!available)
        {
            _sessionsItem.Text = "Sessions";
            _sessionsItem.DropDownItems.Add(MakeDisabledItem("(unavailable — check API key)"));
            _sessionsItem.Enabled = false;
            return;
        }

        _sessionsItem.Text = $"Sessions ({openSessions.Count})";
        if (openSessions.Count == 0)
        {
            _sessionsItem.DropDownItems.Add(MakeDisabledItem("(none open)"));
            _sessionsItem.Enabled = false;
            return;
        }

        _sessionsItem.Enabled = true;
        // Most-recently-active first so the user sees what they're currently doing at the top.
        foreach (var s in openSessions.OrderByDescending(s => s.LastActive ?? s.StartedAt ?? 0))
        {
            var source = (s.Source ?? "?").ToUpperInvariant();
            var title = TruncateMiddle(string.IsNullOrWhiteSpace(s.Title) ? "(untitled)" : s.Title, 42);
            var age = FormatAge(s.SinceActive);
            var label = $"● {source} · {title} · {age}";
            var item = MakeDisabledItem(label);
            if (!string.IsNullOrWhiteSpace(s.Preview))
                item.ToolTipText = s.Preview;
            _sessionsItem.DropDownItems.Add(item);
        }
    }

    private static string FormatAge(TimeSpan? span)
    {
        if (span is not TimeSpan t) return "unknown";
        if (t.TotalSeconds < 10) return "just now";
        if (t.TotalSeconds < 60) return $"{(int)t.TotalSeconds}s ago";
        if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes}m ago";
        if (t.TotalHours < 24)   return $"{(int)t.TotalHours}h ago";
        return $"{(int)t.TotalDays}d ago";
    }

    private static string TruncateMiddle(string value, int max)
    {
        if (value.Length <= max) return value;
        var keep = (max - 1) / 2;
        return value[..keep] + "…" + value[^keep..];
    }

    private void RebuildPlatformsSubmenu(IReadOnlyDictionary<string, PlatformStatus>? platforms)
    {
        _platformsItem.DropDownItems.Clear();
        if (platforms is null || platforms.Count == 0)
        {
            _platformsItem.DropDownItems.Add(MakeDisabledItem("(no platforms reported)"));
            _platformsItem.Enabled = false;
            return;
        }

        _platformsItem.Enabled = true;
        foreach (var (name, info) in platforms.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var marker = string.Equals(info.State, "connected", StringComparison.OrdinalIgnoreCase) ? "✓" : "✗";
            var label = $"{marker}  {name}  ({info.State})";
            var item = MakeDisabledItem(label);
            if (!string.IsNullOrWhiteSpace(info.ErrorMessage))
                item.ToolTipText = info.ErrorMessage;
            _platformsItem.DropDownItems.Add(item);
        }
    }

    private void ShowContextMenuAtCursor()
    {
        var menu = _notifyIcon.ContextMenuStrip;
        if (menu is null) return;
        // Reflection trick avoids the visual glitch where leftclick + ContextMenuStrip
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

    private void ExitTray()
    {
        _shutdownCts.Cancel();
        _pollTimer.Stop();
        _notifyIcon.Visible = false;
        ExitThread();
    }

    private static ToolStripMenuItem MakeDisabledItem(string text) =>
        new(text) { Enabled = false };

    private static ToolStripMenuItem MakeAction(string text, EventHandler handler)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += handler;
        return item;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _shutdownCts.Cancel();
            _pollTimer.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _client.Dispose();
            _icons.Dispose();
            _shutdownCts.Dispose();
        }
        base.Dispose(disposing);
    }
}
