namespace Hermes.App.ViewModels;

public enum MessageRole
{
    User,
    Assistant,
    System,
}

public enum MessageState
{
    /// <summary>The user just typed it — no server interaction yet.</summary>
    Authored,
    /// <summary>Server is actively producing tokens / tool calls.</summary>
    Streaming,
    /// <summary>Run reached <c>run.completed</c> cleanly.</summary>
    Completed,
    /// <summary>User cancelled via Stop.</summary>
    Stopped,
    /// <summary>Server sent an explicit error event.</summary>
    Failed,
    /// <summary>The SSE connection dropped without a terminal event.</summary>
    Disconnected,
}
