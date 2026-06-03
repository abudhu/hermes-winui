using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hermes.ApiClient;
using Hermes.ApiClient.Models;
using Hermes.App.ViewModels;

namespace Hermes.App.Services;

/// <summary>
/// Builds the command palette's item list and runs the fuzzy match.
/// Decoupled from any specific page view-model — the palette host hands
/// in delegates for navigation actions ("open settings", "go to chat")
/// so the registry doesn't need to know about MainWindow internals.
///
/// <para>Phased load: <see cref="GetStatic"/> returns instantly from
/// in-memory data (no network). <see cref="LoadDynamicAsync"/> hits the
/// local gateway for sessions + jobs. The palette renders static rows
/// first so the user can type and invoke before the gateway responds.</para>
/// </summary>
public sealed class CommandRegistry
{
    private readonly HermesApiClient _api;
    private readonly ChatViewModel _chat;
    private readonly PaletteHost _host;

    public CommandRegistry(HermesApiClient api, ChatViewModel chat, PaletteHost host)
    {
        _api = api;
        _chat = chat;
        _host = host;
    }

    /// <summary>
    /// Per-app navigation hooks. Implemented by MainWindow — the registry
    /// invokes these inside command lambdas without holding a reference to
    /// the window itself. <see cref="ResumeSession"/> takes the session id
    /// because the palette doesn't (and shouldn't) know how to drive
    /// ChatViewModel resume directly.
    /// </summary>
    public sealed class PaletteHost
    {
        public required Func<Task> NavigateToChat { get; init; }
        public required Func<Task> NavigateToSettings { get; init; }
        public required Func<Task> NavigateToJobs { get; init; }
        public required Func<string, Task> ResumeSession { get; init; }
    }

    /// <summary>Static commands that never hit the gateway. Available
    /// immediately on palette open.</summary>
    public List<PaletteCommand> GetStatic()
    {
        return new List<PaletteCommand>
        {
            new() {
                Id = "app.newChat",
                Title = "New chat",
                Subtitle = "Start a fresh conversation",
                Group = "Chat",
                IconGlyph = "\uE8BD",
                Rank = 10,
                Invoke = async () => {
                    await _host.NavigateToChat();
                    if (_chat.NewChatCommand.CanExecute(null))
                    {
                        _chat.NewChatCommand.Execute(null);
                    }
                },
            },
            new() {
                Id = "nav.settings",
                Title = "Open Settings",
                Subtitle = "Configuration, models, advanced",
                Group = "Navigation",
                IconGlyph = "\uE713",
                Rank = 20,
                Invoke = () => _host.NavigateToSettings(),
            },
            new() {
                Id = "nav.jobs",
                Title = "Open Jobs",
                Subtitle = "Scheduled tasks and recurring agents",
                Group = "Navigation",
                IconGlyph = "\uE823",
                Rank = 21,
                Invoke = () => _host.NavigateToJobs(),
            },
            new() {
                Id = "chat.reloadModels",
                Title = "Reload models",
                Subtitle = "Re-fetch the available model list",
                Group = "Chat",
                IconGlyph = "\uE72C",
                Rank = 30,
                Invoke = () => {
                    if (_chat.ReloadModelsCommand.CanExecute(null))
                    {
                        _chat.ReloadModelsCommand.Execute(null);
                    }
                    return Task.CompletedTask;
                },
            },
        };
    }

    /// <summary>
    /// Hits the gateway for recent sessions and current jobs. Returns
    /// commands for resume-session, run/pause/resume-job. Failures are
    /// swallowed — palette is still usable with just static commands.
    /// The CT is observed; gateway timeout is 6s so a cancelled palette
    /// (user pressed Esc) can still leave the build mid-flight.
    /// </summary>
    public async Task<List<PaletteCommand>> LoadDynamicAsync(CancellationToken ct)
    {
        var dynamic = new List<PaletteCommand>();

        // Fire both fetches in parallel — they're independent and the
        // gateway can serve them concurrently. WhenAll surfaces the first
        // exception but we want the partial results, so each task wraps
        // its own try/catch and returns an empty list on failure.
        var sessionsTask = TryGetSessionsAsync(ct);
        var jobsTask = TryGetJobsAsync(ct);

        await Task.WhenAll(sessionsTask, jobsTask).ConfigureAwait(false);

        AddSessionCommands(dynamic, sessionsTask.Result);
        AddJobCommands(dynamic, jobsTask.Result);

        return dynamic;
    }

