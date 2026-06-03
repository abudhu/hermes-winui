namespace Hermes.ApiClient.Models;

/// <summary>
/// Discriminated union of Hermes SSE events. We parse the SSE frame field by
/// field, then dispatch on the <c>event:</c> name. Any payload we don't model
/// becomes <see cref="UnknownStreamEvent"/> — the UI logs it but keeps streaming,
/// because Hermes is allowed to add new event types without notice.
/// </summary>
public abstract record ChatStreamEvent
{
    /// <summary>The raw <c>event:</c> name as it came off the wire.</summary>
    public string RawEvent { get; init; } = "";
    /// <summary>The raw <c>data:</c> payload (multi-line concatenated, newline-joined).</summary>
    public string RawData { get; init; } = "";
}

/// <summary>Incremental token from the assistant.</summary>
public sealed record AssistantDeltaEvent : ChatStreamEvent
{
    public string Text { get; init; } = "";
}

/// <summary>Tool call has been dispatched.</summary>
public sealed record ToolStartedEvent : ChatStreamEvent
{
    public string? Name { get; init; }
    public string? CallId { get; init; }
    public string? ArgumentsJson { get; init; }
    /// <summary>Short summary the gateway provides describing what this
    /// invocation is doing (e.g. <c>"ls"</c> for terminal, <c>"*.py"</c>
    /// for search_files). Shown as a subtitle in the tool card.</summary>
    public string? Preview { get; init; }
}

/// <summary>Tool call returned a result.</summary>
public sealed record ToolCompletedEvent : ChatStreamEvent
{
    public string? Name { get; init; }
    public string? CallId { get; init; }
    public string? OutputText { get; init; }
    public string? OutputJson { get; init; }
    public bool IsError { get; init; }
    /// <summary>Wall-clock duration in seconds (from the gateway).</summary>
    public double? DurationSeconds { get; init; }
}

/// <summary>Progress update from inside a running tool (optional).</summary>
public sealed record ToolProgressEvent : ChatStreamEvent
{
    public string? Name { get; init; }
    public string? CallId { get; init; }
    public string? Message { get; init; }
}

/// <summary>Terminal: the assistant message finished. Carries the full content
/// so we can recover even if deltas were dropped along the way.</summary>
public sealed record AssistantCompletedEvent : ChatStreamEvent
{
    public string? Content { get; init; }
    public string? MessageId { get; init; }
}

/// <summary>Terminal: the whole run completed.</summary>
public sealed record RunCompletedEvent : ChatStreamEvent
{
    public string? Output { get; init; }
    public string? UsageJson { get; init; }
    public string? RunId { get; init; }
    /// <summary>Final messages array from run.completed (assistant content is here).</summary>
    public string? FinalAssistantContent { get; init; }
    /// <summary>Per-run token accounting parsed out of <see cref="UsageJson"/>;
    /// <see langword="null"/> when the gateway didn't ship a usage object or
    /// its shape was unrecognized.</summary>
    public UsageStats? Usage { get; init; }
}

/// <summary>Terminal: the run errored.</summary>
public sealed record StreamErrorEvent : ChatStreamEvent
{
    public string Message { get; init; } = "";
}

/// <summary>Anything we don't know about — keep going.</summary>
public sealed record UnknownStreamEvent : ChatStreamEvent;

public sealed record SessionChatRequest(
    string Input,
    string? Instructions = null
);
