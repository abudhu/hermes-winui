using Hermes.ApiClient.Models;

namespace Hermes.TrayApp;

/// <summary>The set of strings the tray needs to render after a poll, plus
/// the status enum that picks the icon. Computed by <see cref="StatusBucketer"/>
/// in a pure pass so the rendering side has nothing left to decide.</summary>
public sealed record TrayStatusView(
    IconRenderer.Status Status,
    bool Busy,
    string HeaderText,
    string GatewayText,
    string RunsText,
    string SessionsCountText,
    string Tooltip);

/// <summary>
/// Pure status computation for the tray. Takes a poll snapshot in and emits
/// a <see cref="TrayStatusView"/> plus a live/stale split — no touching of
/// NotifyIcon, menu items, or anything stateful. Keeps the actual rendering
/// path in <see cref="TrayAppContext"/> trivially correct and trivially
/// reviewable.
/// </summary>
public static class StatusBucketer
{
    /// <summary>
    /// Split open sessions into live vs stale buckets. Sessions with no
    /// <c>last_active</c> are treated as live — we have no evidence to call
    /// them stale, and hiding a session because of missing telemetry would
    /// be the worst kind of bug ("where did my work go?").
    /// </summary>
    public static (IReadOnlyList<SessionSummary> Live, IReadOnlyList<SessionSummary> Stale) Bucket(
        IEnumerable<SessionSummary>? openSessions,
        TimeSpan staleAfter)
    {
        var open = openSessions?.ToList() ?? new List<SessionSummary>();
        var live = open.Where(s => s.SinceActive is not TimeSpan ts || ts < staleAfter).ToList();
        var stale = open.Where(s => s.SinceActive is TimeSpan ts && ts >= staleAfter).ToList();
        return (live, stale);
    }

    /// <summary>
    /// Compute the renderable tray-status view from a poll snapshot. Pure
    /// function: same inputs always produce the same outputs.
    /// </summary>
    public static TrayStatusView Compute(
        DetailedHealth? health,
        bool sessionsAvailable,
        IReadOnlyList<SessionSummary> liveOpen,
        IReadOnlyList<SessionSummary> staleOpen,
        string? errorMessage,
        string baseAddress,
        string modelName)
    {
        var liveCount = liveOpen.Count;
        var staleCount = staleOpen.Count;

        if (health is null)
        {
            return new TrayStatusView(
                Status: IconRenderer.Status.Down,
                Busy: liveCount > 0,
                HeaderText: "● Hermes (Unreachable)",
                GatewayText: "Gateway: " + (errorMessage ?? "no response"),
                RunsText: "Active runs (in gateway): —",
                SessionsCountText: sessionsAvailable
                    ? FormatSessionsCount(liveCount, staleCount)
                    : "Open sessions: —",
                Tooltip: $"Hermes • unreachable\n{baseAddress}");
        }

        var anyPlatformError = health.Platforms is { Count: > 0 } &&
            health.Platforms.Values.Any(p =>
                !string.Equals(p.State, "connected", StringComparison.OrdinalIgnoreCase));
        var gatewayRunning = string.Equals(health.GatewayState, "running", StringComparison.OrdinalIgnoreCase);

        // Busy if the gateway is currently servicing an agent turn, OR any
        // live session has had activity in the last 5 seconds. Stale sessions
        // by definition can't satisfy the 5s window, so they don't pollute
        // the busy indicator.
        var busy = health.ActiveAgents > 0
            || liveOpen.Any(s => s.SinceActive is TimeSpan ts && ts.TotalSeconds <= 5);

        var status = (gatewayRunning, anyPlatformError) switch
        {
            (false, _)    => IconRenderer.Status.Degraded,
            (true, true)  => IconRenderer.Status.Degraded,
            (true, false) => IconRenderer.Status.Healthy,
        };

        var headerText = status switch
        {
            IconRenderer.Status.Healthy  => "● Hermes",
            IconRenderer.Status.Degraded => "● Hermes (Degraded)",
            _                            => "● Hermes (Down)",
        };

        var gatewayText = $"Gateway: {health.GatewayState}"
            + (health.Pid is int pid ? $"  (PID {pid})" : string.Empty);

        var runsText = $"Active runs (in gateway): {health.ActiveAgents}"
            + (health.ActiveAgents > 0 ? "  •" : string.Empty);

        var sessionsCountText = FormatSessionsCount(liveCount, staleCount)
            + (liveCount > 0 && busy ? "  •" : string.Empty);

        var staleSuffix = staleCount > 0 ? $" (+{staleCount} stale)" : string.Empty;
        var tooltip = $"Hermes • {health.GatewayState} • {modelName} • " +
                      $"runs:{health.ActiveAgents} sessions:{liveCount}{staleSuffix}";

        return new TrayStatusView(status, busy, headerText, gatewayText, runsText, sessionsCountText, tooltip);
    }

    private static string FormatSessionsCount(int live, int stale) =>
        stale > 0
            ? $"Open sessions: {live}  (+{stale} stale)"
            : $"Open sessions: {live}";
}
