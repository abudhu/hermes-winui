using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hermes.ApiClient;
using Hermes.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Hermes.App.Pages;

public sealed partial class BridgesPage : Page
{
    private readonly HermesApiClient _api;
    public ObservableCollection<BridgeCardVm> Bridges { get; } = [];

    public BridgesPage()
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
            var health = await _api.GetDetailedHealthAsync(CancellationToken.None);
            Bridges.Clear();

            var platformsByName = health?.Platforms ?? new();
            foreach (var kv in platformsByName.OrderBy(kv => kv.Key))
            {
                Bridges.Add(BridgeCardVm.From(kv.Key, kv.Value));
            }

            Subtitle.Text = Bridges.Count switch
            {
                0 => "No platform bridges configured",
                1 => "1 bridge",
                _ => $"{Bridges.Count} bridges",
            };
            EmptyState.Visibility = Bridges.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ListScroll.Visibility = Bridges.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex)
        {
            Subtitle.Text = $"Error: {ex.Message}";
            EmptyState.Visibility = Visibility.Visible;
            ListScroll.Visibility = Visibility.Collapsed;
        }
    }
}
