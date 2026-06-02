using System.Text.Json.Serialization;

namespace Hermes.ApiClient.Models;

public sealed record ModelList(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("data")] List<ModelInfo> Data
);

public sealed record ModelInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("owned_by")] string? OwnedBy
);
