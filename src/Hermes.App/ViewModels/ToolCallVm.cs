using System;
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

    /// <summary>Server-provided summary of what the tool is doing
    /// (e.g. <c>"ls"</c> for terminal, <c>"*.py"</c> for search_files).
    /// Shown next to the name in the header.</summary>
    [ObservableProperty]
    public partial string? Preview { get; set; }

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

    /// <summary>Wall-clock duration in seconds, populated by <c>tool.completed</c>.</summary>
    [ObservableProperty]
    public partial double? DurationSeconds { get; set; }

    /// <summary>Human-readable subtitle: prefers live <see cref="Progress"/>,
    /// falls back to the start-time <see cref="Preview"/>. Lets the header
    /// reuse a single TextBlock for both signals.</summary>
    public string? Subtitle => !string.IsNullOrEmpty(Progress) ? Progress : Preview;

    /// <summary>Compact duration string for the completed-state badge
    /// (e.g. <c>"1.2s"</c>, <c>"340ms"</c>). Empty until tool.completed lands.</summary>
    public string? DurationLabel => DurationSeconds switch
    {
        null => null,
        < 1.0 => $"{(int)Math.Round(DurationSeconds.Value * 1000)}ms",
        _ => $"{DurationSeconds.Value:0.#}s",
    };

    partial void OnProgressChanged(string? value) => OnPropertyChanged(nameof(Subtitle));
    partial void OnPreviewChanged(string? value) => OnPropertyChanged(nameof(Subtitle));
    partial void OnDurationSecondsChanged(double? value) => OnPropertyChanged(nameof(DurationLabel));
}
