using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hermes.ApiClient;
using Hermes.ApiClient.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;

namespace Hermes.App.Pages;

public sealed partial class HomePage : Page
{
    private readonly HermesApiClient _api;
    private CancellationTokenSource? _inflight;

    public HomePage()
    {
        InitializeComponent();
        _api = App.Services.GetRequiredService<HermesApiClient>();

        GatewayUrlText.Text = _api.Config.BaseAddress.ToString();
        ConfigDirText.Text = _api.Config.ConfigDirectory ?? "(not found)";
        ApiKeyText.Text = string.IsNullOrEmpty(_api.Config.ApiKey)
            ? "(none)"
            : MaskKey(_api.Config.ApiKey);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await RefreshAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    /// <summary>
    /// Hits /health/detailed, /v1/models, and /api/sessions in parallel and
    /// updates the dashboard. Cancels any prior in-flight refresh so rapid
    /// button mashing can't pile up requests or stomp on the UI out of order.
    /// </summary>
    private async Task RefreshAsync()
    {
        _inflight?.Cancel();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _inflight = cts;

        try
        {
            var healthTask = SafeAsync(() => _api.GetDetailedHealthAsync(cts.Token));
            var modelsTask = SafeAsync(() => _api.GetModelsAsync(cts.Token));
            var sessionsTask = SafeAsync(() => _api.GetSessionsAsync(50, true, cts.Token));

            await Task.WhenAll(healthTask, modelsTask, sessionsTask);

            if (cts.IsCancellationRequested) return;

            var health = healthTask.Result;
            var models = modelsTask.Result;
            var sessions = sessionsTask.Result;

            ApplySnapshot(health, models, sessions);
        }
        finally
        {
            if (_inflight == cts) _inflight = null;
            cts.Dispose();
        }
    }

    private void ApplySnapshot(DetailedHealth? health, ModelList? models, SessionList? sessions)
    {
        // Status dot + headline
        var allReachable = health is not null;
        var gatewayRunning = string.Equals(health?.GatewayState, "running", StringComparison.OrdinalIgnoreCase);
        var anyPlatformError = health?.Platforms?.Values.Any(p =>
            !string.Equals(p.State, "connected", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(p.State, "disabled", StringComparison.OrdinalIgnoreCase)) ?? false;

        (Color color, string label) = (allReachable, gatewayRunning, anyPlatformError) switch
        {
            (false, _, _) => (Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C), "Gateway unreachable"),
            (true, false, _) => (Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C), $"Gateway {health!.GatewayState}"),
            (true, true, true) => (Color.FromArgb(0xFF, 0xF7, 0x63, 0x0C), "Degraded — platform error"),
            (true, true, false) => (Color.FromArgb(0xFF, 0x10, 0x88, 0x3E), "Healthy"),
        };
        StatusDot.Fill = new SolidColorBrush(color);
        StatusText.Text = label;
        ErrorBar.IsOpen = !allReachable;

        // Cards
        GatewayStateText.Text = health?.GatewayState ?? "unreachable";
        ActiveAgentsText.Text = health?.ActiveAgents.ToString() ?? "—";

        var openCount = sessions?.Data?.Count(s => s.IsOpen) ?? 0;
        OpenSessionsText.Text = openCount.ToString();

        ModelText.Text = models?.Data is { Count: > 0 } d
            ? d[0].Id ?? "(unnamed)"
            : (_api.Config.ModelName ?? "—");
    }

    private static async Task<T?> SafeAsync<T>(Func<Task<T?>> call)
    {
        try { return await call(); }
        catch { return default; }
    }

    private static string MaskKey(string key) =>
        key.Length <= 8 ? new string('•', key.Length)
                       : $"{key[..4]}…{key[^4..]}  ({key.Length} chars)";
}
