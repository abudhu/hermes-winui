using System.Text.Json.Serialization;

namespace Hermes.ApiClient.Models;

/// <summary>
/// Envelope around a single session returned by GET /api/sessions/{id}.
/// Shape: <c>{ "object": "hermes.session", "session": {...} }</c>.
/// </summary>
public sealed record SessionDetailEnvelope(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("session")] SessionDetail Session
);

public sealed record SessionDetail(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("user_id")] string? UserId,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("started_at")] double? StartedAt,
    [property: JsonPropertyName("ended_at")] double? EndedAt,
    [property: JsonPropertyName("end_reason")] string? EndReason,
    [property: JsonPropertyName("message_count")] int? MessageCount,
    [property: JsonPropertyName("tool_call_count")] int? ToolCallCount,
    [property: JsonPropertyName("input_tokens")] long? InputTokens,
    [property: JsonPropertyName("output_tokens")] long? OutputTokens,
    [property: JsonPropertyName("cache_read_tokens")] long? CacheReadTokens,
    [property: JsonPropertyName("cache_write_tokens")] long? CacheWriteTokens,
    [property: JsonPropertyName("reasoning_tokens")] long? ReasoningTokens,
    [property: JsonPropertyName("estimated_cost_usd")] double? EstimatedCostUsd,
    [property: JsonPropertyName("actual_cost_usd")] double? ActualCostUsd,
    [property: JsonPropertyName("api_call_count")] int? ApiCallCount,
    [property: JsonPropertyName("parent_session_id")] string? ParentSessionId,
    [property: JsonPropertyName("has_system_prompt")] bool? HasSystemPrompt,
    [property: JsonPropertyName("has_model_config")] bool? HasModelConfig
);

public sealed record SessionMessageList(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("data")] List<SessionMessage> Data
);

public sealed record SessionMessage(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("tool_call_id")] string? ToolCallId,
    [property: JsonPropertyName("tool_name")] string? ToolName,
    [property: JsonPropertyName("timestamp")] double? Timestamp,
    [property: JsonPropertyName("token_count")] int? TokenCount,
    [property: JsonPropertyName("finish_reason")] string? FinishReason,
    [property: JsonPropertyName("reasoning")] string? Reasoning,
    [property: JsonPropertyName("reasoning_content")] string? ReasoningContent
);

public sealed record CreateSessionRequest(
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("source")] string? Source = "api",
    [property: JsonPropertyName("model")] string? Model = null
);

public sealed record CreateSessionResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("session")] SessionDetail Session
);
