using System;
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
    private readonly HermesApiClient _api;
    private readonly ChatViewModel _chat;
    private CancellationTokenSource? _detailCts;
    private SessionRowVm? _selectedRow;

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
        // callbacks) for up to 10s after navigation.
        Unloaded += (_, _) => DisposeDetailCts();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        Subtitle.Text = "Loading…";
        try
        {
            var sessionsResponse = await _api.GetSessionsAsync(50, true, CancellationToken.None);
            _allRows.Clear();
            if (sessionsResponse?.Data is null)
            {
                Groups.Clear();
                Subtitle.Text = "No sessions";
                ApplyFilter();
                return;
            }

            foreach (var s in sessionsResponse.Data)
            {
                _allRows.Add(SessionRowVm.FromSummary(s));
            }

            ApplyFilter();
        }
        catch (Exception ex)
        {
            Subtitle.Text = $"Error: {ex.Message}";
        }
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // Only filter on user keystrokes — ignore programmatic text changes
        // (e.g. the box clearing itself after selection).
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        ApplyFilter();
    }

    /// <summary>
    /// Rebuilds the grouped <see cref="Groups"/> view from <see cref="_allRows"/>,
    /// optionally filtered by the SearchBox text. Match is case-insensitive
    /// on Title and Preview. Empty groups self-hide via the GroupStyle's
    /// <c>HidesIfEmpty="True"</c>.
    /// </summary>
    private void ApplyFilter()
    {
        var query = SearchBox.Text?.Trim() ?? "";
        var hasQuery = query.Length > 0;

        IEnumerable<SessionRowVm> matched = hasQuery
            ? _allRows.Where(r => MatchesQuery(r, query))
            : _allRows;
        var matchedList = matched.ToList();

        Groups.Clear();
        var bucketed = matchedList
            .Select(r => (Row: r, Bucket: DateGroupHelper.BucketForEpochSeconds(r.LastActiveEpoch)))
            .GroupBy(t => t.Bucket.SortKey, t => t)
            .OrderByDescending(g => g.Key);

        int total = 0;
        int open = 0;
        foreach (var group in bucketed)
        {
            var header = group.First().Bucket.Header;
            var gvm = new SessionGroupVm(header, group.Key);
            foreach (var t in group.OrderByDescending(t => t.Row.LastActiveEpoch))
            {
                gvm.Items.Add(t.Row);
                total++;
                if (t.Row.IsOpen) open++;
            }
            Groups.Add(gvm);
        }

        // Subtitle + empty-state messaging
        if (_allRows.Count == 0)
        {
            Subtitle.Text = "No sessions";
            NoMatchesPanel.Visibility = Visibility.Collapsed;
            SessionList.Visibility = Visibility.Visible;
        }
        else if (hasQuery && total == 0)
        {
            Subtitle.Text = $"0 of {_allRows.Count} match \u201c{query}\u201d";
            NoMatchesText.Text = $"No sessions match \u201c{query}\u201d.";
            NoMatchesPanel.Visibility = Visibility.Visible;
            SessionList.Visibility = Visibility.Collapsed;
        }
        else if (hasQuery)
        {
            Subtitle.Text = $"{total} of {_allRows.Count} match \u201c{query}\u201d";
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

    private static bool MatchesQuery(SessionRowVm row, string query)
    {
        // Case-insensitive contains on the most useful fields.
        return Contains(row.Title, query)
            || Contains(row.Preview, query)
            || Contains(row.Model, query)
            || Contains(row.Source, query);
    }

    private static bool Contains(string? haystack, string needle) =>
        !string.IsNullOrEmpty(haystack) &&
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private async void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is not SessionRowVm row)
        {
            _selectedRow = null;
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
                DetailToolCalls.Text = (d.ToolCallCount ?? 0).ToString();
                var total = (d.InputTokens ?? 0) + (d.OutputTokens ?? 0);
                DetailTokens.Text = total > 0 ? total.ToString("N0") : "—";
                DetailCost.Text = d.EstimatedCostUsd is double c ? $"${c:F4}" : "—";
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

