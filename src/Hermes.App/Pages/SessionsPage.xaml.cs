using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hermes.ApiClient;
using Hermes.ApiClient.Models;
using Hermes.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Hermes.App.Pages;

public sealed partial class SessionsPage : Page
{
    private const int DebounceMilliseconds = 250;
    private const int ContentSearchLimit = 30;

    // Pseudo-SortKeys for the two search-mode group headers. Larger sorts
    // higher (matches DateGroupHelper's convention). Picked outside the
    // date-bucket range so they don't collide if the rebuild logic ever
    // gets bypassed mid-flight.
    private const long MetadataGroupSortKey = 9_999_999_995L;
    private const long ContentGroupSortKey  = 9_999_999_994L;

    private readonly HermesApiClient _api;
    private readonly ChatViewModel _chat;
    private CancellationTokenSource? _detailCts;
    private CancellationTokenSource? _searchCts;

    // Monotonically increasing per-search id. Defense-in-depth past CTS
    // cancellation: any UI write that lands after an `await` first checks
    // its captured generation against the current one, so a slow server
    // response from a stale keystroke can't paint over a newer one.
    private int _searchGeneration;

    private SessionRowVm? _selectedRow;

    // Guard around Groups.Clear / rebuild cycles. While true, the
    // SelectionChanged handler treats a null selection as "WinUI dropped
    // it because the items collection shifted" rather than "user
    // deselected", so _selectedRow stays valid and Resume keeps working
    // when the user types a query that filters out the selected row.
    private bool _suppressSelectionEvents;

    /// <summary>Top-level groups for the left ListView. Each group's
    /// <c>Items</c> holds the actual session rows. The <c>CollectionViewSource</c>
    /// declared in XAML grafts these together with <c>IsSourceGrouped</c>.</summary>
    public ObservableCollection<SessionGroupVm> Groups { get; } = [];

    public ObservableCollection<MessageRowVm> Messages { get; } = [];

    /// <summary>
    /// Cached flat list of every loaded session — the source of truth for
    /// search. <see cref="Groups"/> is a derived view rebuilt from this
    /// whenever the user types in the search box or hits Refresh.
    /// </summary>
    private readonly List<SessionRowVm> _allRows = [];

    public SessionsPage()
    {
        _api = App.Services.GetRequiredService<HermesApiClient>();
        _chat = App.Services.GetRequiredService<ChatViewModel>();
        InitializeComponent();

        // Wire CollectionViewSource → grouped collection here (rather than
        // in XAML via x:Bind) so the binding evaluates exactly once after
        // the page is initialised, with the live ObservableCollection.
        GroupedSessionsSource.Source = Groups;

        // Cancel + dispose the in-flight detail load when the page goes
        // away. Without this, a CTS created with a timeout keeps an
        // internal Timer alive that holds the CTS (and its registered
        // callbacks) for up to 10s after navigation. Same logic for the
        // search CTS — its Task.Delay registers a callback on the token
        // that lingers until the delay elapses.
        Unloaded += (_, _) =>
        {
            DisposeDetailCts();
            CancelInFlightSearch();
        };
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        // A refresh is going to blow away the rows the in-flight search is
        // about to enrich. Cancel + bump the generation so any pending
        // search awaits exit before they paint stale data.
        CancelInFlightSearch();

        Subtitle.Text = "Loading…";
        try
        {
            var sessionsResponse = await _api.GetSessionsAsync(50, true, CancellationToken.None);
            _allRows.Clear();
            if (sessionsResponse?.Data is null)
            {
                RebuildGroups(Array.Empty<SessionGroupVm>());
                Subtitle.Text = "No sessions";
                return;
            }

            foreach (var s in sessionsResponse.Data)
            {
                _allRows.Add(SessionRowVm.FromSummary(s));
            }

            // Re-apply the current search box state — the user might have
            // a query typed when they hit Refresh, and we want the new
            // _allRows to flow through the same filter pipeline.
            await RunSearchPipelineAsync(SearchBox.Text?.Trim() ?? "", debounce: false);
        }
        catch (Exception ex)
        {
            Subtitle.Text = $"Error: {ex.Message}";
        }
    }

