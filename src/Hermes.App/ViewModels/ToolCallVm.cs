using CommunityToolkit.Mvvm.ComponentModel;

namespace Hermes.App.ViewModels;

/// <summary>
/// Inline tool-call card rendered as an Expander inside an assistant message.
/// Created on <c>tool.started</c>, mutated by <c>tool.progress</c> and
/// <c>tool.completed</c>.
/// </summary>
public sealed partial class ToolCallVm : ObservableObject
{
    /// <summary>Server-issued correlation id; used to match start → progress → completed.</summary>
    public string? CallId { get; init; }

    [ObservableProperty]
    public partial string Name { get; set; } = "(tool)";

    [ObservableProperty]
    public partial string? ArgumentsJson { get; set; }

    [ObservableProperty]
    public partial string? Output { get; set; }

    [ObservableProperty]
    public partial string? Progress { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; } = true;

    [ObservableProperty]
    public partial bool IsError { get; set; }
}
