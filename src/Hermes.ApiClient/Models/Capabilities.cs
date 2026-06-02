using System.Text.Json.Serialization;

namespace Hermes.ApiClient.Models;

/// <summary>
/// Subset of GET /v1/capabilities used by the tray. The endpoint returns much more —
/// we model only the fields Phase 1 needs and let System.Text.Json ignore the rest.
/// </summary>
public sealed record Capabilities(
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("features")] Dictionary<string, bool>? Features
);
