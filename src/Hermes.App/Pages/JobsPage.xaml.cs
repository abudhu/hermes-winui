using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Hermes.ApiClient;
using Hermes.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Hermes.App.Pages;

public sealed partial class JobsPage : Page
{
    private readonly HermesApiClient _api;
    public ObservableCollection<JobRowVm> Jobs { get; } = [];

    public JobsPage()
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
            var list = await _api.GetJobsAsync(CancellationToken.None);
            Jobs.Clear();
            foreach (var j in list?.Jobs ?? [])
            {
                Jobs.Add(JobRowVm.FromJob(j));
            }
            Subtitle.Text = $"{Jobs.Count} job{(Jobs.Count == 1 ? "" : "s")}";
            EmptyState.Visibility = Jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ListScroll.Visibility = Jobs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex)
        {
            Subtitle.Text = $"Error: {ex.Message}";
        }
    }
}
