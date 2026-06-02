using System.Text.Json.Serialization;

namespace Hermes.ApiClient.Models;

public sealed record SessionList(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("data")] List<SessionSummary> Data,
    [property: JsonPropertyName("limit")] int? Limit,
    [property: JsonPropertyName("offset")] int? Offset,
    [property: JsonPropertyName("has_more")] bool HasMore
);

/// <summary>
/// One row from GET /api/sessions. Models only the fields the tray uses;
/// System.Text.Json silently ignores the rest (token counts, costs, etc.)
/// so we can add them later without an API breaking change forcing a rebuild.
/// </summary>
public sealed record SessionSummary(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("started_at")] double? StartedAt,
    [property: JsonPropertyName("ended_at")] double? EndedAt,
    [property: JsonPropertyName("last_active")] double? LastActive,
    [property: JsonPropertyName("message_count")] int? MessageCount,
    [property: JsonPropertyName("parent_session_id")] string? ParentSessionId,
    [property: JsonPropertyName("preview")] string? Preview
)
{
    /// <summary>True when the session has not been formally ended.</summary>
    public bool IsOpen => EndedAt is null;

    /// <summary>Time since the session last had any activity (using server's last_active epoch).</summary>
    public TimeSpan? SinceActive =>
        LastActive is double t
            ? DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds((long)(t * 1000))
            : null;
}
