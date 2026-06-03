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

    /// <summary>true if <c>enabled: false</c> is set on this server.
    /// We do NOT honour <c>disabled: true</c> for the disabled pill —
    /// that's a typo'd key Hermes does not recognise, surfaced as a
    /// warning by the editor instead.</summary>
    public bool IsDisabled =>
        Body.ValueKind == JsonValueKind.Object
        && Body.TryGetProperty("enabled", out var en)
        && en.ValueKind == JsonValueKind.False;

    public void Update(string name, JsonElement body)
    {
        Name = name;
        Body = body;
        OnPropertyChanged(nameof(Transport));
        OnPropertyChanged(nameof(IsDisabled));
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
