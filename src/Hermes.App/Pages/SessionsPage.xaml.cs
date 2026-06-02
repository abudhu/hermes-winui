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
    private CancellationTokenSource? _detailCts;

    public ObservableCollection<SessionRowVm> Sessions { get; } = [];
    public ObservableCollection<MessageRowVm> Messages { get; } = [];

    public SessionsPage()
    {
        _api = App.Services.GetRequiredService<HermesApiClient>();
        InitializeComponent();
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
            Sessions.Clear();
            if (list?.Data is null) { Subtitle.Text = "No sessions"; return; }

            foreach (var s in list.Data.OrderByDescending(s => s.LastActive ?? 0))
            {
                Sessions.Add(SessionRowVm.FromSummary(s));
            }
            var openCount = Sessions.Count(s => s.IsOpen);
            Subtitle.Text = $"{Sessions.Count} session{(Sessions.Count == 1 ? "" : "s")} · {openCount} open";
        }
        catch (Exception ex)
        {
            Subtitle.Text = $"Error: {ex.Message}";
        }
    }

    private async void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is not SessionRowVm row) return;

        _detailCts?.Cancel();
        _detailCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = _detailCts.Token;

        EmptyDetail.Visibility = Visibility.Collapsed;
        DetailHeader.Visibility = Visibility.Visible;
        DetailMetaBorder.Visibility = Visibility.Visible;
        MessagesScroll.Visibility = Visibility.Visible;

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
}
