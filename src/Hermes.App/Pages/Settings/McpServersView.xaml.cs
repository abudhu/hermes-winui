using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hermes.ApiClient;
using Hermes.App.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace Hermes.App.Pages.Settings;

/// <summary>
/// Master-detail editor for the <c>mcp_servers</c> block of the user's
/// Hermes <c>config.yaml</c>. Reads via
/// <see cref="HermesYamlConfig.Load"/>; saves via
/// <see cref="HermesYamlConfig.Save"/>. The list on the left always
/// reflects what's on disk; the form on the right is the staging area
/// for the currently-selected (or new) server.
/// </summary>
public sealed partial class McpServersView : UserControl
{
    private readonly HermesConfig _hermesConfig;
    public ObservableCollection<McpServerItemVm> Servers { get; } = [];

    private ConcurrencyToken? _token;

    /// <summary>Snapshot of the on-disk
    /// <c>platform_toolsets.api_server</c> list captured at load time.
    /// Compared against what we WOULD write to decide whether the
    /// "gateway opt-in is out of sync" hint should appear and whether
    /// Save should light up even when the editor form isn't dirty.</summary>
    private IReadOnlyList<string> _onDiskApiServer = [];

    /// <summary>The server currently selected in the list (or
    /// <c>null</c> for "New server"). Cached so we know whether to
    /// rename vs add when the user saves.</summary>
    private McpServerItemVm? _selected;

    /// <summary>"Original" snapshot of the editor fields, captured every
    /// time we (re)populate the editor from the list. Dirty state is
    /// the diff between this and the current field values.</summary>
    private string _originalName = "";
    private string _originalBody = "";

    /// <summary>True while we're populating the editor wholesale —
    /// selection change, "new server", template pick, revert,
    /// post-save resync, or a toggle-driven rewrite. Every change
    /// handler early-returns when this is set, because the load path
    /// re-syncs everything (parse state, toggle, form, buttons)
    /// explicitly at the end. Lets us avoid the worst dirty/feedback
    /// loops without sprinkling guard flags everywhere.</summary>
    private bool _loadingEditor;

    /// <summary>True while <see cref="SyncFormFromBody"/> is writing
    /// the structured form fields. Form *_Changed handlers check this
    /// so they don't immediately push the just-read value back into
    /// the body (which would then re-trigger SyncFormFromBody, ad
    /// infinitum). Independent of <see cref="_syncingBodyFromForm"/>
    /// so the two sync paths can't shadow each other.</summary>
    private bool _syncingFormFromBody;

    /// <summary>True while <see cref="RenderBodyFromForm"/> is writing
    /// <see cref="BodyBox"/>. <see cref="Body_Changed"/> still updates
    /// dirty state and parse state, but skips the form-resync step so
    /// the form values the user is actively typing aren't clobbered
    /// by a round-trip through JSON.</summary>
    private bool _syncingBodyFromForm;

    /// <summary>Tracks whether <see cref="BodyBox"/>'s current text
    /// parses as a JSON object. The editor Enabled toggle disables
    /// itself when this is false so it never lies about state. Kept
    /// as a field rather than recomputed on demand because the
    /// Toggled handler needs to short-circuit fast.</summary>
    private bool _bodyIsValidJsonObject = true;

    /// <summary>True when the body's managed keys all have shapes
    /// the structured form can safely round-trip. When false the
    /// form controls disable and a banner explains why; raw JSON
    /// editing still works. Recomputed every
    /// <see cref="SyncFormFromBody"/>.</summary>
    private bool _isBodyFormCompatible = true;

    /// <summary>Cancellation token source for the currently-running
    /// Test connection request. We cancel + dispose this whenever the
    /// user switches selection, edits the body in a way we should treat
    /// as a re-test, or starts another test, so a slow process spawn
    /// can't update the InfoBar for the wrong server.</summary>
    private CancellationTokenSource? _testCts;

    /// <summary>Monotonically-incrementing token assigned to each Test
    /// click; compared on completion to decide whether the result is
    /// still relevant (no newer test or selection change happened
    /// while we were spawning/waiting).</summary>
    private int _testGeneration;

    /// <summary>Args list backing the stdio "Args" repeater. Each
    /// entry is one positional arg string.</summary>
    public ObservableCollection<EditableStringVm> Args { get; } = [];

    /// <summary>Env vars backing the stdio "Env" repeater. Key=name,
    /// Value=raw string value (we don't redact or interpret).</summary>
    public ObservableCollection<EditableKeyValueVm> Env { get; } = [];

    /// <summary>HTTP headers backing the http "Headers" repeater.
    /// Key=header name, Value=raw header value.</summary>
    public ObservableCollection<EditableKeyValueVm> Headers { get; } = [];

    /// <summary>Managed keys the structured form owns. Everything
    /// else in the body (enabled, auth, sampling, tools, etc.) is
    /// preserved verbatim across <see cref="RenderBodyFromForm"/>.</summary>
    private static readonly HashSet<string> ManagedKeys = new(StringComparer.Ordinal)
    {
        "command", "args", "env", "url", "headers", "timeout",
    };

    // ---- Row-toggle gating (bound from the DataTemplate) -------------------

    /// <summary>Bound from each row's <c>ToggleSwitch.IsEnabled</c>
    /// (one-way) so editor-dirty state disables every row toggle in one
    /// go. The DataTemplate refers to this via
    /// <c>ElementName=Root</c>.</summary>
    public static readonly DependencyProperty AreRowTogglesEnabledProperty =
        DependencyProperty.Register(
            nameof(AreRowTogglesEnabled),
            typeof(bool),
            typeof(McpServersView),
            new PropertyMetadata(true));

    public bool AreRowTogglesEnabled
    {
        get => (bool)GetValue(AreRowTogglesEnabledProperty);
        set => SetValue(AreRowTogglesEnabledProperty, value);
    }

    /// <summary>Companion tooltip shown on every row toggle.
    /// "Save or revert current edits first" when row toggles are
    /// disabled; empty string when enabled (avoids a noisy permanent
    /// tooltip on the always-present case).</summary>
    public static readonly DependencyProperty RowTogglesTooltipProperty =
        DependencyProperty.Register(
            nameof(RowTogglesTooltip),
            typeof(string),
            typeof(McpServersView),
            new PropertyMetadata(""));

