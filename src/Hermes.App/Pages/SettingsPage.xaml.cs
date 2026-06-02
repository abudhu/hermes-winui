using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Hermes.ApiClient;
using Hermes.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Hermes.App.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly HermesApiClient _api;
    public ObservableCollection<FeatureFlagVm> Features { get; } = [];

    public SettingsPage()
    {
        _api = App.Services.GetRequiredService<HermesApiClient>();
        InitializeComponent();

        BaseUrlText.Text = _api.Config.BaseAddress.ToString();
        ModelText.Text = _api.Config.ModelName ?? "—";
        ConfigDirText.Text = _api.Config.ConfigDirectory ?? "(not found)";
        ApiKeyText.Text = string.IsNullOrEmpty(_api.Config.ApiKey)
            ? "(none)"
            : MaskKey(_api.Config.ApiKey);
        VersionText.Text = $"v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?"}";
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        try
        {
            var caps = await _api.GetCapabilitiesAsync(CancellationToken.None);
            Features.Clear();
            if (caps?.Features is null)
            {
                CapsStatus.Text = "No capabilities returned by the gateway.";
                return;
            }
            foreach (var kv in caps.Features.OrderBy(kv => kv.Key))
            {
                Features.Add(FeatureFlagVm.From(kv.Key, kv.Value));
            }
            CapsStatus.Text = $"{Features.Count(f => f.IsEnabled)} of {Features.Count} features enabled on the gateway.";
        }
        catch (Exception ex)
        {
            CapsStatus.Text = $"Could not load capabilities: {ex.Message}";
        }
    }

    private void OpenConfig_Click(object sender, RoutedEventArgs e)
    {
        var dir = _api.Config.ConfigDirectory;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        try { Process.Start("explorer.exe", $"\"{dir}\""); } catch { }
    }

    private static string MaskKey(string key) =>
        key.Length <= 8 ? new string('•', key.Length)
                       : $"{key[..4]}…{key[^4..]}  ({key.Length} chars)";
}
