using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hermes.ApiClient;
using Hermes.ApiClient.Models;
using Hermes.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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

    // Status indicator state — cancelled and re-issued on every probe so
    // a slow refresh can't stomp a faster one.
    private CancellationTokenSource? _statusCts;

    private enum StatusKind { Loading, Connected, Degraded, Unreachable }

    // ---- Diagnostics pane state -------------------------------------------
    //
    // Diagnostics loads lazily — the first navigation to the pane (or
    // an explicit Refresh click) triggers LoadDiagnosticsAsync. The
    // _loading flag prevents concurrent refreshes (button + nav race);
    // the _loaded flag stops re-navigation from re-probing every time.

    private bool _diagnosticsLoaded;
    private bool _diagnosticsLoading;

    /// <summary>Platform bridges list backing the
    /// <c>PlatformBridges</c> ItemsRepeater in the Diagnostics pane.
    /// One entry per platform key from <c>/health/detailed.platforms</c>.</summary>
    public ObservableCollection<PlatformBridgeVm> PlatformBridgesList { get; } = [];

    public SettingsPage()
    {
        _api = App.Services.GetRequiredService<HermesApiClient>();
        InitializeComponent();

        PlatformBridges.ItemsSource = PlatformBridgesList;
        LoadInitialValues();
        ApplyBuildInfo();
    }

    /// <summary>Wires the About pane's version + commit display.
    /// Commit link is hidden gracefully when the build wasn't run from
    /// a git checkout (no SHA embedded by the SDK).</summary>
    private void ApplyBuildInfo()
    {
        VersionText.Text = $"v{BuildInfo.Version}";

        var sha = BuildInfo.CommitSha;
        if (sha is not null && BuildInfo.CommitUrl is string url)
        {
            CommitText.Text = BuildInfo.ShortCommit!;
            CommitLink.NavigateUri = new Uri(url);
            CommitLink.IsEnabled = true;
            ToolTipService.SetToolTip(CommitLink, sha);
        }
        else
        {
            CommitText.Text = "unknown";
            CommitLink.IsEnabled = false;
            ToolTipService.SetToolTip(CommitLink,
                "No commit SHA embedded — this build wasn't packaged from a git checkout.");
        }
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

        // Connection-status probe is cheap and always relevant.
        // Capabilities + gateway probe live in Diagnostics and load
        // lazily when the user navigates there (see NavRail_SelectionChanged).
        await RefreshConnectionStatusAsync();
    }

    // ---- connection status --------------------------------------------------

    private async void ProbeStatus_Click(object sender, RoutedEventArgs e)
        => await RefreshConnectionStatusAsync();

    /// <summary>
    /// Pings <c>/health/detailed</c> on the persisted gateway config (the
    /// running HermesApiClient — not whatever's in the form right now)
    /// and updates the status dot. After a successful Save the persisted
    /// HttpClients still hold the old BaseAddress / bearer, which is what
    /// we want: the indicator reflects the gateway the running app would
    /// hit, not future state.
    /// </summary>
    private async Task RefreshConnectionStatusAsync()
    {
        _statusCts?.Cancel();
        _statusCts = new CancellationTokenSource();
        var ct = _statusCts.Token;

        SetStatus(StatusKind.Loading, "Probing gateway…", null);
        try
        {
            var health = await _api.GetDetailedHealthAsync(ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;

            if (health is null)
            {
                SetStatus(StatusKind.Unreachable, "No response",
                    $"Gateway at {_api.Config.BaseAddress} returned an empty response.");
                return;
            }

            var healthy = string.Equals(health.Status, "healthy", StringComparison.OrdinalIgnoreCase);
            var agentText = health.ActiveAgents switch
            {
                0 => "no active agents",
                1 => "1 active agent",
                _ => $"{health.ActiveAgents} active agents",
            };

            if (healthy)
            {
                SetStatus(StatusKind.Connected,
                    $"Connected to {_api.Config.Host}:{_api.Config.Port}",
                    $"Gateway healthy, {agentText}.");
            }
            else
            {
                SetStatus(StatusKind.Degraded,
                    $"Reachable but {health.Status ?? "unknown"}",
                    $"Gateway state: {health.GatewayState ?? "unknown"}, {agentText}.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Superseded by a newer probe — leave the new probe to update UI.
        }
        catch (HttpRequestException ex)
        {
            SetStatus(StatusKind.Unreachable, "Unreachable",
                $"Could not reach {_api.Config.BaseAddress} — {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            SetStatus(StatusKind.Unreachable, "Timed out",
                $"Gateway at {_api.Config.BaseAddress} did not respond within the 6s deadline.");
        }
        catch (Exception ex)
        {
            SetStatus(StatusKind.Unreachable, "Error", ex.Message);
        }
    }

    private void SetStatus(StatusKind kind, string text, string? detail)
    {
        var brushKey = kind switch
        {
            StatusKind.Connected   => "SystemFillColorSuccessBrush",
            StatusKind.Degraded    => "SystemFillColorCautionBrush",
            StatusKind.Unreachable => "SystemFillColorCriticalBrush",
            _                      => "TextFillColorDisabledBrush",
        };
        StatusDot.Fill = (Brush)Application.Current.Resources[brushKey];
        StatusText.Text = text;
        StatusDetail.Text = detail ?? "";
        StatusDetail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- test connection (uses form values, NOT persisted config) -----------

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text?.Trim() ?? "";
        var port = (int)Math.Round(PortBox.Value);
        var key  = ApiKeyBox.Password ?? "";

        if (!EnvFileWriter.IsValid("API_SERVER_HOST", host))
        {
            ShowStatus(InfoBarSeverity.Error, "Invalid host",
                "Host must be a hostname or IP using letters, digits, dots, dashes, or underscores.");
            HostBox.Focus(FocusState.Programmatic);
            return;
        }
        if (port < 1 || port > 65535)
        {
            ShowStatus(InfoBarSeverity.Error, "Invalid port", "Port must be between 1 and 65535.");
            PortBox.Focus(FocusState.Programmatic);
            return;
        }

        TestButton.IsEnabled = false;
        try
        {
            using var http = new HttpClient
            {
                BaseAddress = new Uri($"http://{host}:{port}/"),
                Timeout = TimeSpan.FromSeconds(6),
            };
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrWhiteSpace(key))
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }

            using var resp = await http.GetAsync("/health/detailed");
            if (resp.IsSuccessStatusCode)
            {
                ShowStatus(InfoBarSeverity.Success, "Test passed",
                    $"Gateway at {host}:{port} responded successfully.");
            }
            else if ((int)resp.StatusCode is 401 or 403)
            {
                ShowStatus(InfoBarSeverity.Error, "Auth rejected",
                    $"Gateway at {host}:{port} returned {(int)resp.StatusCode} {resp.ReasonPhrase}. " +
                    "Check the API key.");
            }
            else
            {
                ShowStatus(InfoBarSeverity.Warning, "Test failed",
                    $"Gateway at {host}:{port} returned {(int)resp.StatusCode} {resp.ReasonPhrase}.");
            }
        }
        catch (TaskCanceledException)
        {
            ShowStatus(InfoBarSeverity.Error, "Test timed out",
                $"Gateway at {host}:{port} did not respond within 6 seconds.");
        }
        catch (HttpRequestException ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Test failed",
                $"Could not reach {host}:{port} — {ex.Message}");
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Test failed", ex.Message);
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    // ---- section selector ---------------------------------------------------

    private void NavRail_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // First-load guard — the ListView may fire selection events before
        // x:Bind has wired up the panes (when IsSelected="True" on the
        // default item).
        if (ConnectionPane is null || McpPaneHost is null
            || DiagnosticsPane is null || AboutPane is null) return;

        var selected = NavRail.SelectedItem;
        ConnectionPane.Visibility  = ReferenceEquals(selected, ConnectionNavItem)  ? Visibility.Visible : Visibility.Collapsed;
        McpPaneHost.Visibility     = ReferenceEquals(selected, McpNavItem)         ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPane.Visibility = ReferenceEquals(selected, DiagnosticsNavItem) ? Visibility.Visible : Visibility.Collapsed;
        AboutPane.Visibility       = ReferenceEquals(selected, AboutNavItem)       ? Visibility.Visible : Visibility.Collapsed;

        if (ReferenceEquals(selected, DiagnosticsNavItem) && !_diagnosticsLoaded)
        {
            _ = LoadDiagnosticsAsync();
        }
    }

    /// <summary>True if EITHER the gateway form OR the MCP editor has
    /// unsaved changes. Exposed for the top-level navigation guard.</summary>
    public bool HasUnsavedChanges => IsDirty() || McpPane.IsEditorDirty;

    private void OpenConfig_Click(object sender, RoutedEventArgs e)
    {
        var dir = _api.Config.ConfigDirectory;
        if (string.IsNullOrEmpty(dir))
        {
            ShowStatus(InfoBarSeverity.Warning, "No config directory",
                "Hermes hasn't told us where its config lives yet — start the gateway and try again.");
            return;
        }
        if (!Directory.Exists(dir))
        {
            ShowStatus(InfoBarSeverity.Warning, "Config directory missing",
                $"Expected {dir} but it doesn't exist on disk.");
            return;
        }
        try
        {
            Process.Start("explorer.exe", $"\"{dir}\"");
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Couldn't open folder",
                $"Explorer refused to open {dir}: {ex.Message}");
        }
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

    // ---- Diagnostics pane --------------------------------------------------
    //
    // Three independent data sources: gateway.pid file probe (PID +
    // kind + argv + start time approximation), /health/detailed
    // (state + active agents + platform bridges + exit reason), and
    // /capabilities (feature matrix). Each is wrapped in its own
    // try/catch so a single failure surfaces inline without nuking
    // the whole pane — important precisely when one of them is broken.

    private void RefreshDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        // Force-reload on explicit refresh, even if previously loaded.
        _diagnosticsLoaded = false;
        _ = LoadDiagnosticsAsync();
    }

    private async Task LoadDiagnosticsAsync()
    {
        if (_diagnosticsLoading) return;
        _diagnosticsLoading = true;
        DiagnosticsRefreshButton.IsEnabled = false;
        try
        {
            // Log path — synchronous + cheap, do it first so the row
            // populates even if everything else times out.
            UpdateLogPathLabel();

            // Probe everything in parallel. Per-source error handling
            // keeps a single failure from clobbering the others.
            var probeTask = Task.Run(() => HermesGatewayProbe.Probe(_api.Config.ConfigDirectory));
            var healthTask = TryFetchAsync(_api.GetDetailedHealthAsync);
            var capsTask = TryFetchAsync(ct => _api.GetCapabilitiesAsync(ct));

            HermesGatewayInfo? probe = null;
            try { probe = await probeTask; }
            catch (Exception ex) { ShowDiagStatus(InfoBarSeverity.Warning, "PID file unreadable", ex.Message); }

            var healthResult = await healthTask;
            var capsResult = await capsTask;

            ApplyGatewayInfo(probe, healthResult.Value);
            ApplyPlatformBridges(healthResult.Value);
            ApplyCapabilities(capsResult);

            // If any source errored, surface a friendly summary —
            // detail is already visible in the per-section labels.
            var errors = new List<string>();
            if (healthResult.Error is not null) errors.Add($"/health/detailed: {healthResult.Error}");
            if (capsResult.Error is not null) errors.Add($"/capabilities: {capsResult.Error}");
            if (errors.Count > 0)
            {
                ShowDiagStatus(InfoBarSeverity.Warning,
                    "Some diagnostics unavailable",
                    string.Join(Environment.NewLine, errors));
            }
            else
            {
                DiagnosticsStatusBar.IsOpen = false;
            }

            _diagnosticsLoaded = true;
        }
        finally
        {
            _diagnosticsLoading = false;
            DiagnosticsRefreshButton.IsEnabled = true;
        }
    }

    /// <summary>Wraps an async fetch into a result-style tuple so the
    /// caller can apply each source independently. Avoids
    /// <c>Task.WhenAll</c>'s "one throw discards the rest" behaviour.</summary>
    private static async Task<(T? Value, string? Error)> TryFetchAsync<T>(
        Func<CancellationToken, Task<T?>> fetch)
        where T : class
    {
        try
        {
            var v = await fetch(CancellationToken.None);
            return (v, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private void ApplyGatewayInfo(HermesGatewayInfo? probe, DetailedHealth? health)
    {
        // State
        if (health is not null)
        {
            GatewayStateText.Text = string.IsNullOrWhiteSpace(health.GatewayState)
                ? (health.Status ?? "unknown")
                : health.GatewayState;
        }
        else if (probe is not null)
        {
            GatewayStateText.Text = "(unable to reach /health/detailed; PID file present)";
        }
        else
        {
            GatewayStateText.Text = "not running";
        }

        // PID — prefer the one from the live health endpoint over the
        // one in gateway.pid; they should agree, but the live one is
        // more authoritative if Hermes restarted without rewriting the file.
        var pid = health?.Pid ?? probe?.Pid;
        GatewayPidText.Text = pid?.ToString() ?? "—";

        // Kind / argv come from gateway.pid only.
        GatewayKindText.Text = string.IsNullOrEmpty(probe?.Kind) ? "—" : probe.Kind;

        // Uptime: prefer Process.GetProcessById(pid).StartTime when the
        // PID is alive and the process kind looks right; fall back to
        // gateway.pid file mtime; show "unknown" if neither works.
        GatewayUptimeText.Text = ComputeUptime(pid, _api.Config.ConfigDirectory);

        GatewayActiveAgentsText.Text = health?.ActiveAgents.ToString() ?? "—";
        GatewayUpdatedText.Text = string.IsNullOrEmpty(health?.UpdatedAt) ? "—" : health.UpdatedAt!;

        if (!string.IsNullOrWhiteSpace(health?.ExitReason))
        {
            GatewayExitReasonLabel.Visibility = Visibility.Visible;
            GatewayExitReasonText.Visibility = Visibility.Visible;
            GatewayExitReasonText.Text = health.ExitReason!;
        }
        else
        {
            GatewayExitReasonLabel.Visibility = Visibility.Collapsed;
            GatewayExitReasonText.Visibility = Visibility.Collapsed;
            GatewayExitReasonText.Text = "";
        }
    }

    private void ApplyPlatformBridges(DetailedHealth? health)
    {
        PlatformBridgesList.Clear();
        if (health?.Platforms is null || health.Platforms.Count == 0)
        {
            PlatformBridgesCard.Visibility = Visibility.Collapsed;
            return;
        }
        foreach (var kv in health.Platforms.OrderBy(kv => kv.Key))
        {
            PlatformBridgesList.Add(PlatformBridgeVm.From(
                kv.Key, kv.Value.State, kv.Value.ErrorCode, kv.Value.ErrorMessage));
        }
        PlatformBridgesCard.Visibility = Visibility.Visible;
    }

    private void ApplyCapabilities((Hermes.ApiClient.Models.Capabilities? Value, string? Error) result)
    {
        Features.Clear();
        if (result.Error is not null)
        {
            CapsStatus.Text = $"Could not load capabilities: {result.Error}";
            return;
        }
        var caps = result.Value;
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

    /// <summary>Best-effort gateway uptime. Tries the live process
    /// first (most accurate), then the gateway.pid file mtime, then
    /// gives up.</summary>
    private static string ComputeUptime(int? pid, string? configDir)
    {
        if (pid is int p && p > 0)
        {
            try
            {
                using var proc = Process.GetProcessById(p);
                // StartTime is local-time; Now is also local — both DateTime, same kind.
                var dur = DateTime.Now - proc.StartTime;
                if (dur.TotalSeconds >= 0)
                    return FormatDuration(dur);
            }
            catch
            {
                // Process gone, no perms, mismatched arch — fall through.
            }
        }

        if (!string.IsNullOrWhiteSpace(configDir))
        {
            try
            {
                var path = Path.Combine(configDir, HermesGatewayProbe.PidFileName);
                if (File.Exists(path))
                {
                    var dur = DateTime.Now - File.GetLastWriteTime(path);
                    if (dur.TotalSeconds >= 0)
                        return $"≈ {FormatDuration(dur)} (from pid-file mtime)";
                }
            }
            catch
            {
                // Permissions error / I/O race — same fall-through.
            }
        }

        return "unknown";
    }

    private static string FormatDuration(TimeSpan dur)
    {
        if (dur.TotalDays >= 1) return $"{(int)dur.TotalDays}d {dur.Hours}h";
        if (dur.TotalHours >= 1) return $"{(int)dur.TotalHours}h {dur.Minutes}m";
        if (dur.TotalMinutes >= 1) return $"{(int)dur.TotalMinutes}m {dur.Seconds}s";
        return $"{(int)dur.TotalSeconds}s";
    }

    private void UpdateLogPathLabel()
    {
        var cfg = _api.Config.ConfigDirectory;
        if (string.IsNullOrWhiteSpace(cfg))
        {
            LogDirText.Text = "(config directory not found)";
            return;
        }
        var logsPath = Path.Combine(cfg, "logs");
        // Show the logs path even when missing — clicking Open will
        // gracefully fall back to the config directory.
        LogDirText.Text = logsPath;
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        var cfg = _api.Config.ConfigDirectory;
        if (string.IsNullOrWhiteSpace(cfg))
        {
            ShowDiagStatus(InfoBarSeverity.Warning, "No config directory",
                "Hermes config directory wasn't discovered, so there's nowhere to open. " +
                "Set HERMES_HOME or install Hermes to %LOCALAPPDATA%\\hermes.");
            return;
        }

        var logsPath = Path.Combine(cfg, "logs");
        string toOpen;
        if (Directory.Exists(logsPath)) toOpen = logsPath;
        else if (Directory.Exists(cfg)) toOpen = cfg;
        else
        {
            ShowDiagStatus(InfoBarSeverity.Warning, "Directory missing",
                $"Neither '{logsPath}' nor '{cfg}' exists on disk. Hermes may not have been started yet.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = toOpen,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowDiagStatus(InfoBarSeverity.Error, "Couldn't open folder", ex.Message);
        }
    }

    private void CopyDebugInfo_Click(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**Hermes WinUI debug info** — {DateTime.Now:yyyy-MM-ddTHH:mm:ssK}");
        sb.AppendLine();
        sb.AppendLine($"App: v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?"}");
        sb.AppendLine($"OS: {Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")})");
        sb.AppendLine($"Config dir: {RedactPath(_api.Config.ConfigDirectory)}");
        sb.AppendLine();
        sb.AppendLine("Gateway:");
        sb.AppendLine($"- State: {GatewayStateText.Text}");
        sb.AppendLine($"- PID: {GatewayPidText.Text}");
        sb.AppendLine($"- Kind: {GatewayKindText.Text}");
        sb.AppendLine($"- Uptime: {GatewayUptimeText.Text}");
        sb.AppendLine($"- Active agents: {GatewayActiveAgentsText.Text}");
        sb.AppendLine($"- Last updated: {GatewayUpdatedText.Text}");
        if (GatewayExitReasonText.Visibility == Visibility.Visible
            && !string.IsNullOrWhiteSpace(GatewayExitReasonText.Text))
        {
            sb.AppendLine($"- Exit reason: {GatewayExitReasonText.Text}");
        }
        // Intentionally NOT including argv from gateway.pid — it can
        // contain --api-key, bearer tokens in URLs, custom config
        // paths, and other things people don't realise they're
        // pasting into a GitHub issue.
        sb.AppendLine();
        sb.AppendLine("Connection:");
        sb.AppendLine($"- Host: {_api.Config.Host}");
        sb.AppendLine($"- Port: {_api.Config.Port}");
        sb.AppendLine($"- Model: {_api.Config.ModelName}");
        sb.AppendLine($"- API key set: {(string.IsNullOrEmpty(_api.Config.ApiKey) ? "no" : "yes (not shown)")}");
        sb.AppendLine();
        if (PlatformBridgesList.Count > 0)
        {
            sb.AppendLine("Platform bridges:");
            foreach (var b in PlatformBridgesList)
            {
                sb.AppendLine($"- {b.Name}: {b.StateDescription}");
            }
            sb.AppendLine();
        }
        if (Features.Count > 0)
        {
            var enabled = Features.Count(f => f.IsEnabled);
            sb.AppendLine($"Capabilities ({enabled}/{Features.Count}):");
            foreach (var f in Features)
            {
                sb.AppendLine($"- {(f.IsEnabled ? "✓" : "✗")} {f.Name}");
            }
        }
        else
        {
            sb.AppendLine("Capabilities: not loaded (open Diagnostics first, then copy again).");
        }

        try
        {
            var pkg = new DataPackage();
            pkg.SetText(sb.ToString());
            Clipboard.SetContent(pkg);
            ShowDiagStatus(InfoBarSeverity.Success, "Debug info copied",
                "Paste into a GitHub issue or chat to share with a maintainer.");
        }
        catch (Exception ex)
        {
            ShowDiagStatus(InfoBarSeverity.Error, "Couldn't copy",
                $"Clipboard wasn't writable: {ex.Message}");
        }
    }

    /// <summary>Rewrites user-specific path prefixes to their environment
    /// variable form so copied debug info doesn't leak the username
    /// (or other personal path bits) into a public issue.</summary>
    private static string RedactPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "(not set)";
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(local) && path.StartsWith(local, StringComparison.OrdinalIgnoreCase))
            return "%LOCALAPPDATA%" + path[local.Length..];
        if (!string.IsNullOrEmpty(profile) && path.StartsWith(profile, StringComparison.OrdinalIgnoreCase))
            return "%USERPROFILE%" + path[profile.Length..];
        return path;
    }

    private void ShowDiagStatus(InfoBarSeverity severity, string title, string message)
    {
        DiagnosticsStatusBar.Severity = severity;
        DiagnosticsStatusBar.Title = title;
        DiagnosticsStatusBar.Message = message;
        DiagnosticsStatusBar.IsOpen = true;
    }

    // ---- About pane --------------------------------------------------------
    //
    // "Check for updates" pings the GitHub Releases API for the latest
    // tag and compares it to BuildInfo.Version. The UpdateChecker
    // service handles HTTP, payload parsing, rate limiting (403/429),
    // missing-release (404), and network-error cases — we just map its
    // result onto the InfoBar + status line, with an Action button
    // hyperlink to the release page when one is available.

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking GitHub for updates…";
        AboutStatusBar.IsOpen = false;
        // Clear any prior ActionButton so a previous "View release"
        // doesn't stick around past a check that no longer offers one.
        AboutStatusBar.ActionButton = null;
        try
        {
            using var checker = new UpdateChecker();
            var result = await checker.CheckAsync(BuildInfo.Version);
            ApplyUpdateResult(result);
        }
        catch (Exception ex)
        {
            ShowAboutStatus(InfoBarSeverity.Error, "Update check failed", ex.Message);
            UpdateStatusText.Text = "";
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private void ApplyUpdateResult(UpdateCheckResult r)
    {
        switch (r.Status)
        {
            case UpdateStatus.UpToDate:
                ShowAboutStatus(InfoBarSeverity.Success, "You're up to date", r.Message ?? "");
                UpdateStatusText.Text = $"Latest release: v{r.LatestVersion}";
                break;

            case UpdateStatus.UpdateAvailable:
                ShowAboutStatus(InfoBarSeverity.Informational,
                    $"Update available: v{r.LatestVersion}", r.Message ?? "");
                UpdateStatusText.Text = $"Current: v{BuildInfo.Version} → Latest: v{r.LatestVersion}";
                if (!string.IsNullOrEmpty(r.LatestUrl))
                {
                    AboutStatusBar.ActionButton = new HyperlinkButton
                    {
                        Content = "View release",
                        NavigateUri = new Uri(r.LatestUrl!),
                    };
                }
                break;

            case UpdateStatus.AheadOfReleased:
                ShowAboutStatus(InfoBarSeverity.Informational, "Dev build", r.Message ?? "");
                UpdateStatusText.Text = $"Current: v{BuildInfo.Version} (latest released: v{r.LatestVersion})";
                break;

            case UpdateStatus.NoReleases:
                ShowAboutStatus(InfoBarSeverity.Informational, "No releases yet",
                    r.Message ?? "This repository hasn't published any releases.");
                UpdateStatusText.Text = $"Current: v{BuildInfo.Version}";
                AboutStatusBar.ActionButton = new HyperlinkButton
                {
                    Content = "Open Releases page",
                    NavigateUri = new Uri(BuildInfo.ReleasesUrl),
                };
                break;

            case UpdateStatus.RateLimited:
                ShowAboutStatus(InfoBarSeverity.Warning, "GitHub rate-limited",
                    r.Message ?? "Try again in a few minutes.");
                UpdateStatusText.Text = "";
                break;

            case UpdateStatus.NetworkError:
            default:
                ShowAboutStatus(InfoBarSeverity.Warning, "Couldn't check for updates",
                    r.Message ?? "Network error — check your connection and try again.");
                UpdateStatusText.Text = "";
                break;
        }
    }

    private void ShowAboutStatus(InfoBarSeverity severity, string title, string message)
    {
        AboutStatusBar.Severity = severity;
        AboutStatusBar.Title = title;
        AboutStatusBar.Message = message;
        AboutStatusBar.IsOpen = true;
    }
}