    public string RowTogglesTooltip
    {
        get => (string)GetValue(RowTogglesTooltipProperty);
        set => SetValue(RowTogglesTooltipProperty, value);
    }

    public McpServersView()
    {
        InitializeComponent();
        _hermesConfig = App.Services.GetRequiredService<HermesConfig>();
        ServerList.ItemsSource = Servers;
        ArgsRepeater.ItemsSource = Args;
        EnvRepeater.ItemsSource = Env;
        HeadersRepeater.ItemsSource = Headers;
        Loaded += OnLoaded;
    }

    /// <summary>True if the editor has unsaved changes vs the currently
    /// selected server. Polled by SettingsPage's nav guard.</summary>
    public bool IsEditorDirty =>
        NameBox.Text != _originalName || BodyBox.Text != _originalBody;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            LoadFromDisk();
        }
        catch (InvalidDataException ex)
        {
            ShowEditorStatus(InfoBarSeverity.Error, "Cannot read config.yaml", ex.Message);
        }
        catch (Exception ex)
        {
            ShowEditorStatus(InfoBarSeverity.Error, "Cannot read config.yaml", ex.Message);
        }
    }

    private void LoadFromDisk()
    {
        var (entries, token) = HermesYamlConfig.Load(_hermesConfig.ConfigYamlPath);
        _token = token;
        _onDiskApiServer = HermesYamlConfig.ReadApiServerToolsets(_hermesConfig.ConfigYamlPath);

        Servers.Clear();
        foreach (var entry in entries)
        {
            Servers.Add(McpServerItemVm.From(entry));
        }
        UpdateEmptyHint();
        UpdateGatewaySyncBanner();

        // Re-select previous server by name if still present, else select
        // the first, else show "new server" form.
        var prevName = _selected?.Name;
        McpServerItemVm? next = null;
        if (!string.IsNullOrEmpty(prevName))
        {
            next = Servers.FirstOrDefault(s => s.Name == prevName);
        }
        next ??= Servers.FirstOrDefault();

        if (next is not null)
        {
            ServerList.SelectedItem = next;  // triggers SelectionChanged
        }
        else
        {
            ShowNewServerForm();
        }
    }

    private void UpdateEmptyHint()
    {
        EmptyHint.Visibility = Servers.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>True when the on-disk
    /// <c>platform_toolsets.api_server</c> list doesn't include every
    /// MCP server we know about (or includes stale entries). When this
    /// is true the Save button lights up even without form edits, and
    /// a banner explains why.</summary>
    private bool IsGatewayOptInOutOfSync
    {
        get
        {
            var desired = BuildApiServerToolsets(Servers.Select(s => new McpServerEntry(s.Name, s.Body)).ToList());
            if (desired.Count != _onDiskApiServer.Count) return true;
            for (int i = 0; i < desired.Count; i++)
            {
                if (!string.Equals(desired[i], _onDiskApiServer[i], StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }

    private void UpdateGatewaySyncBanner()
    {
        if (IsGatewayOptInOutOfSync)
        {
            GatewaySyncBar.Title = "Gateway opt-in needs syncing";
            GatewaySyncBar.Message =
                "Your saved MCP servers aren't exposed to the WinUI chat yet. " +
                "Click Save (on any server) to write `platform_toolsets.api_server` into config.yaml, " +
                "then restart your Hermes gateway. Without this step, chats will say " +
                "\"I don't have access\" when asked about MCP-backed data.";
            GatewaySyncBar.Severity = InfoBarSeverity.Warning;
            GatewaySyncBar.IsOpen = true;
        }
        else
        {
            GatewaySyncBar.IsOpen = false;
        }
    }

    private void ServerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        if (e.AddedItems[0] is not McpServerItemVm vm) return;

        CancelAnyInFlightTest();
        _selected = vm;
        EditorTitle.Text = $"Edit server '{vm.Name}'";

        _loadingEditor = true;
        try
        {
            NameBox.Text = vm.Name;
            BodyBox.Text = vm.BodyAsJsonText();
            _originalName = NameBox.Text;
            _originalBody = BodyBox.Text;
            RefreshBodyParseState();
            SyncEditorToggleFromBody();
            SyncFormFromBody();
        }
        finally
        {
            _loadingEditor = false;
        }
        UpdateButtons();
        EditorStatusBar.IsOpen = false;
    }

    private void AddEmpty_Click(object sender, RoutedEventArgs e)
    {
        ServerList.SelectedItem = null;
        ShowNewServerForm();
    }

    private void AddTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem mfi || mfi.Tag is not string tag) return;
        var (name, body) = GetTemplate(tag);
        ServerList.SelectedItem = null;
        _selected = null;
        EditorTitle.Text = "New server";

        _loadingEditor = true;
        try
        {
            NameBox.Text = name;
            BodyBox.Text = body;
            // Templates start dirty so the user can save immediately.
            _originalName = "";
            _originalBody = "";
            RefreshBodyParseState();
            SyncEditorToggleFromBody();
            SyncFormFromBody();
        }
        finally
        {
            _loadingEditor = false;
        }
        UpdateButtons();
        EditorStatusBar.IsOpen = false;
    }

    private void ShowNewServerForm()
    {
        _selected = null;
        EditorTitle.Text = "New server";

        _loadingEditor = true;
        try
        {
            NameBox.Text = "";
            // Minimal stdio scaffold so the form opens populated with
            // empty Command + empty Args list ready for the user to
            // fill in. Anyone who wants a working starter picks a
            // template from the dropdown.
            BodyBox.Text = """
                {
                  "command": "",
                  "args": []
                }
                """;
            _originalName = "";
            _originalBody = "";
            RefreshBodyParseState();
            SyncEditorToggleFromBody();
            SyncFormFromBody();
        }
        finally
        {
            _loadingEditor = false;
        }
        UpdateButtons();
        EditorStatusBar.IsOpen = false;
    }

    private void Editor_Changed(object sender, object e)
    {
        if (_loadingEditor) return;
        UpdateButtons();
    }

    /// <summary>BodyBox text-changed handler. Splits off from
    /// <see cref="Editor_Changed"/> because edits to the JSON body have
    /// the extra side effect of re-syncing the Enabled toggle and the
    /// structured form (and disabling the toggle when the body becomes
    /// invalid JSON).</summary>
    private void Body_Changed(object sender, object e)
    {
        if (_loadingEditor) return;
        RefreshBodyParseState();
        SyncEditorToggleFromBody();
        if (!_syncingBodyFromForm)
        {
            // Body changed by direct user edit (or by the enabled
            // toggle's rewrite) — re-derive the form from it. Skipped
            // when the form itself is what triggered the body write,
            // to avoid clobbering the user's in-flight typing.
            SyncFormFromBody();
        }
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var dirty = IsEditorDirty;
        var apiOutOfSync = IsGatewayOptInOutOfSync;
        // Save lights up when the editor form is dirty OR the gateway
        // opt-in is out of sync — the latter case still produces a
        // meaningful write even with no per-server edits.
        SaveButton.IsEnabled = _selected is null
            ? !string.IsNullOrWhiteSpace(NameBox.Text) || !string.IsNullOrWhiteSpace(BodyBox.Text)
            : (dirty || apiOutOfSync);
        RevertButton.IsEnabled = dirty;
        RemoveButton.IsEnabled = _selected is not null;
        if (dirty)
        {
            DirtyHint.Text = "Unsaved changes";
        }
        else if (apiOutOfSync)
        {
            DirtyHint.Text = "Gateway opt-in out of sync — click Save to update platform_toolsets.api_server";
        }
        else
        {
            DirtyHint.Text = "";
        }

        // Row toggles share a single gating bool: dirty editor blocks
        // immediate-save row toggles to keep the page's "edit then save"
        // model coherent with the right pane.
        AreRowTogglesEnabled = !dirty;
        RowTogglesTooltip = dirty
            ? "Save or revert current edits first."
            : "";

        // Test connection needs a parseable body and a transport to
        // poke at. It also turns off while a test is in flight (the
        // click handler flips this manually when starting).
        TestButton.IsEnabled = _testCts is null
            && _bodyIsValidJsonObject
            && HasTestableTransport();
    }

    /// <summary>True when <see cref="BodyBox"/>'s parsed body has either
    /// a non-empty <c>command</c> or a non-empty <c>url</c>. Used to gate
    /// the Test connection button so we don't try to spawn nothing.</summary>
    private bool HasTestableTransport()
    {
        if (!TryParseBody(BodyBox.Text, out var body)) return false;
        var hasCommand = body.TryGetProperty("command", out var c)
            && c.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(c.GetString());
        var hasUrl = body.TryGetProperty("url", out var u)
            && u.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(u.GetString());
        return hasCommand ^ hasUrl;
    }

    private static bool TryParseBody(string text, out JsonElement body)
    {
        body = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            body = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException) { return false; }
    }

    private void CancelAnyInFlightTest()
    {
        if (_testCts is null) return;
        try { _testCts.Cancel(); }
        catch (ObjectDisposedException) { /* benign */ }
        _testCts.Dispose();
        _testCts = null;
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        // Bump generation + cancel any prior in-flight test so its result
        // can't update the InfoBar after the user clicked again.
        CancelAnyInFlightTest();
        var generation = unchecked(++_testGeneration);
        _testCts = new CancellationTokenSource();
        var ct = _testCts.Token;

        // Validate the same way Save does, so a broken config produces a
        // friendly "fix this first" rather than spawning a doomed process.
        var name = NameBox.Text;
        var body = BodyBox.Text;
        var others = Servers.Where(s => !ReferenceEquals(s, _selected)).ToList();
        var validation = McpServerValidator.Validate(name, body, others);
        if (validation.Errors.Count > 0)
        {
            ShowEditorStatus(InfoBarSeverity.Warning,
                "Fix validation errors first",
                string.Join(Environment.NewLine, validation.Errors));
            _testCts.Dispose();
            _testCts = null;
            return;
        }

        var parsedBody = validation.ParsedBody!.Value;
        var displayName = string.IsNullOrWhiteSpace(name) ? "server" : name.Trim();
        SetTestButtonRunning(true);
        ShowEditorStatus(InfoBarSeverity.Informational,
            $"Testing '{displayName}'…",
            "Running MCP initialize handshake. This usually takes a second or two.");

        McpTestResult result;
        try
        {
            var tester = new McpServerTester();
            result = await Task.Run(() => tester.TestAsync(parsedBody, ct), ct);
        }
        catch (OperationCanceledException)
        {
            // User switched away or clicked again. The new path owns the UI;
            // do nothing here.
            return;
        }
        catch (Exception ex)
        {
            // If we're the still-current generation, surface the error.
            if (generation == _testGeneration)
            {
                ShowEditorStatus(InfoBarSeverity.Error,
                    "Test failed", ex.Message);
                SetTestButtonRunning(false);
                _testCts?.Dispose();
                _testCts = null;
                UpdateButtons();
            }
            return;
        }

        // Stale result guard: if generation changed (user clicked again or
        // switched selection), discard.
        if (generation != _testGeneration) return;

        ApplyTestResult(displayName, result);
        SetTestButtonRunning(false);
        _testCts?.Dispose();
        _testCts = null;
        UpdateButtons();
    }

    private void SetTestButtonRunning(bool running)
    {
        TestButton.IsEnabled = !running && _bodyIsValidJsonObject && HasTestableTransport();
        TestButtonText.Text = running ? "Testing…" : "Test connection";
        // Spinner glyph (E895 / GlobalNavigationButton fallback to Refresh E72C).
        // Use Sync (E895) when idle; just keep idle icon, the InfoBar carries
        // the running state — avoids needing a ProgressRing in the button.
    }

    private void ApplyTestResult(string displayName, McpTestResult result)
    {
        if (result.IsSuccess)
        {
            ShowEditorStatus(InfoBarSeverity.Success,
                $"{result.Title} — '{displayName}'",
                result.Detail);
            return;
        }

        var severity = result.Status switch
        {
            McpTestStatus.Timeout => InfoBarSeverity.Warning,
            McpTestStatus.ConfigInvalid => InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Error,
        };
        ShowEditorStatus(severity, $"{result.Title} — '{displayName}'", result.Detail);
    }

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        _loadingEditor = true;
        try
        {
            NameBox.Text = _originalName;
            BodyBox.Text = _originalBody;
            RefreshBodyParseState();
            SyncEditorToggleFromBody();
            SyncFormFromBody();
        }
        finally
        {
            _loadingEditor = false;
        }
        UpdateButtons();
        EditorStatusBar.IsOpen = false;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_token is null)
        {
            ShowEditorStatus(InfoBarSeverity.Error, "Not loaded",
                "Config wasn't loaded successfully — restart the app or check that " +
                $"'{_hermesConfig.ConfigYamlPath}' is readable.");
            return;
        }

        var name = NameBox.Text;
        var body = BodyBox.Text;

        // Collision check excludes the server we're editing (if any) so
        // renaming to a normalised collision with ourselves doesn't
        // false-positive.
        var others = Servers.Where(s => !ReferenceEquals(s, _selected)).ToList();
        var validation = McpServerValidator.Validate(name, body, others);

        if (validation.Errors.Count > 0)
        {
            ShowEditorStatus(InfoBarSeverity.Error,
                "Validation failed",
                string.Join(Environment.NewLine, validation.Errors));
            return;
        }

        // Build the new server list with the edit applied.
        var nameTrimmed = name.Trim();
        var parsedBody = validation.ParsedBody!.Value;
        var newList = new List<McpServerEntry>(Servers.Count + 1);
        var added = false;
        foreach (var existing in Servers)
        {
            if (ReferenceEquals(existing, _selected))
            {
                newList.Add(new McpServerEntry(nameTrimmed, parsedBody));
                added = true;
            }
            else
            {
                newList.Add(new McpServerEntry(existing.Name, existing.Body));
            }
        }
        if (!added) // "New server" path — append.
        {
            newList.Add(new McpServerEntry(nameTrimmed, parsedBody));
        }

        var result = HermesYamlConfig.Save(
            _hermesConfig.ConfigYamlPath,
            _token,
            newList,
            BuildApiServerToolsets(newList));
        HandleSaveResult(result, $"'{nameTrimmed}' saved.");

        if (result.Status == YamlSaveStatus.Saved || result.Status == YamlSaveStatus.Unchanged)
        {
            // Cache the name we just saved so LoadFromDisk re-selects it.
            _selected = new McpServerItemVm(nameTrimmed, parsedBody);
            LoadFromDisk();

            // Append a warning if the validator produced any.
            if (validation.Warnings.Count > 0)
            {
                ShowEditorStatus(InfoBarSeverity.Warning, "Saved (with warnings)",
                    string.Join(Environment.NewLine, validation.Warnings));
            }
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_token is null || _selected is null) return;

        var removedName = _selected.Name;
        var newList = Servers
            .Where(s => !ReferenceEquals(s, _selected))
            .Select(s => new McpServerEntry(s.Name, s.Body))
            .ToList();

        var result = HermesYamlConfig.Save(
            _hermesConfig.ConfigYamlPath,
            _token,
            newList,
            BuildApiServerToolsets(newList));
        HandleSaveResult(result, $"'{removedName}' removed.");

        if (result.Status == YamlSaveStatus.Saved || result.Status == YamlSaveStatus.Unchanged)
        {
            _selected = null;
            LoadFromDisk();
        }
    }

    /// <summary>
    /// Builds the desired <c>platform_toolsets.api_server</c> list:
    /// <see cref="HermesYamlConfig.DefaultApiServerToolset"/> first
    /// (so non-MCP api_server tools — web, file, terminal — stay
    /// available), then each MCP server name. Without this, the WinUI
    /// chat won't see any MCP tools because the api_server platform
    /// passes <c>include_default_mcp_servers=False</c>.
    /// </summary>
    private static List<string> BuildApiServerToolsets(IReadOnlyList<McpServerEntry> servers)
    {
        var list = new List<string>(servers.Count + 1)
        {
            HermesYamlConfig.DefaultApiServerToolset,
        };
        foreach (var s in servers)
        {
            // Defensive: skip empty/whitespace names. Validation upstream
            // should already prevent this, but cheap to enforce here too.
            if (!string.IsNullOrWhiteSpace(s.Name))
                list.Add(s.Name);
        }
        return list;
    }

    private void HandleSaveResult(YamlSaveResult result, string successTitle)
    {
        switch (result.Status)
        {
            case YamlSaveStatus.Saved:
                ShowEditorStatus(InfoBarSeverity.Success, successTitle,
                    "Wrote config.yaml. A backup is at config.yaml.bak. " +
                    "On the very first save, a permanent pre-edit backup was also " +
                    "created at config.yaml.before-hermes-winui.bak.");
                ReloadHintBar.IsOpen = true;
                break;
            case YamlSaveStatus.Unchanged:
                ShowEditorStatus(InfoBarSeverity.Informational, "No changes",
                    "The on-disk config already matches your edits — nothing to save.");
                break;
            case YamlSaveStatus.ConflictExternalEdit:
                ShowEditorStatus(InfoBarSeverity.Warning, "Conflict",
                    result.Detail ?? "config.yaml was modified outside this app.");
                break;
            case YamlSaveStatus.ValidationFailed:
                ShowEditorStatus(InfoBarSeverity.Error, "Could not save",
                    result.Detail ?? "Validation failed.");
                break;
        }
    }

    private void ShowEditorStatus(InfoBarSeverity severity, string title, string message)
    {
        EditorStatusBar.Severity = severity;
        EditorStatusBar.Title = title;
        EditorStatusBar.Message = message;
        EditorStatusBar.IsOpen = true;
    }

    // ---- Enabled toggle ⇄ body-JSON sync ----------------------------------
    //
    // The BodyBox is the canonical representation of the server entry.
    // The editor's Enabled toggle is a thin affordance that mutates the
    // body's `enabled` key when the user flips it, and reflects the
    // body's `enabled` key into its `IsOn` state on every body change /
    // selection change. We intentionally never re-format the body when
    // the user is just typing — only an explicit toggle flip mutates
    // text. When the body isn't a valid JSON object the toggle is
    // disabled with a teaching caption so it never displays a stale
    // value the user might trust.

    /// <summary>Re-parses <see cref="BodyBox"/> and updates
    /// <see cref="_bodyIsValidJsonObject"/>. Cheap (small body, runs on
    /// keystrokes) but avoid calling more often than necessary.</summary>
    private void RefreshBodyParseState()
    {
        _bodyIsValidJsonObject = TryParseObject(BodyBox.Text, out _);
    }

    /// <summary>Push the body's current <c>enabled</c> state into
    /// <see cref="EditorEnabledToggle.IsOn"/>, and enable/disable the
    /// toggle itself based on body validity. Suppresses dirty/toggle
    /// handlers around the <c>IsOn</c> assignment so callers don't have
    /// to remember to wrap the call themselves.</summary>
    private void SyncEditorToggleFromBody()
    {
        if (_bodyIsValidJsonObject && TryParseObject(BodyBox.Text, out var body))
        {
            // Three cases for `enabled`:
            //   * absent → toggle on (Hermes default true)
            //   * JSON bool → toggle reflects it
            //   * present but wrong type → toggle disabled, hint shown
            //     (we can't honestly represent a non-bool value, and the
            //     validator will reject any save until it's fixed)
            var hasEnabled = body.TryGetProperty("enabled", out var en);
            var enabledIsBool = hasEnabled
                && (en.ValueKind == JsonValueKind.True || en.ValueKind == JsonValueKind.False);
            if (hasEnabled && !enabledIsBool)
            {
                EditorEnabledToggle.IsEnabled = false;
                EditorEnabledHint.Text =
                    "'enabled' must be true or false. Fix it below before using the toggle.";
                return;
            }

            var newIsOn = TryReadEnabled(body) ?? true;
            _loadingEditor = true;
            try
            {
                EditorEnabledToggle.IsEnabled = true;
                EditorEnabledToggle.IsOn = newIsOn;
            }
            finally
            {
                _loadingEditor = false;
            }
            EditorEnabledHint.Text = "";
        }
        else
        {
            EditorEnabledToggle.IsEnabled = false;
            // Keep IsOn at whatever it was — disabled toggles look
            // visually frozen anyway, and not flipping it avoids implying
            // a value we don't actually know.
            EditorEnabledHint.Text = "Fix the JSON below before changing Enabled.";
        }
    }

    private void EditorEnabled_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingEditor) return;
        if (sender is not ToggleSwitch ts) return;
        if (!_bodyIsValidJsonObject)
        {
            // Should be unreachable (toggle is disabled) — but defend.
            return;
        }
        if (!TryParseObject(BodyBox.Text, out var body)) return;

        var newJsonText = SerializeObject(SetOrRemoveEnabled(body, ts.IsOn));

        _loadingEditor = true;
        try
        {
            BodyBox.Text = newJsonText;
            RefreshBodyParseState();
            // Body just changed — pull the form along so the user
            // sees the same JSON shape they'd see if they typed it.
            SyncFormFromBody();
        }
        finally
        {
            _loadingEditor = false;
        }
        // Toggle flips count as a body edit — fire the normal dirty path.
        UpdateButtons();
    }

    // ---- Row-toggle handlers (per-server, immediate save) ------------------

    /// <summary>Block the row-toggle tap from also selecting the row.
    /// The row-toggle is its own affordance; tapping it should never
    /// double as "select this row for editing."</summary>
    private void RowToggle_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
    }

    /// <summary>Belt-and-braces with <see cref="RowToggle_Tapped"/>:
    /// stops the pointer-down from being interpreted by ListViewItem as
    /// a row-selection gesture before Tapped fires.</summary>
    private void RowToggle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
    }

    private async void RowEnabled_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingEditor) return;
        if (sender is not ToggleSwitch ts) return;
        if (ts.DataContext is not McpServerItemVm vm) return;

        // Defensive: AreRowTogglesEnabled binding should already gate
        // this, but a race during dirty-state transitions could let one
        // tap through. Snap back without saving rather than write a
        // partial state with stale editor edits hanging around.
        if (IsEditorDirty)
        {
            _loadingEditor = true;
            try { ts.IsOn = vm.IsEnabled; }
            finally { _loadingEditor = false; }
            ShowEditorStatus(InfoBarSeverity.Warning, "Save your edits first",
                "Save or revert the open server before toggling other rows.");
            return;
        }

        if (_token is null)
        {
            ShowEditorStatus(InfoBarSeverity.Error, "Not loaded",
                "Config wasn't loaded successfully — restart the app or check that " +
                $"'{_hermesConfig.ConfigYamlPath}' is readable.");
            // Revert the visual flip so we don't lie about state.
            _loadingEditor = true;
            try { ts.IsOn = vm.IsEnabled; }
            finally { _loadingEditor = false; }
            return;
        }

        var desiredEnabled = ts.IsOn;
        var newBody = SetOrRemoveEnabled(vm.Body, desiredEnabled);
        var newList = Servers
            .Select(s => ReferenceEquals(s, vm)
                ? new McpServerEntry(vm.Name, newBody)
                : new McpServerEntry(s.Name, s.Body))
            .ToList();

        var result = HermesYamlConfig.Save(
            _hermesConfig.ConfigYamlPath,
            _token,
            newList,
            BuildApiServerToolsets(newList));

        var verb = desiredEnabled ? "enabled" : "disabled";
        HandleSaveResult(result, $"'{vm.Name}' {verb}.");

        if (result.Status == YamlSaveStatus.Saved || result.Status == YamlSaveStatus.Unchanged)
        {
            // Preserve which server is selected across the reload, in
            // case the user is editing the toggled server itself.
            var keepName = vm.Name;
            _selected = new McpServerItemVm(keepName, newBody);
            LoadFromDisk();
        }
        else
        {
            // Save rejected — flip the toggle back so it matches reality.
            _loadingEditor = true;
            try { ts.IsOn = vm.IsEnabled; }
            finally { _loadingEditor = false; }
        }

        // Avoid the unused parameter warning while keeping the async
        // signature for the event handler.
        await Task.CompletedTask;
    }

    // ---- Restart Hermes ----------------------------------------------------
    //
    // We do NOT own the Hermes gateway process — it's launched by the
    // user (installer shortcut, `hermes serve` in a terminal, scheduled
    // task). The button shows a dialog with the canonical restart
    // command (`hermes gateway run --replace`, which Hermes itself uses
    // to atomically stop+restart) and offers to copy it to the
    // clipboard. We never spawn Hermes ourselves and never
    // Stop-Process anything — the process names overlap with other
    // tools (the gateway runs as python.exe, not hermes.exe) and any
    // blanket kill is unsafe.

    /// <summary>Command we offer to the user. Single source of truth so
    /// the dialog body and the clipboard payload can't drift.</summary>
    private const string RestartCommand = "hermes gateway run --replace";

    /// <summary>True while a Restart Hermes dialog is showing. WinUI
    /// allows only one <see cref="ContentDialog"/> per XamlRoot at a
    /// time; a fast double-click on the button (or one click each on the
    /// header button + the InfoBar action button) would otherwise throw.</summary>
    private bool _restartDialogOpen;

    private async void RestartHermes_Click(object sender, RoutedEventArgs e)
    {
        if (_restartDialogOpen) return;
        _restartDialogOpen = true;
        try
        {
            await ShowRestartHermesDialogAsync();
        }
        finally
        {
            _restartDialogOpen = false;
        }
    }

    private async Task ShowRestartHermesDialogAsync()
    {
        var info = HermesGatewayProbe.Probe(_hermesConfig.ConfigDirectory);

        var (title, statusLine) = info is not null
            ? ($"Restart Hermes to apply changes",
               $"Hermes gateway is running (PID {info.Pid}).")
            : ($"Start Hermes to apply changes",
               "No running Hermes gateway detected.");

        var explanation = info is not null
            ? "Hermes only reads its MCP server list at startup. Your changes are saved, but the running Hermes won't see them until you restart it. This app can't restart Hermes for you because Hermes was started outside this app."
            : "Hermes only reads its MCP server list at startup. Start Hermes (or restart it if you think it should already be running) and it will pick up your saved changes.";

        var note = info is not null
            ? "This will stop the running Hermes and start a fresh one. Existing chats will end when Hermes restarts. This app will stay open and reconnect automatically once the new Hermes is up."
            : "Run this command in whatever terminal you usually use for Hermes — it will start a fresh gateway with your latest MCP settings.";

        var content = new StackPanel { Spacing = 12 };

        content.Children.Add(new TextBlock
        {
            Text = statusLine,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });
        content.Children.Add(new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock
        {
            Text = "Open PowerShell (or your usual terminal) and run:",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new Border
        {
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"],
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 8),
            Child = new TextBlock
            {
                Text = RestartCommand,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                IsTextSelectionEnabled = true,
            },
        });
        content.Children.Add(new TextBlock
        {
            Text = note,
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });

        var dialog = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = content,
            },
            PrimaryButtonText = "Copy command",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            try
            {
                var pkg = new DataPackage();
                pkg.SetText(RestartCommand);
                Clipboard.SetContent(pkg);
                ShowEditorStatus(InfoBarSeverity.Success, "Command copied",
                    "Paste into your PowerShell terminal to restart Hermes.");
            }
            catch (Exception ex)
            {
                ShowEditorStatus(InfoBarSeverity.Warning, "Couldn't copy",
                    $"Copy the command manually: {RestartCommand}  ({ex.Message})");
            }
        }
    }

    // ---- JSON helpers ------------------------------------------------------

    private static bool TryParseObject(string text, out JsonElement body)
    {
        body = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            body = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Reads the body's <c>enabled</c> field, returning the
    /// boolean if present and JSON-bool-typed, or <c>null</c> for both
    /// "absent" and "present but wrong type." Callers treat null as
    /// "default (true)" — that's Hermes's behaviour too.</summary>
    private static bool? TryReadEnabled(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return null;
        if (!body.TryGetProperty("enabled", out var en)) return null;
        return en.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    /// <summary>Returns a new body with <c>enabled</c> set to false if
    /// <paramref name="enabled"/> is false, or with <c>enabled</c>
    /// removed if true. Other keys (and their order) are preserved.
    /// We don't write <c>enabled: true</c> because that's Hermes's
    /// default — omitting the key keeps configs minimal.</summary>
    private static JsonElement SetOrRemoveEnabled(JsonElement body, bool enabled)
    {
        if (body.ValueKind != JsonValueKind.Object) return body;

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            var wrote = false;
            foreach (var p in body.EnumerateObject())
            {
                if (p.NameEquals("enabled"))
                {
                    if (!enabled)
                    {
                        writer.WriteBoolean("enabled", false);
                        wrote = true;
                    }
                    // else: skip the key — toggling on removes it.
                }
                else
                {
                    p.WriteTo(writer);
                }
            }
            if (!enabled && !wrote)
            {
                writer.WriteBoolean("enabled", false);
            }
            writer.WriteEndObject();
        }

        var bytes = ms.ToArray();
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.Clone();
    }

    /// <summary>Pretty-print a body the same way
    /// <see cref="McpServerItemVm.BodyAsJsonText"/> does so toggle-driven
    /// rewrites match the formatting users see everywhere else.</summary>
    private static string SerializeObject(JsonElement body)
    {
        return JsonSerializer.Serialize(body, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    // ---- Structured form ⇄ raw JSON sync ----------------------------------
    //
    // BodyBox is the canonical source of truth (save/validation/dirty
    // tracking all read from it). The structured form is a secondary
    // editor that pushes its values back into BodyBox whenever the user
    // edits a field, and pulls values out of BodyBox whenever the body
    // is mutated by anything else (direct typing, enabled toggle,
    // load/revert, template pick). Three flags keep the round-trip
    // from looping: _loadingEditor (whole-editor populate),
    // _syncingFormFromBody (form is being written from body), and
    // _syncingBodyFromForm (body is being written from form).

    /// <summary>Populate the structured form fields from
    /// <see cref="BodyBox"/>'s current text. Disables the form and
    /// shows a banner when the body uses field shapes the form can't
    /// safely round-trip (e.g. numeric args, nested objects in env).</summary>
    private void SyncFormFromBody()
    {
        _syncingFormFromBody = true;
        try
        {
            if (!TryParseObject(BodyBox.Text, out var body))
            {
                _isBodyFormCompatible = false;
                ShowFormCompatBanner("Body isn't valid JSON.");
                SetFormControlsEnabled(false);
                return;
            }

            var (compat, reason) = CheckFormCompat(body);
            _isBodyFormCompatible = compat;
            if (!compat)
            {
                ShowFormCompatBanner(reason);
                SetFormControlsEnabled(false);
                return;
            }
            HideFormCompatBanner();
            SetFormControlsEnabled(true);

            // Transport: URL takes precedence — if both are absent we
            // default to stdio because that's the dominant MCP shape.
            var hasUrl = body.TryGetProperty("url", out _);
            if (hasUrl)
            {
                TransportHttp.IsChecked = true;
            }
            else
            {
                TransportStdio.IsChecked = true;
            }
            ApplyTransportVisibility();

            CommandBox.Text = body.TryGetProperty("command", out var cmd)
                              && cmd.ValueKind == JsonValueKind.String
                ? cmd.GetString() ?? ""
                : "";

            Args.Clear();
            if (body.TryGetProperty("args", out var args)
                && args.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in args.EnumerateArray())
                {
                    Args.Add(new EditableStringVm(a.GetString() ?? ""));
                }
            }

            Env.Clear();
            if (body.TryGetProperty("env", out var env)
                && env.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in env.EnumerateObject())
                {
                    Env.Add(new EditableKeyValueVm(p.Name, p.Value.GetString() ?? ""));
                }
            }

            UrlBox.Text = body.TryGetProperty("url", out var url)
                          && url.ValueKind == JsonValueKind.String
                ? url.GetString() ?? ""
                : "";

            Headers.Clear();
            if (body.TryGetProperty("headers", out var headers)
                && headers.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in headers.EnumerateObject())
                {
                    Headers.Add(new EditableKeyValueVm(p.Name, p.Value.GetString() ?? ""));
                }
            }

            if (body.TryGetProperty("timeout", out var t)
                && t.ValueKind == JsonValueKind.Number
                && t.TryGetDouble(out var tv))
            {
                TimeoutBox.Value = tv;
            }
            else
            {
                // NumberBox treats NaN as "no value" and shows the
                // placeholder.
                TimeoutBox.Value = double.NaN;
            }
        }
        finally
        {
            _syncingFormFromBody = false;
        }
    }

    /// <summary>Render the structured form back into
    /// <see cref="BodyBox"/>. Preserves any keys outside
    /// <see cref="ManagedKeys"/> verbatim (enabled, auth, sampling,
    /// tools, supports_parallel_tool_calls, connect_timeout, etc.).
    /// Skipped when the form is incompatible — the user can only
    /// edit raw JSON in that case so there's nothing to render.</summary>
    private void RenderBodyFromForm()
    {
        if (!_isBodyFormCompatible) return;

        var isStdio = TransportStdio.IsChecked == true;

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();

            if (isStdio)
            {
                if (!string.IsNullOrEmpty(CommandBox.Text))
                {
                    writer.WriteString("command", CommandBox.Text);
                }
                if (Args.Count > 0)
                {
                    writer.WriteStartArray("args");
                    foreach (var a in Args)
                    {
                        writer.WriteStringValue(a.Value ?? "");
                    }
                    writer.WriteEndArray();
                }
                if (Env.Count > 0)
                {
                    writer.WriteStartObject("env");
                    foreach (var kv in Env)
                    {
                        // Skip blank-keyed rows so the user can leave a
                        // half-edited row around without it leaking into
                        // the rendered body.
                        if (!string.IsNullOrEmpty(kv.Key))
                        {
                            writer.WriteString(kv.Key, kv.Value ?? "");
                        }
                    }
                    writer.WriteEndObject();
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(UrlBox.Text))
                {
                    writer.WriteString("url", UrlBox.Text);
                }
                if (Headers.Count > 0)
                {
                    writer.WriteStartObject("headers");
                    foreach (var kv in Headers)
                    {
                        if (!string.IsNullOrEmpty(kv.Key))
                        {
                            writer.WriteString(kv.Key, kv.Value ?? "");
                        }
                    }
                    writer.WriteEndObject();
                }
            }

            var t = TimeoutBox.Value;
            if (!double.IsNaN(t))
            {
                if (t == Math.Floor(t) && t >= long.MinValue && t <= long.MaxValue)
                {
                    writer.WriteNumber("timeout", (long)t);
                }
                else
                {
                    writer.WriteNumber("timeout", t);
                }
            }

            // Pass through any keys the form doesn't manage so the
            // Enabled toggle, gateway tool routing, and any
            // hand-edited extras don't get clobbered by a form edit.
            if (TryParseObject(BodyBox.Text, out var existing))
            {
                foreach (var p in existing.EnumerateObject())
                {
                    if (!ManagedKeys.Contains(p.Name))
                    {
                        p.WriteTo(writer);
                    }
                }
            }

            writer.WriteEndObject();
        }

        var json = Encoding.UTF8.GetString(ms.ToArray());

        _syncingBodyFromForm = true;
        try
        {
            BodyBox.Text = json;
            RefreshBodyParseState();
            // Body just changed — the enabled toggle reads from it, so
            // keep that in sync too. SyncFormFromBody is skipped via
            // _syncingBodyFromForm so we don't blow away the user's
            // in-flight typing.
            SyncEditorToggleFromBody();
        }
        finally
        {
            _syncingBodyFromForm = false;
        }
        UpdateButtons();
    }

    /// <summary>Returns (true, "") if every managed key in
    /// <paramref name="body"/> has a shape the form can edit
    /// losslessly. Returns (false, reason) otherwise.</summary>
    private static (bool compat, string reason) CheckFormCompat(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return (false, "Body must be a JSON object.");

        var hasCommand = body.TryGetProperty("command", out var cmd);
        var hasUrl = body.TryGetProperty("url", out var url);

        if (hasCommand && hasUrl)
            return (false, "Both 'command' and 'url' are set; the form edits only one transport at a time.");
        if (hasCommand && cmd.ValueKind != JsonValueKind.String)
            return (false, "'command' isn't a string.");
        if (hasUrl && url.ValueKind != JsonValueKind.String)
            return (false, "'url' isn't a string.");

        if (body.TryGetProperty("args", out var args))
        {
            if (args.ValueKind != JsonValueKind.Array)
                return (false, "'args' isn't an array.");
            foreach (var a in args.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.String)
                    return (false, "'args' contains a non-string value.");
            }
        }

        if (body.TryGetProperty("env", out var env))
        {
            if (env.ValueKind != JsonValueKind.Object)
                return (false, "'env' isn't an object.");
            foreach (var p in env.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.String)
                    return (false, $"'env.{p.Name}' isn't a string.");
            }
        }

        if (body.TryGetProperty("headers", out var headers))
        {
            if (headers.ValueKind != JsonValueKind.Object)
                return (false, "'headers' isn't an object.");
            foreach (var p in headers.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.String)
                    return (false, $"'headers.{p.Name}' isn't a string.");
            }
        }

        if (body.TryGetProperty("timeout", out var t)
            && t.ValueKind != JsonValueKind.Number)
        {
            return (false, "'timeout' isn't a number.");
        }

        return (true, "");
    }

    private void ApplyTransportVisibility()
    {
        var isStdio = TransportStdio.IsChecked == true;
        CommandRow.Visibility = isStdio ? Visibility.Visible : Visibility.Collapsed;
        ArgsSection.Visibility = isStdio ? Visibility.Visible : Visibility.Collapsed;
        EnvSection.Visibility = isStdio ? Visibility.Visible : Visibility.Collapsed;
        UrlRow.Visibility = isStdio ? Visibility.Collapsed : Visibility.Visible;
        HeadersSection.Visibility = isStdio ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetFormControlsEnabled(bool enabled)
    {
        TransportStdio.IsEnabled = enabled;
        TransportHttp.IsEnabled = enabled;
        CommandBox.IsEnabled = enabled;
        UrlBox.IsEnabled = enabled;
        TimeoutBox.IsEnabled = enabled;
        // Panels (Grid/StackPanel) don't expose IsEnabled, so we
        // visually + interactively disable the whole subtree.
        var dim = enabled ? 1.0 : 0.5;
        CommandRow.IsHitTestVisible = enabled;
        CommandRow.Opacity = dim;
        ArgsSection.IsHitTestVisible = enabled;
        ArgsSection.Opacity = dim;
        EnvSection.IsHitTestVisible = enabled;
        EnvSection.Opacity = dim;
        UrlRow.IsHitTestVisible = enabled;
        UrlRow.Opacity = dim;
        HeadersSection.IsHitTestVisible = enabled;
        HeadersSection.Opacity = dim;
    }

    private void ShowFormCompatBanner(string reason)
    {
        FormCompatBar.Message =
            $"{reason} Use the Raw JSON expander below to edit this server.";
        FormCompatBar.IsOpen = true;
    }

    private void HideFormCompatBanner() => FormCompatBar.IsOpen = false;

    // ---- Form change handlers ---------------------------------------------

    private void Transport_Changed(object sender, SelectionChangedEventArgs e)
    {
        ApplyTransportVisibility();
        if (_loadingEditor || _syncingFormFromBody) return;
        RenderBodyFromForm();
    }

    private void FormField_Changed(object sender, object e)
    {
        if (_loadingEditor || _syncingFormFromBody) return;
        RenderBodyFromForm();
    }

    private void ArgItem_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loadingEditor || _syncingFormFromBody) return;
        RenderBodyFromForm();
    }

    private void EnvItem_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loadingEditor || _syncingFormFromBody) return;
        RenderBodyFromForm();
    }

    private void HeaderItem_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loadingEditor || _syncingFormFromBody) return;
        RenderBodyFromForm();
    }

    private void AddArg_Click(object sender, RoutedEventArgs e)
    {
        Args.Add(new EditableStringVm(""));
        if (_loadingEditor || _syncingFormFromBody) return;
        RenderBodyFromForm();
    }

    private void RemoveArg_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is EditableStringVm vm)
        {
            Args.Remove(vm);
            if (_loadingEditor || _syncingFormFromBody) return;
            RenderBodyFromForm();
        }
    }

    private void AddEnv_Click(object sender, RoutedEventArgs e)
    {
        Env.Add(new EditableKeyValueVm("", ""));
        if (_loadingEditor || _syncingFormFromBody) return;
        RenderBodyFromForm();
    }

    private void RemoveEnv_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is EditableKeyValueVm vm)
        {
            Env.Remove(vm);
            if (_loadingEditor || _syncingFormFromBody) return;
            RenderBodyFromForm();
        }
    }

    private void AddHeader_Click(object sender, RoutedEventArgs e)
    {
        Headers.Add(new EditableKeyValueVm("", ""));
        if (_loadingEditor || _syncingFormFromBody) return;
        RenderBodyFromForm();
    }

    private void RemoveHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is EditableKeyValueVm vm)
        {
            Headers.Remove(vm);
            if (_loadingEditor || _syncingFormFromBody) return;
            RenderBodyFromForm();
        }
    }

    // ---- Templates ---------------------------------------------------------
    //
    // Pre-canned working starter bodies for the popular MCP servers.
    // Names are the conventional ones; the user can rename. Tokens
    // and paths are placeholders the user must edit before saving.

    private static (string name, string body) GetTemplate(string id) => id switch
    {
        // Templates pre-fill the editor with known-good config blobs.
        //
        // Windows quirk worth knowing: `npx` is a .cmd shim, not a real
        // executable. Spawning it with CreateProcess (which is what
        // most MCP hosts do under the hood) fails unless you wrap with
        // `cmd /c`. The MCP docs explicitly recommend `"command":
        // "cmd", "args": ["/c", "npx", ...]` on Windows — we follow
        // that for every npx-based template here.
        //
        // The legacy `@modelcontextprotocol/server-github` package is
        // ARCHIVED. The current canonical implementation is the Go
        // server at github/github-mcp-server, distributed as a Docker
        // image — the template below points at that. Users who don't
        // have Docker can swap to the local binary documented in the
        // server's README.
        "filesystem" => ("filesystem", """
            {
              "command": "cmd",
              "args": [
                "/c",
                "npx",
                "-y",
                "@modelcontextprotocol/server-filesystem",
                "C:/Users/Public/Documents"
              ]
            }
            """),
        "git" => ("git", """
            {
              "command": "uvx",
              "args": [
                "mcp-server-git",
                "--repository",
                "C:/path/to/repo"
              ]
            }
            """),
        "github" => ("github", """
            {
              "command": "docker",
              "args": [
                "run",
                "-i",
                "--rm",
                "-e",
                "GITHUB_PERSONAL_ACCESS_TOKEN",
                "ghcr.io/github/github-mcp-server"
              ],
              "env": {
                "GITHUB_PERSONAL_ACCESS_TOKEN": "ghp_replace_me"
              }
            }
            """),
        "memory" => ("memory", """
            {
              "command": "cmd",
              "args": [
                "/c",
                "npx",
                "-y",
                "@modelcontextprotocol/server-memory"
              ]
            }
            """),
        "fetch" => ("fetch", """
            {
              "command": "uvx",
              "args": ["mcp-server-fetch"]
            }
            """),
        "time" => ("time", """
            {
              "command": "uvx",
              "args": ["mcp-server-time"]
            }
            """),
        "sequential-thinking" => ("sequential-thinking", """
            {
              "command": "cmd",
              "args": [
                "/c",
                "npx",
                "-y",
                "@modelcontextprotocol/server-sequential-thinking"
              ]
            }
            """),
        _ => ("new-server", "{}"),
    };
}
