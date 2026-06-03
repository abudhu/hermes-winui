using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using Hermes.ApiClient;
using Hermes.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace Hermes.App.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly HermesApiClient _api;
    public ObservableCollection<FeatureFlagVm> Features { get; } = [];

    // Initial / persisted values, captured on page load. Dirty state is
    // whatever the current TextBoxes hold compared against these.
    private string _originalHost = "";
    private int _originalPort;
    private string _originalApiKey = "";
    private string _originalModel = "";

    // Optimistic-concurrency token for EnvFileWriter — if .env changes
    // outside the app between page-load and Save, we'll catch it.
    private DateTime? _envSnapshot;

    // True while we're mutating boxes programmatically (initial load,
    // Revert, post-Save resync) so Field_Changed doesn't false-positive
    // "user dirtied the form" and re-arm the Save button.
    private bool _suppressDirtyTracking;

    public SettingsPage()
    {
        _api = App.Services.GetRequiredService<HermesApiClient>();
        InitializeComponent();

        LoadInitialValues();
        VersionText.Text = $"v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?"}";
    }

    private void LoadInitialValues()
    {
        _suppressDirtyTracking = true;
        try
        {
            _originalHost = _api.Config.Host;
            _originalPort = _api.Config.Port;
            _originalApiKey = _api.Config.ApiKey ?? "";
            _originalModel = _api.Config.ModelName;

            HostBox.Text = _originalHost;
            PortBox.Value = _originalPort;
            ModelBox.Text = _originalModel;
            ApiKeyBox.Password = _originalApiKey;
            ConfigDirText.Text = _api.Config.ConfigDirectory ?? "(not found)";

            _envSnapshot = EnvFileWriter.GetSnapshotStamp(_api.Config.EnvFilePath);
            UpdateApiKeyHint(_originalApiKey);
            UpdateDirtyState();
        }
        finally
        {
            _suppressDirtyTracking = false;
        }
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

    // ---- dirty tracking -----------------------------------------------------

    private void Field_Changed(object sender, object e)
    {
        if (_suppressDirtyTracking) return;
        UpdateDirtyState();
    }

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressDirtyTracking) return;
        UpdateApiKeyHint(ApiKeyBox.Password);
        UpdateDirtyState();
    }

    private bool IsDirty()
    {
        return HostBox.Text != _originalHost
            || (int)Math.Round(PortBox.Value) != _originalPort
            || ModelBox.Text != _originalModel
            || ApiKeyBox.Password != _originalApiKey;
    }

    private void UpdateDirtyState()
    {
        var dirty = IsDirty();
        SaveButton.IsEnabled = dirty;
        RevertButton.IsEnabled = dirty;
        DirtyHint.Text = dirty ? "Unsaved changes" : "";
    }

    private void UpdateApiKeyHint(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            ApiKeyHint.Text = "No key set — gateway will accept anonymous requests.";
        }
        else
        {
            ApiKeyHint.Text = $"{key.Length} chars";
        }
    }

    // ---- key generation -----------------------------------------------------

    private void GenerateKey_Click(object sender, RoutedEventArgs e)
    {
        // 32 random bytes → ~44 chars base64; well above the 16-char minimum
        // the writer enforces and below the 256-char ceiling. Use URL-safe
        // base64 so the value never contains '+', '/', '=' that could trip
        // up downstream consumers parsing the .env differently.
        var bytes = RandomNumberGenerator.GetBytes(32);
        var b64 = Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        ApiKeyBox.Password = b64;  // triggers PasswordChanged → dirty
    }

    private void CopyKey_Click(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyBox.Password;
        if (string.IsNullOrEmpty(key)) return;
        var data = new DataPackage();
        data.SetText(key);
        Clipboard.SetContent(data);

        ShowStatus(InfoBarSeverity.Informational, "Copied", "API key copied to clipboard.");
    }

    // ---- save / revert ------------------------------------------------------

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        LoadInitialValues();
        ShowStatus(InfoBarSeverity.Informational, "Reverted",
            "Form reset to values currently saved on disk.");
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text?.Trim() ?? "";
        var port = ((int)Math.Round(PortBox.Value)).ToString();
        var model = ModelBox.Text?.Trim() ?? "";
        var apiKey = ApiKeyBox.Password ?? "";

        // Validate per-field so we can point the user at the exact box.
        if (!EnvFileWriter.IsValid("API_SERVER_HOST", host))
        {
            ShowStatus(InfoBarSeverity.Error, "Invalid host",
                "Host must be a hostname or IP using letters, digits, dots, dashes, or underscores.");
            HostBox.Focus(FocusState.Programmatic);
            return;
        }
        if (!EnvFileWriter.IsValid("API_SERVER_PORT", port))
        {
            ShowStatus(InfoBarSeverity.Error, "Invalid port",
                "Port must be between 1 and 65535.");
            PortBox.Focus(FocusState.Programmatic);
            return;
        }
        if (!EnvFileWriter.IsValid("API_SERVER_MODEL_NAME", model))
        {
            ShowStatus(InfoBarSeverity.Error, "Invalid model name",
                "Model may contain letters, digits, dots, dashes, underscores, colons, or slashes.");
            ModelBox.Focus(FocusState.Programmatic);
            return;
        }
        if (!EnvFileWriter.IsValid("API_SERVER_KEY", apiKey))
        {
            ShowStatus(InfoBarSeverity.Error, "Invalid API key",
                "Key must be empty or 16-256 characters using base64 / base64url charset.");
            ApiKeyBox.Focus(FocusState.Programmatic);
            return;
        }

        var updates = new List<EnvFileWriter.ManagedValue>
        {
            new("API_SERVER_HOST", host),
            new("API_SERVER_PORT", port),
            new("API_SERVER_MODEL_NAME", model),
            new("API_SERVER_KEY", apiKey),
        };

        try
        {
            var result = EnvFileWriter.Save(_api.Config.EnvFilePath, updates, _envSnapshot);
            if (result == EnvFileWriter.SaveStatus.ConcurrentExternalEdit)
            {
                ShowStatus(InfoBarSeverity.Warning, "Conflict",
                    ".env was modified outside this app since you opened Settings. " +
                    "Click Revert to reload current values, then re-apply your changes.");
                return;
            }

            // Successful save. Resync "original" baseline + snapshot so the
            // Save button disarms and Revert won't ping-pong to old values.
            _originalHost = host;
            _originalPort = int.Parse(port);
            _originalModel = model;
            _originalApiKey = apiKey;
            _envSnapshot = EnvFileWriter.GetSnapshotStamp(_api.Config.EnvFilePath);
            UpdateDirtyState();

            ShowStatus(InfoBarSeverity.Success, "Saved",
                "Settings written to .env. Restart the Hermes gateway " +
                "and this app to apply the new values.");
        }
        catch (ArgumentException ex)
        {
            // Defensive: validation above should catch this.
            ShowStatus(InfoBarSeverity.Error, "Invalid value", ex.Message);
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Could not save", ex.Message);
        }
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        SaveStatusBar.Severity = severity;
        SaveStatusBar.Title = title;
        SaveStatusBar.Message = message;
        SaveStatusBar.IsOpen = true;
    }
}
