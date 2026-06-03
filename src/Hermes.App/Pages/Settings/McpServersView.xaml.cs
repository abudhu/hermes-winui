using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using Hermes.ApiClient;
using Hermes.App.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
    /// (selection change, revert, post-save resync) so
    /// <see cref="Editor_Changed"/> doesn't false-positive dirty.</summary>
    private bool _suppressDirty;

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

        Servers.Clear();
        foreach (var entry in entries)
        {
            Servers.Add(McpServerItemVm.From(entry));
        }
        UpdateEmptyHint();

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

    private void UpdateButtons()
    {
        var dirty = IsEditorDirty;
        // The Save button is enabled whenever the form has content (a
        // new server with empty fields is also "savable" so validation
        // can run and surface specific errors).
        SaveButton.IsEnabled = _selected is null
            ? !string.IsNullOrWhiteSpace(NameBox.Text) || !string.IsNullOrWhiteSpace(BodyBox.Text)
            : dirty;
        RevertButton.IsEnabled = dirty;
        RemoveButton.IsEnabled = _selected is not null;
        DirtyHint.Text = dirty ? "Unsaved changes" : "";
    }

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        _suppressDirty = true;
        try
        {
            NameBox.Text = _originalName;
            BodyBox.Text = _originalBody;
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
}
