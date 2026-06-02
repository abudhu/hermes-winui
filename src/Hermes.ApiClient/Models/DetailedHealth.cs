using System.Text.Json.Serialization;

namespace Hermes.ApiClient.Models;

/// <summary>
/// Response from GET /health/detailed — the workhorse endpoint for the tray.
/// Drives the status dot color, the active-agents indicator, and the per-platform
/// bridge health list in the context menu.
/// </summary>
public sealed record DetailedHealth(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("gateway_state")] string GatewayState,
    [property: JsonPropertyName("platforms")] Dictionary<string, PlatformStatus>? Platforms,
    [property: JsonPropertyName("active_agents")] int ActiveAgents,
    [property: JsonPropertyName("exit_reason")] string? ExitReason,
    [property: JsonPropertyName("updated_at")] string? UpdatedAt,
    [property: JsonPropertyName("pid")] int? Pid
);

public sealed record PlatformStatus(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("error_code")] string? ErrorCode,
    [property: JsonPropertyName("error_message")] string? ErrorMessage,
    [property: JsonPropertyName("updated_at")] string? UpdatedAt
);
