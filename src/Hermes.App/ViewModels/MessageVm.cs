using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Hermes.ApiClient.Models;

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

    /// <summary>Timestamp this message was authored. For new messages this
    /// defaults to <see cref="DateTimeOffset.Now"/>; for messages hydrated
    /// from server history (<c>ResumeSessionAsync</c>) it's the persisted
    /// timestamp converted from epoch seconds.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

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

    /// <summary>
    /// Per-turn token accounting once <c>run.completed</c> arrives, or a
    /// best-effort approximation built from <c>SessionMessage.TokenCount</c>
    /// for messages hydrated from server history. <see langword="null"/>
    /// while streaming and for hydrated messages with no recorded count.
    /// </summary>
    [ObservableProperty]
    public partial UsageStats? Usage { get; set; }

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

    /// <summary>True when <see cref="Usage"/> has any tokens worth showing.
    /// Drives the per-message usage footer's Visibility so the row doesn't
    /// reserve dead space on bubbles without usage data (e.g. user turns).</summary>
    public bool HasUsage => Usage is { HasAny: true };

    /// <summary>Formatted one-liner for the per-message usage footer.
    /// Empty string when there's nothing to show — bindings should also
    /// gate on <see cref="HasUsage"/> for Visibility.</summary>
    public string UsageLine => FormatUsage(Usage, compact: false);

    /// <summary>Display name for the message header ("You" or "Hermes").
    /// Computed from <see cref="Role"/> which is init-only, so this is
    /// effectively a constant for the lifetime of the VM.</summary>
    public string RoleLabel => Role == MessageRole.User ? "You" : "Hermes";

    /// <summary>Short local time the message was authored (e.g. "2:05 PM").
    /// Uses the current culture so users in 24h locales see "14:05".</summary>
    public string TimeLabel => Timestamp.LocalDateTime.ToString("t", CultureInfo.CurrentCulture);

    partial void OnStateChanged(MessageState value) => OnPropertyChanged(nameof(IsStreaming));
    partial void OnReasoningChanged(string value) => OnPropertyChanged(nameof(HasReasoning));
    partial void OnUsageChanged(UsageStats? value)
    {
        OnPropertyChanged(nameof(HasUsage));
        OnPropertyChanged(nameof(UsageLine));
    }

    public MessageVm()
    {
        // ObservableCollection raises CollectionChanged whenever items are
        // added/removed; we use that to re-raise HasToolCalls for the binding.
        ToolCalls.CollectionChanged += (_, __) => OnPropertyChanged(nameof(HasToolCalls));
    }

    /// <summary>Formats a usage snapshot for display. Returns an empty
    /// string when nothing useful would render. <paramref name="compact"/>
    /// shortens with K/M suffixes (for the session-total chip in the
    /// header where horizontal space is tight).</summary>
    public static string FormatUsage(UsageStats? u, bool compact)
    {
        if (u is null || !u.HasAny) return string.Empty;
        var sb = new StringBuilder();
        if (u.InputTokens is long inT)
        {
            sb.Append('\u2193').Append(' ').Append(FormatCount(inT, compact)).Append(" in");
        }
        if (u.OutputTokens is long outT)
        {
            if (sb.Length > 0) sb.Append(" \u00b7 ");
            sb.Append('\u2191').Append(' ').Append(FormatCount(outT, compact)).Append(" out");
        }
        if (u.CachedReadTokens is long cr && cr > 0)
        {
            if (sb.Length > 0) sb.Append(" \u00b7 ");
            sb.Append(FormatCount(cr, compact)).Append(" cached");
        }
        if (u.ReasoningTokens is long rt && rt > 0)
        {
            if (sb.Length > 0) sb.Append(" \u00b7 ");
            sb.Append(FormatCount(rt, compact)).Append(" reasoning");
        }
        return sb.ToString();
    }

    private static string FormatCount(long n, bool compact)
    {
        if (!compact) return n.ToString("N0", CultureInfo.CurrentCulture);
        if (n >= 1_000_000) return (n / 1_000_000.0).ToString("0.#", CultureInfo.CurrentCulture) + "M";
        if (n >= 1_000) return (n / 1_000.0).ToString("0.#", CultureInfo.CurrentCulture) + "K";
        return n.ToString("N0", CultureInfo.CurrentCulture);
    }
}
