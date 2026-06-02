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
    private readonly ToolStripMenuItem _agentsItem;
    private readonly ToolStripMenuItem _platformsItem;

    private string _modelName;
    private bool _inFlight;

    public TrayAppContext()
    {
        _config = HermesConfig.Load();
        _client = new HermesApiClient(_config);
        _modelName = _config.ModelName;

        _headerItem    = MakeDisabledItem("● Hermes");
        _gatewayItem   = MakeDisabledItem("Gateway: …");
        _modelItem     = MakeDisabledItem($"Model: {_modelName}");
        _agentsItem    = MakeDisabledItem("Active agents: …");
        _platformsItem = new ToolStripMenuItem("Platforms") { Enabled = false };

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _headerItem,
            _gatewayItem,
            _modelItem,
            _agentsItem,
            new ToolStripSeparator(),
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
            string? errorMessage = null;
            try
            {
                health = await _client.GetDetailedHealthAsync(_shutdownCts.Token);
            }
            catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
            {
                return; // shutting down
            }
            catch (Exception ex)
            {
                errorMessage = ex.GetBaseException().Message;
            }

            ApplyHealth(health, errorMessage);
        }
        finally
        {
            _inFlight = false;
        }
    }

    private void ApplyHealth(DetailedHealth? health, string? errorMessage)
    {
        IconRenderer.Status status;
        bool busy = false;
        string headerText;
        string gatewayText;
        string agentsText;
        string tooltip;

        if (health is null)
        {
            status = IconRenderer.Status.Down;
            headerText = "● Hermes (Unreachable)";
            gatewayText = "Gateway: " + (errorMessage ?? "no response");
            agentsText = "Active agents: —";
            tooltip = $"Hermes • unreachable\n{_config.BaseAddress}";
        }
        else
        {
            busy = health.ActiveAgents > 0;
            var anyPlatformError = health.Platforms is { Count: > 0 } &&
                health.Platforms.Values.Any(p => !string.Equals(p.State, "connected", StringComparison.OrdinalIgnoreCase));
            var gatewayRunning = string.Equals(health.GatewayState, "running", StringComparison.OrdinalIgnoreCase);

            status = (gatewayRunning, anyPlatformError) switch
            {
                (false, _) => IconRenderer.Status.Degraded,
                (true, true) => IconRenderer.Status.Degraded,
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
            agentsText = $"Active agents: {health.ActiveAgents}"
                + (busy ? "  •" : string.Empty);

            tooltip = $"Hermes • {health.GatewayState} • {_modelName} • {health.ActiveAgents} agent(s)";
        }

        _notifyIcon.Icon = _icons.Get(status, busy);
        // NotifyIcon.Text has a 127-char limit on modern Windows — trim defensively.
        _notifyIcon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;

        _headerItem.Text = headerText;
        _gatewayItem.Text = gatewayText;
        _agentsItem.Text = agentsText;

        RebuildPlatformsSubmenu(health?.Platforms);
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