    private async void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // Only react to user keystrokes — ignore programmatic text changes
        // (e.g. the box clearing itself after selection).
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        var query = sender.Text?.Trim() ?? "";
        await RunSearchPipelineAsync(query, debounce: true);
    }

    /// <summary>
    /// Top of the search pipeline: cancels any in-flight search, opens a
    /// fresh CTS + generation, optionally waits out the debounce window,
    /// then defers to <see cref="ExecuteSearchAsync"/>. Empty queries
    /// bypass debouncing entirely so clearing the box feels instant.
    /// </summary>
    private async Task RunSearchPipelineAsync(string query, bool debounce)
    {
        CancelInFlightSearch();
        var generation = ++_searchGeneration;
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        if (query.Length == 0)
        {
            ApplyNoQueryView();
            return;
        }

        try
        {
            if (debounce)
            {
                await Task.Delay(DebounceMilliseconds, ct);
            }
            await ExecuteSearchAsync(query, generation, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Superseded by a newer keystroke or by RefreshAsync — let
            // the newer call own the UI.
        }
    }

    /// <summary>
    /// Cancels the in-flight search and disposes its CTS. Also bumps the
    /// generation counter so any continuation that's already past the
    /// cancellation check will still bail out on its captured generation
    /// before writing to the UI. Safe to call repeatedly.
    /// </summary>
    private void CancelInFlightSearch()
    {
        _searchGeneration++;
        var cts = _searchCts;
        if (cts is null) return;
        _searchCts = null;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        cts.Dispose();
    }

    /// <summary>Runs the metadata + server-side content search and merges
    /// them into two flat groups. Every UI mutation guards on
    /// <paramref name="generation"/> to avoid stale paints after the
    /// user keeps typing.</summary>
    private async Task ExecuteSearchAsync(string query, int generation, CancellationToken ct)
    {
        var metadataMatches = ComputeMetadataMatches(query);

        // Immediate paint with metadata matches + a "Searching…" subtitle.
        // This gives the user something the moment the debounce elapses;
        // the content matches are folded in once the server replies.
        if (generation != _searchGeneration) return;
        RebuildGroupsForSearch(query, metadataMatches, contentMatches: [], snippetsById: null, searching: true);

        SessionSearchResponse? response;
        try
        {
            response = await _api.SearchSessionsAsync(query, ContentSearchLimit, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Don't blank the sidebar on a search failure — metadata
            // matches still came back synchronously, and the user might
            // keep typing past the error.
            if (generation != _searchGeneration) return;
            Subtitle.Text = $"Search error: {ex.Message}";
            return;
        }

        if (generation != _searchGeneration || ct.IsCancellationRequested) return;

        var serverResults = response?.Results ?? [];
        var contentMatches = BuildContentMatches(serverResults, metadataMatches);
        var snippetsById = BuildSnippetMap(serverResults);
        RebuildGroupsForSearch(query, metadataMatches, contentMatches, snippetsById, searching: false);
    }

    /// <summary>
    /// Synchronous "is this row a plausible hit?" check against fields the
    /// sidebar already surfaces. Same surface area as the old in-process
    /// filter — case-insensitive contains across Title, Preview, Model,
    /// and Source — so the user's existing muscle memory keeps working.
    /// </summary>
    private List<SessionRowVm> ComputeMetadataMatches(string query)
    {
        var matches = new List<SessionRowVm>();
        foreach (var row in _allRows)
        {
            if (RowMatchesMetadata(row, query)) matches.Add(row);
        }
        return matches;
    }

    private static bool RowMatchesMetadata(SessionRowVm row, string query)
    {
        return ContainsIgnoreCase(row.Title, query)
            || ContainsIgnoreCase(row.Preview, query)
            || ContainsIgnoreCase(row.Model, query)
            || ContainsIgnoreCase(row.Source, query);
    }

    private static bool ContainsIgnoreCase(string? haystack, string needle) =>
        !string.IsNullOrEmpty(haystack) &&
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Folds server-side content hits into a list of content-match rows.
    /// Dedupe rule: if a session is already in <paramref name="metadataMatches"/>
    /// it doesn't get a second row in the content group — the metadata
    /// row gets the snippet folded in by <see cref="RebuildGroupsForSearch"/>
    /// instead (so the user sees the matched text under the title without
    /// the section being listed twice). Server preserves rank order;
    /// we preserve it here too.
    /// </summary>
    private List<SessionRowVm> BuildContentMatches(
        List<SessionSearchResult> results,
        List<SessionRowVm> metadataMatches)
    {
        var metadataIds = metadataMatches.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var rowsById = _allRows.ToDictionary(r => r.Id, StringComparer.Ordinal);

        var contentRows = new List<SessionRowVm>(results.Count);
        foreach (var result in results)
        {
            if (metadataIds.Contains(result.SessionId)) continue;

            if (rowsById.TryGetValue(result.SessionId, out var baseRow))
            {
                contentRows.Add(SessionRowVm.WithContentSnippet(baseRow, result.Snippet));
            }
            else
            {
                contentRows.Add(SessionRowVm.FromSearchResult(result));
            }
        }
        return contentRows;
    }

    /// <summary>
    /// Builds a session-id → first-snippet map across <i>all</i> server
    /// hits (including ones that overlap with metadata matches). Used by
    /// the rebuild step to upgrade metadata-match rows with the matched
    /// snippet so the user sees what content matched, not just that the
    /// title matched.
    /// </summary>
    private static Dictionary<string, string> BuildSnippetMap(List<SessionSearchResult> results)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in results)
        {
            // First hit wins — server returns results in rank order, so
            // the first snippet for a given session is the most relevant.
            if (!map.ContainsKey(r.SessionId)) map[r.SessionId] = r.Snippet;
        }
        return map;
    }

    /// <summary>
    /// No-query view: re-bucket every loaded row by date and rebuild the
    /// groups. Mirrors the original sidebar behaviour from before search
    /// went async.
    /// </summary>
    private void ApplyNoQueryView()
    {
        var buckets = _allRows
            .Select(r => (Row: r, Bucket: DateGroupHelper.BucketForEpochSeconds(r.LastActiveEpoch)))
            .GroupBy(t => t.Bucket.SortKey, t => t)
            .OrderByDescending(g => g.Key);

        var freshGroups = new List<SessionGroupVm>();
        int total = 0;
        int open = 0;
        foreach (var group in buckets)
        {
            var header = group.First().Bucket.Header;
            var gvm = new SessionGroupVm(header, group.Key);
            foreach (var t in group.OrderByDescending(t => t.Row.LastActiveEpoch))
            {
                gvm.Items.Add(t.Row);
                total++;
                if (t.Row.IsOpen) open++;
            }
            freshGroups.Add(gvm);
        }

        RebuildGroups(freshGroups);

        if (_allRows.Count == 0)
        {
            Subtitle.Text = "No sessions";
            NoMatchesPanel.Visibility = Visibility.Collapsed;
            SessionList.Visibility = Visibility.Visible;
        }
        else
        {
            Subtitle.Text = $"{total} session{(total == 1 ? "" : "s")} \u00b7 {open} open";
            NoMatchesPanel.Visibility = Visibility.Collapsed;
            SessionList.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Builds the two-group search-mode view. Metadata matches go on top
    /// (date-bucketing dropped here because <c>CollectionViewSource</c>
    /// only supports one level of grouping); content matches go below
    /// in server-rank order. When <paramref name="snippetsById"/> is
    /// non-null, metadata rows whose session also got a server hit are
    /// cloned with the snippet folded in — the icon stays off (title
    /// already explains the match) but the snippet shows up.
    /// <paramref name="searching"/> toggles the "Searching…" subtitle
    /// while the server roundtrip is in flight.
    /// </summary>
    private void RebuildGroupsForSearch(
        string query,
        List<SessionRowVm> metadataMatches,
        List<SessionRowVm> contentMatches,
        Dictionary<string, string>? snippetsById,
        bool searching)
    {
        var freshGroups = new List<SessionGroupVm>();

        if (metadataMatches.Count > 0)
        {
            var metaGroup = new SessionGroupVm("Sessions", MetadataGroupSortKey);
            foreach (var row in metadataMatches.OrderByDescending(r => r.LastActiveEpoch))
            {
                var displayRow = (snippetsById is not null && snippetsById.TryGetValue(row.Id, out var snippet))
                    ? SessionRowVm.WithContentSnippet(row, snippet, SessionMatchKind.Metadata)
                    : row;
                metaGroup.Items.Add(displayRow);
            }
            freshGroups.Add(metaGroup);
        }

        if (contentMatches.Count > 0)
        {
            var contentGroup = new SessionGroupVm("Message matches", ContentGroupSortKey);
            foreach (var row in contentMatches)
            {
                contentGroup.Items.Add(row);
            }
            freshGroups.Add(contentGroup);
        }

        RebuildGroups(freshGroups);

        var totalMatches = metadataMatches.Count + contentMatches.Count;
        if (searching)
        {
            // Early subtitle reflects what we have so far while the server
            // call is still in flight. "Searching…" trumps the count
            // because the count will tick up once content matches arrive.
            Subtitle.Text = totalMatches > 0
                ? $"{totalMatches} match{(totalMatches == 1 ? "" : "es")} \u00b7 searching…"
                : "Searching…";
            NoMatchesPanel.Visibility = Visibility.Collapsed;
            SessionList.Visibility = Visibility.Visible;
        }
        else if (totalMatches == 0)
        {
            Subtitle.Text = $"0 of {_allRows.Count} match \u201c{query}\u201d";
            NoMatchesText.Text = $"No sessions match \u201c{query}\u201d.";
            NoMatchesPanel.Visibility = Visibility.Visible;
            SessionList.Visibility = Visibility.Collapsed;
        }
        else
        {
            var contentDesc = contentMatches.Count > 0
                ? $" ({contentMatches.Count} in messages)"
                : "";
            Subtitle.Text = $"{totalMatches} match{(totalMatches == 1 ? "" : "es")} for \u201c{query}\u201d{contentDesc}";
            NoMatchesPanel.Visibility = Visibility.Collapsed;
            SessionList.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Swaps the entire grouped sidebar over to a new set of groups.
    /// Wrapped in <see cref="_suppressSelectionEvents"/> so the
    /// selection-cleared-during-rebuild noise WinUI fires doesn't
    /// nuke <see cref="_selectedRow"/> and break the Resume button.
    /// </summary>
    private void RebuildGroups(IEnumerable<SessionGroupVm> freshGroups)
    {
        _suppressSelectionEvents = true;
        try
        {
            Groups.Clear();
            foreach (var g in freshGroups) Groups.Add(g);
        }
        finally
        {
            _suppressSelectionEvents = false;
        }
    }

    private async void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is not SessionRowVm row)
        {
            // WinUI fires SelectionChanged with a null SelectedItem when
            // we rebuild Groups under the hood. Treat that as transient —
            // the user hasn't actually deselected, they just typed in
            // the search box. Preserving _selectedRow keeps Resume alive.
            if (!_suppressSelectionEvents) _selectedRow = null;
            return;
        }
        _selectedRow = row;

        // Replace the previous in-flight load. The previous CTS is created
        // with a 10s timeout, which spins up an internal Timer that keeps
        // the CTS rooted until the timeout fires. We have to dispose it
        // explicitly when the user moves on, otherwise rapid selection
        // changes leak one CTS+Timer per click.
        DisposeDetailCts();
        _detailCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = _detailCts.Token;

        EmptyDetail.Visibility = Visibility.Collapsed;
        DetailHeader.Visibility = Visibility.Visible;
        DetailMetaBorder.Visibility = Visibility.Visible;
        MessagesScroll.Visibility = Visibility.Visible;
        ResumeButton.IsEnabled = true;

        DetailTitle.Text = row.Title;
        DetailModel.Text = row.Model ?? "—";
        DetailMessages.Text = (row.MessageCount ?? 0).ToString();
        DetailToolCalls.Text = "—";
        DetailTokens.Text = "—";
        DetailCost.Text = "—";
        Messages.Clear();

        try
        {
            var detailTask = _api.GetSessionAsync(row.Id, ct);
            var messagesTask = _api.GetSessionMessagesAsync(row.Id, ct);
            await Task.WhenAll(detailTask, messagesTask);

            if (detailTask.Result?.Session is { } d)
            {
                // The search-result fallback row doesn't know the real
                // title; once detail comes back, upgrade the header so
                // the user sees the actual conversation name.
                if (!string.IsNullOrWhiteSpace(d.Title)) DetailTitle.Text = d.Title!;
                DetailToolCalls.Text = (d.ToolCallCount ?? 0).ToString();
                var total = (d.InputTokens ?? 0) + (d.OutputTokens ?? 0);
                DetailTokens.Text = total > 0 ? total.ToString("N0") : "—";
                DetailCost.Text = d.EstimatedCostUsd is double c ? $"${c:F4}" : "—";
                if (d.MessageCount is int mc) DetailMessages.Text = mc.ToString();
            }

            if (messagesTask.Result?.Data is { } msgs)
            {
                foreach (var m in msgs) Messages.Add(MessageRowVm.FromMessage(m));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // User selected a different session before this one finished
            // loading. No-op — the new selection will populate the panel.
        }
        catch (Exception ex)
        {
            Messages.Add(new MessageRowVm("system", $"Could not load: {ex.Message}", DateTimeOffset.Now));
        }
    }

    private void DisposeDetailCts()
    {
        var cts = _detailCts;
        if (cts is null) return;
        _detailCts = null;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        cts.Dispose();
    }

    /// <summary>Hands the selected session over to ChatViewModel and switches
    /// the nav to the Chat tab. The await guarantees ChatPage is presented
    /// already-hydrated (no flicker between empty transcript and history).</summary>
    private async void ResumeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRow is not { } row) return;

        ResumeButton.IsEnabled = false;
        try
        {
            await _chat.ResumeSessionAsync(row.Id);
            App.MainWindow?.NavigateToChat();
        }
        catch (Exception ex)
        {
            // ResumeSessionAsync handles its own errors via StatusText, but
            // a thrown exception here means something pre-fetch went wrong.
            Messages.Add(new MessageRowVm("system", $"Could not resume: {ex.Message}", DateTimeOffset.Now));
        }
        finally
        {
            ResumeButton.IsEnabled = true;
        }
    }
}
