using System.Text.Json.Serialization;

namespace Hermes.ApiClient.Models;

/// <summary>
/// Wraps the GET /api/jobs response. The gateway returns `{"jobs": [...]}`
/// rather than the OpenAI-style `{object, data}` envelope.
/// </summary>
public sealed record JobList(
    [property: JsonPropertyName("jobs")] List<Job> Jobs
);

public sealed record Job(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("prompt")] string? Prompt,
    [property: JsonPropertyName("schedule")] string? Schedule,
    [property: JsonPropertyName("enabled")] bool? Enabled,
    [property: JsonPropertyName("paused")] bool? Paused,
    [property: JsonPropertyName("provider")] string? Provider,
    [property: JsonPropertyName("skills")] List<string>? Skills,
    [property: JsonPropertyName("last_run_at")] double? LastRunAt,
    [property: JsonPropertyName("next_run_at")] double? NextRunAt,
    [property: JsonPropertyName("last_status")] string? LastStatus,
    [property: JsonPropertyName("delivery_target")] string? DeliveryTarget
);