    private async Task<List<SessionSummary>> TryGetSessionsAsync(CancellationToken ct)
    {
        try
        {
            var resp = await _api.GetSessionsAsync(limit: 15, includeChildren: true, ct).ConfigureAwait(false);
            return resp?.Data ?? new List<SessionSummary>();
        }
        catch
        {
            // Palette stays usable with just static commands — no point
            // surfacing a per-row error for "couldn't load sessions".
            return new List<SessionSummary>();
        }
    }

    private async Task<List<Job>> TryGetJobsAsync(CancellationToken ct)
    {
        try
        {
            var resp = await _api.GetJobsAsync(ct).ConfigureAwait(false);
            return resp?.Jobs ?? new List<Job>();
        }
        catch
        {
            return new List<Job>();
        }
    }

    private void AddSessionCommands(List<PaletteCommand> dynamic, List<SessionSummary> sessions)
    {
        // Take the 10 most recent (server returns ordered by last_active
        // already, but be defensive — sort by LastActive desc, null-last).
        var orderedSessions = sessions
            .OrderByDescending(s => s.LastActive ?? 0)
            .Take(10);

        int rank = 100;
        foreach (var s in orderedSessions)
        {
            // Capture for the lambda — `s` is a foreach variable and was
            // historically shared across iterations on .NET; today it's
            // per-iteration, but explicit capture is still clearer to read.
            var sessionId = s.Id;
            var title = string.IsNullOrEmpty(s.Title) ? "(untitled)" : s.Title!;
            dynamic.Add(new PaletteCommand
            {
                Id = $"session.open:{sessionId}",
                Title = $"Open: {title}",
                Subtitle = ShortenPreview(s.Preview) ?? s.Source ?? sessionId,
                Group = "Sessions",
                IconGlyph = "\uE8FD",
                Rank = rank++,
                Invoke = () => _host.ResumeSession(sessionId),
            });
        }
    }

    private void AddJobCommands(List<PaletteCommand> dynamic, List<Job> jobs)
    {
        int rank = 200;
        foreach (var job in jobs)
        {
            var jobId = job.Id;
            var displayName = string.IsNullOrEmpty(job.Name) ? jobId : job.Name!;
            var scheduleHint = job.ScheduleDisplay ?? job.Schedule?.Expr;

            // Run command — always offered, regardless of pause state. The
            // gateway accepts an explicit run on a paused job (runs once,
            // doesn't unpause the schedule).
            dynamic.Add(new PaletteCommand
            {
                Id = $"job.run:{jobId}",
                Title = $"Run job: {displayName}",
                Subtitle = scheduleHint ?? "Run now",
                Group = "Jobs",
                IconGlyph = "\uE768",
                Rank = rank++,
                Invoke = () => RunJobAsync(jobId),
            });

            // Pause vs Resume — mutually exclusive based on the current
            // enabled state. Showing both is noise; pick the one that's
            // actually applicable.
            var isEnabled = job.Enabled ?? true;
            if (isEnabled)
            {
                dynamic.Add(new PaletteCommand
                {
                    Id = $"job.pause:{jobId}",
                    Title = $"Pause job: {displayName}",
                    Subtitle = scheduleHint ?? "Currently enabled",
                    Group = "Jobs",
                    IconGlyph = "\uE769",
                    Rank = rank++,
                    Invoke = () => PauseJobAsync(jobId),
                });
            }
            else
            {
                dynamic.Add(new PaletteCommand
                {
                    Id = $"job.resume:{jobId}",
                    Title = $"Resume job: {displayName}",
                    Subtitle = scheduleHint ?? "Currently paused",
                    Group = "Jobs",
                    IconGlyph = "\uE768",
                    Rank = rank++,
                    Invoke = () => ResumeJobAsync(jobId),
                });
            }
        }
    }

