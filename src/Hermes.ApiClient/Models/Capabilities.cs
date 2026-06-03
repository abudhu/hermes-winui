using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hermes.ApiClient.Models;

/// <summary>
/// Subset of GET /v1/capabilities used by the tray. The endpoint returns much more —
/// we model only the fields Phase 1 needs and let System.Text.Json ignore the rest.
/// </summary>
/// <remarks>
/// <c>features</c> values used to be bools, but the gateway now mixes in
/// string-valued "informational" features (e.g. <c>session_continuity_header</c>
/// returns the header name <c>"X-Hermes-Session-Id"</c>). Modelling as
/// <see cref="JsonElement"/> stops the deserializer crashing on the whole
/// payload when a new non-bool feature appears; the ViewModel layer decides
/// what "enabled" means per kind.
/// </remarks>
public sealed record Capabilities(
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("features")] Dictionary<string, JsonElement>? Features
);
