using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
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

    /// <summary>True while we're mutating the boxes programmatically
    /// (selection change, revert, post-save resync, toggle⇄body sync)
    /// so the various *_Changed and *_Toggled handlers don't false-
    /// positive dirty or trigger feedback loops.</summary>
    private bool _suppressDirty;

    /// <summary>Tracks whether <see cref="BodyBox"/>'s current text
    /// parses as a JSON object. The editor Enabled toggle disables
    /// itself when this is false so it never lies about state. Kept
    /// as a field rather than recomputed on demand because the
    /// Toggled handler needs to short-circuit fast.</summary>
    private bool _bodyIsValidJsonObject = true;

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

        _selected = vm;
        EditorTitle.Text = $"Edit server '{vm.Name}'";

        _suppressDirty = true;
        try
        {
            NameBox.Text = vm.Name;
            BodyBox.Text = vm.BodyAsJsonText();
            _originalName = NameBox.Text;
            _originalBody = BodyBox.Text;
            RefreshBodyParseState();
            SyncEditorToggleFromBody();
        }
        finally
        {
            _suppressDirty = false;
        }
        UpdateButtons();
        EditorStatusBar.IsOpen = false;
    }

    private void NewServer_Click(object sender, RoutedEventArgs e)
    {
        ServerList.SelectedItem = null;
        ShowNewServerForm();
    }

    private void ShowNewServerForm()
    {
        _selected = null;
        EditorTitle.Text = "New server";

        _suppressDirty = true;
        try
        {
            NameBox.Text = "";
            BodyBox.Text = """
                {
                  "command": "uvx",
                  "args": ["mcp-server-time"]
                }
                """;
            _originalName = "";
            _originalBody = "";
            RefreshBodyParseState();
            SyncEditorToggleFromBody();
        }
        finally
        {
            _suppressDirty = false;
        }
        UpdateButtons();
        EditorStatusBar.IsOpen = false;
    }

    private void Editor_Changed(object sender, object e)
    {
        if (_suppressDirty) return;
        UpdateButtons();
    }

    /// <summary>BodyBox text-changed handler. Splits off from
    /// <see cref="Editor_Changed"/> because edits to the JSON body have
    /// the extra side effect of re-syncing the Enabled toggle (and
    /// disabling it when the body becomes invalid JSON).</summary>
    private void Body_Changed(object sender, object e)
    {
        if (_suppressDirty) return;
        RefreshBodyParseState();
        SyncEditorToggleFromBody();
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
    }

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        _suppressDirty = true;
        try
        {
            NameBox.Text = _originalName;
            BodyBox.Text = _originalBody;
            RefreshBodyParseState();
            SyncEditorToggleFromBody();
        }
        finally
        {
            _suppressDirty = false;
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
            _suppressDirty = true;
            try
            {
                EditorEnabledToggle.IsEnabled = true;
                EditorEnabledToggle.IsOn = newIsOn;
            }
            finally
            {
                _suppressDirty = false;
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
        if (_suppressDirty) return;
        if (sender is not ToggleSwitch ts) return;
        if (!_bodyIsValidJsonObject)
        {
            // Should be unreachable (toggle is disabled) — but defend.
            return;
        }
        if (!TryParseObject(BodyBox.Text, out var body)) return;

        var newJsonText = SerializeObject(SetOrRemoveEnabled(body, ts.IsOn));

        _suppressDirty = true;
        try
        {
            BodyBox.Text = newJsonText;
            RefreshBodyParseState();
        }
        finally
        {
            _suppressDirty = false;
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
        if (_suppressDirty) return;
        if (sender is not ToggleSwitch ts) return;
        if (ts.DataContext is not McpServerItemVm vm) return;

        // Defensive: AreRowTogglesEnabled binding should already gate
        // this, but a race during dirty-state transitions could let one
        // tap through. Snap back without saving rather than write a
        // partial state with stale editor edits hanging around.
        if (IsEditorDirty)
        {
            _suppressDirty = true;
            try { ts.IsOn = vm.IsEnabled; }
            finally { _suppressDirty = false; }
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
            _suppressDirty = true;
            try { ts.IsOn = vm.IsEnabled; }
            finally { _suppressDirty = false; }
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
            _suppressDirty = true;
            try { ts.IsOn = vm.IsEnabled; }
            finally { _suppressDirty = false; }
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
}
