using Hermes.ApiClient.Models;

namespace Hermes.TrayApp;

/// <summary>
/// Owns the tray's <see cref="ContextMenuStrip"/> and every
/// <see cref="ToolStripMenuItem"/> that gets mutated on each poll.
/// <see cref="TrayAppContext"/> hands in a freshly-computed
/// <see cref="TrayStatusView"/> plus the bucketed sessions and platforms;
/// the builder applies them. Static action items (Refresh, Open folder,
/// Exit, …) are wired in via callbacks supplied at construction time so
/// the builder doesn't need a back-reference to the context.
/// </summary>
public sealed class TrayMenuBuilder
{
    private readonly ToolStripMenuItem _headerItem;
    private readonly ToolStripMenuItem _gatewayItem;
    private readonly ToolStripMenuItem _modelItem;
    private readonly ToolStripMenuItem _runsItem;
    private readonly ToolStripMenuItem _sessionsCountItem;
    private readonly ToolStripMenuItem _sessionsItem;
    private readonly ToolStripMenuItem _platformsItem;
    private readonly TimeSpan _staleAfter;

    /// <summary>The fully-built context menu, ready to attach to a NotifyIcon.</summary>
    public ContextMenuStrip Menu { get; }

    public TrayMenuBuilder(
        string initialModelName,
        TimeSpan staleAfter,
        Action onOpenHermesApp,
        Action onRefreshNow,
        Action onOpenConfigFolder,
        Action onOpenLogsFolder,
        Action onShowAbout,
        Action onExit)
    {
        _staleAfter = staleAfter;

        _headerItem        = MakeDisabledItem("● Hermes");
        _gatewayItem       = MakeDisabledItem("Gateway: …");
        _modelItem         = MakeDisabledItem($"Model: {initialModelName}");
        _runsItem          = MakeDisabledItem("Active runs (in gateway): …");
        _sessionsCountItem = MakeDisabledItem("Open sessions: …");
        _sessionsItem      = new ToolStripMenuItem("Sessions") { Enabled = false };
        _platformsItem     = new ToolStripMenuItem("Platforms") { Enabled = false };

        Menu = new ContextMenuStrip();
        Menu.Items.AddRange(new ToolStripItem[]
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
            MakeAction("Open Hermes app",     (_, _) => onOpenHermesApp()),
            MakeAction("Refresh now",         (_, _) => onRefreshNow()),
            MakeAction("Open Hermes folder",  (_, _) => onOpenConfigFolder()),
            MakeAction("Open logs folder",    (_, _) => onOpenLogsFolder()),
            new ToolStripSeparator(),
            MakeAction("About Hermes Tray…",  (_, _) => onShowAbout()),
            MakeAction("Exit",                (_, _) => onExit()),
        });
    }

    /// <summary>Update the static "Model: …" line — fired once after the
    /// first /v1/models probe completes.</summary>
    public void SetModelName(string modelName) =>
        _modelItem.Text = $"Model: {modelName}";

    /// <summary>Push the polled status labels into the menu. Submenus are
    /// rebuilt separately via the two <c>Rebuild…Submenu</c> calls.</summary>
    public void Apply(TrayStatusView view)
    {
        _headerItem.Text = view.HeaderText;
        _gatewayItem.Text = view.GatewayText;
        _runsItem.Text = view.RunsText;
        _sessionsCountItem.Text = view.SessionsCountText;
    }

    public void RebuildSessionsSubmenu(
        bool sessionsAvailable,
        IReadOnlyList<SessionSummary> liveOpen,
        IReadOnlyList<SessionSummary> staleOpen)
    {
        _sessionsItem.DropDownItems.Clear();

        if (!sessionsAvailable)
        {
            _sessionsItem.Text = "Sessions";
            _sessionsItem.DropDownItems.Add(MakeDisabledItem("(unavailable — check API key)"));
            _sessionsItem.Enabled = false;
            return;
        }

        var headerCount = staleOpen.Count > 0
            ? $"{liveOpen.Count}  (+{staleOpen.Count} stale)"
            : liveOpen.Count.ToString();
        _sessionsItem.Text = $"Sessions ({headerCount})";

        if (liveOpen.Count == 0 && staleOpen.Count == 0)
        {
            _sessionsItem.DropDownItems.Add(MakeDisabledItem("(none open)"));
            _sessionsItem.Enabled = false;
            return;
        }

        _sessionsItem.Enabled = true;

        // Live sessions first, most-recently-active at the top.
        foreach (var s in liveOpen.OrderByDescending(s => s.LastActive ?? s.StartedAt ?? 0))
            _sessionsItem.DropDownItems.Add(MakeSessionMenuItem(s));

        if (staleOpen.Count == 0) return;

        if (liveOpen.Count > 0)
            _sessionsItem.DropDownItems.Add(new ToolStripSeparator());
        _sessionsItem.DropDownItems.Add(
            MakeDisabledItem($"Stale  ({staleOpen.Count}, inactive >{(int)_staleAfter.TotalHours}h)"));

        // Cap stale at 20 to keep the menu reasonable; the rest stay
        // accessible via the dashboard / future Sessions UI.
        const int staleCap = 20;
        foreach (var s in staleOpen.OrderByDescending(s => s.LastActive ?? s.StartedAt ?? 0).Take(staleCap))
            _sessionsItem.DropDownItems.Add(MakeSessionMenuItem(s));
        if (staleOpen.Count > staleCap)
            _sessionsItem.DropDownItems.Add(MakeDisabledItem($"  … and {staleOpen.Count - staleCap} more"));
    }

    public void RebuildPlatformsSubmenu(IReadOnlyDictionary<string, PlatformStatus>? platforms)
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

    private static ToolStripMenuItem MakeSessionMenuItem(SessionSummary s)
    {
        var source = (s.Source ?? "?").ToUpperInvariant();
        var title = TruncateMiddle(string.IsNullOrWhiteSpace(s.Title) ? "(untitled)" : s.Title, 42);
        var age = FormatAge(s.SinceActive);
        var label = $"● {source} · {title} · {age}";
        var item = MakeDisabledItem(label);
        if (!string.IsNullOrWhiteSpace(s.Preview))
            item.ToolTipText = s.Preview;
        return item;
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

    private static ToolStripMenuItem MakeDisabledItem(string text) =>
        new(text) { Enabled = false };

    private static ToolStripMenuItem MakeAction(string text, EventHandler handler)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += handler;
        return item;
    }
}
