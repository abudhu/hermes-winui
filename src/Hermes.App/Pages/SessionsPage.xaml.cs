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

    public SessionsPage()
    {
        _api = App.Services.GetRequiredService<HermesApiClient>();
        _chat = App.Services.GetRequiredService<ChatViewModel>();
        InitializeComponent();

        // Wire CollectionViewSource → grouped collection here (rather than
        // in XAML via x:Bind) so the binding evaluates exactly once after
        // the page is initialised, with the live ObservableCollection.
        GroupedSessionsSource.Source = Groups;
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
            var list = await _api.GetSessionsAsync(50, true, CancellationToken.None);
            Groups.Clear();
            if (list?.Data is null) { Subtitle.Text = "No sessions"; return; }

            // Bucket each session by last-active date, then sort groups by
            // SortKey descending so Today lands at the top. Within each group
            // sessions are ordered most-recently-active first.
            var bucketed = list.Data
                .Select(s => (
                    Summary: s,
                    Bucket: DateGroupHelper.BucketForEpochSeconds(s.LastActive ?? s.StartedAt)))
                .GroupBy(t => t.Bucket.SortKey, t => t)
                .OrderByDescending(g => g.Key)
                .ToList();

            int total = 0;
            int open = 0;
            foreach (var group in bucketed)
            {
                var label = group.First().Bucket.Header;
                var gvm = new SessionGroupVm(label, group.Key);
                foreach (var t in group.OrderByDescending(t => t.Summary.LastActive ?? 0))
                {
                    var row = SessionRowVm.FromSummary(t.Summary);
                    gvm.Items.Add(row);
                    total++;
                    if (row.IsOpen) open++;
                }
                Groups.Add(gvm);
            }

            Subtitle.Text = $"{total} session{(total == 1 ? "" : "s")} · {open} open";
        }
        catch (Exception ex)
        {
            Subtitle.Text = $"Error: {ex.Message}";
        }
    }

    private async void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is not SessionRowVm row)
        {
            _selectedRow = null;
            return;
        }
        _selectedRow = row;

        _detailCts?.Cancel();
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
        catch (Exception ex)
        {
            Messages.Add(new MessageRowVm("system", $"Could not load: {ex.Message}", DateTimeOffset.Now));
        }
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

