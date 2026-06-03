using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Hermes.ApiClient;

namespace Hermes.App.ViewModels.Settings;

/// <summary>
/// Display-time view-model for one row in the MCP servers list. Owns
/// the canonical <see cref="JsonElement"/> body so the editor can serve
/// it as a JSON string and re-parse on save without losing types.
/// </summary>
public sealed partial class McpServerItemVm : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; }

    /// <summary>The parsed body (an object) as YAML→JSON. Source of
    /// truth for what's saved on disk for this server.</summary>
    public JsonElement Body { get; private set; }

    public McpServerItemVm(string name, JsonElement body)
    {
        Name = name;
        Body = body;
    }

    /// <summary>"stdio" / "http" / "custom" — derived from
    /// <see cref="Body"/>.</summary>
    public string Transport => GetTransport(Body);

    /// <summary>True when <c>enabled: false</c> is set on this server.
    /// Hermes's startup filter (<c>tools/mcp_tool.py</c>) reads
    /// <c>enabled</c> with a default of <c>true</c>, so absence of the
    /// key means enabled. We never write the default — toggling on
    /// removes the field, toggling off writes <c>enabled: false</c>.</summary>
    public bool IsDisabled =>
        Body.ValueKind == JsonValueKind.Object
        && Body.TryGetProperty("enabled", out var en)
        && en.ValueKind == JsonValueKind.False;

    /// <summary>True when the server is enabled (the negation of
    /// <see cref="IsDisabled"/>, exposed separately for two-way binding
    /// onto the row's <c>ToggleSwitch.IsOn</c>).</summary>
    public bool IsEnabled => !IsDisabled;

    public void Update(string name, JsonElement body)
    {
        Name = name;
        Body = body;
        OnPropertyChanged(nameof(Transport));
        OnPropertyChanged(nameof(IsDisabled));
        OnPropertyChanged(nameof(IsEnabled));
    }

    public static string GetTransport(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return "custom";
        if (body.TryGetProperty("url", out _)) return "http";
        if (body.TryGetProperty("command", out _)) return "stdio";
        return "custom";
    }

    public static McpServerItemVm From(McpServerEntry entry)
        => new(entry.Name, entry.Body);

    /// <summary>Pretty-prints <see cref="Body"/> as 2-space-indented
    /// JSON for the editor textarea.</summary>
    public string BodyAsJsonText()
    {
        return JsonSerializer.Serialize(Body, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }
}