    private async Task RunJobAsync(string id)
    {
        try { await _api.RunJobAsync(id, CancellationToken.None).ConfigureAwait(false); }
        catch { /* errors surface on the Jobs page; palette stays quiet */ }
    }

    private async Task PauseJobAsync(string id)
    {
        try { await _api.PauseJobAsync(id, CancellationToken.None).ConfigureAwait(false); }
        catch { }
    }

    private async Task ResumeJobAsync(string id)
    {
        try { await _api.ResumeJobAsync(id, CancellationToken.None).ConfigureAwait(false); }
        catch { }
    }

    private static string? ShortenPreview(string? preview)
    {
        if (string.IsNullOrWhiteSpace(preview)) return null;
        var trimmed = preview.Trim();
        // Collapse newlines so the subtitle stays one line.
        trimmed = trimmed.Replace('\r', ' ').Replace('\n', ' ');
        const int max = 80;
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "…";
    }

    // ----- Fuzzy match ----------------------------------------------------

    /// <summary>
    /// Subsequence match with prefix / contiguous boosts. Returns
    /// <c>null</c> when the query characters can't be found in order.
    /// Pure function — no allocations beyond the local case-folded copy.
    ///
    /// <para>Scoring intent: contiguous prefix match should always beat
    /// non-prefix; matches in the title should beat matches in the
    /// subtitle; shorter titles with the same match should win on ties.
    /// The exact constants are tuned so that "new" matches "New chat"
    /// before "Reload models", and "set" matches "Open Settings" before
    /// "Sessions".</para>
    /// </summary>
    public static int? Score(PaletteCommand cmd, string query)
    {
        if (string.IsNullOrEmpty(query)) return 0;

        var titleScore = ScoreField(cmd.Title, query, isTitle: true);
        if (titleScore is not null) return titleScore;

        // Fall back to subtitle / group at a lower weight so users can
        // still find "Open Jobs" by typing "schedule" if the subtitle
        // mentions it.
        var subtitleScore = ScoreField(cmd.Subtitle ?? "", query, isTitle: false);
        if (subtitleScore is not null) return subtitleScore;

        return null;
    }

    private static int? ScoreField(string field, string query, bool isTitle)
    {
        if (string.IsNullOrEmpty(field)) return null;

        var f = field.ToLowerInvariant();
        var q = query.ToLowerInvariant();

        // Exact prefix wins big.
        if (f.StartsWith(q, StringComparison.Ordinal))
        {
            var baseScore = isTitle ? 10000 : 5000;
            // Shorter field => slightly higher score on prefix-tied matches.
            // 100 - length keeps the bonus positive for any field shorter
            // than 100 chars; longer fields tie at the floor.
            return baseScore + Math.Max(0, 100 - f.Length);
        }

        // Contiguous substring (non-prefix).
        var subIdx = f.IndexOf(q, StringComparison.Ordinal);
        if (subIdx >= 0)
        {
            var baseScore = isTitle ? 5000 : 2000;
            // Earlier match position is better.
            return baseScore - subIdx + Math.Max(0, 100 - f.Length);
        }

        // Subsequence — every query char appears in order, possibly with
        // gaps. Worth surfacing but ranks below contiguous matches.
        if (!IsSubsequence(f, q)) return null;

        var baseSubseq = isTitle ? 1000 : 500;
        return baseSubseq + Math.Max(0, 100 - f.Length);
    }

    private static bool IsSubsequence(string haystack, string needle)
    {
        int hi = 0;
        int ni = 0;
        while (hi < haystack.Length && ni < needle.Length)
        {
            if (haystack[hi] == needle[ni]) ni++;
            hi++;
        }
        return ni == needle.Length;
    }
}
