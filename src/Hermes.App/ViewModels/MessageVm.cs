using System;
using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Hermes.App.ViewModels;

/// <summary>
/// One message in the chat transcript. Backs the data template selector on
/// <c>ChatPage</c>, so role + state determine which bubble template renders.
///
/// <para>
/// Assistant streaming uses a <see cref="StringBuilder"/> internally; we
/// flush to the bound <see cref="Content"/> via a 60ms timer in
/// <see cref="ChatViewModel"/> so we don't fire INotifyPropertyChanged on
/// every token — that pegs the UI thread and can drop tokens.
/// </para>
/// </summary>
public sealed partial class MessageVm : ObservableObject
{
    /// <summary>Internal buffer used during streaming; flushed to Content periodically.</summary>
    public StringBuilder Buffer { get; } = new();

    /// <summary>Internal buffer for reasoning ("_thinking") tokens; flushed periodically.</summary>
    public StringBuilder ReasoningBuffer { get; } = new();

    public MessageRole Role { get; init; }

    /// <summary>Timestamp captured locally when the message was created.</summary>
    public DateTimeOffset Timestamp { get; } = DateTimeOffset.Now;

    /// <summary>
    /// Playful spinner label shown while this message is streaming
    /// (e.g. "( ͡° ͜ʖ ͡°) cogitating…"). Picked once per turn from the same
    /// verb/face pool as the Hermes CLI's KawaiiSpinner so the UI matches
    /// the terminal client's vibe. Set by <see cref="ChatViewModel"/> when
    /// the message is created; never changes mid-stream.
    /// </summary>
    public string ThinkingLabel { get; init; } = "Thinking…";

    [ObservableProperty]
    public partial string Content { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Reasoning { get; set; } = string.Empty;

    [ObservableProperty]
    public partial MessageState State { get; set; } = MessageState.Authored;

    /// <summary>Inline tool calls that occurred during this assistant turn.</summary>
    public ObservableCollection<ToolCallVm> ToolCalls { get; } = [];

    /// <summary>True while the server is still streaming this message.</summary>
    public bool IsStreaming => State == MessageState.Streaming;

    /// <summary>True if the model emitted any reasoning trace this turn.</summary>
    public bool HasReasoning => !string.IsNullOrEmpty(Reasoning);

    /// <summary>True if this message has any inline tool cards. Drives the
    /// tool-card ItemsRepeater's Visibility so an empty container doesn't
    /// leak StackPanel spacing above the Content text and push the bubble's
    /// contents off-center.</summary>
    public bool HasToolCalls => ToolCalls.Count > 0;

    partial void OnStateChanged(MessageState value) => OnPropertyChanged(nameof(IsStreaming));
    partial void OnReasoningChanged(string value) => OnPropertyChanged(nameof(HasReasoning));

    public MessageVm()
    {
        // ObservableCollection raises CollectionChanged whenever items are
        // added/removed; we use that to re-raise HasToolCalls for the binding.
        ToolCalls.CollectionChanged += (_, __) => OnPropertyChanged(nameof(HasToolCalls));
    }
}
