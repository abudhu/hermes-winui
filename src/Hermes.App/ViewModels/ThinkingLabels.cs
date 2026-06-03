using System;

namespace Hermes.App.ViewModels;

/// <summary>
/// Picks a playful "thinking" indicator (face + verb) to display while an
/// assistant message is streaming.
///
/// <para>
/// The verbs and kawaii faces are copied verbatim from the Hermes CLI's
/// <c>KawaiiSpinner</c> defaults (<c>agent/display.py</c>: <c>THINKING_VERBS</c>
/// and <c>KAWAII_THINKING</c>) so the UI matches the vibe of the official
/// terminal client. Hermes picks one combo per API call, not per token —
/// we do the same: <see cref="Pick"/> is called when a new assistant
/// message starts streaming and the result is held for the whole turn.
/// </para>
///
/// <para>
/// These are picked on the client because Hermes doesn't put them on the
/// SSE stream: the gateway only emits <c>tool.progress</c> events with
/// <c>tool_name="_thinking"</c> carrying the actual reasoning text. The
/// CLI's spinner verb is local flavor.
/// </para>
/// </summary>
internal static class ThinkingLabels
{
    /// <summary>From <c>agent/display.py: KawaiiSpinner.THINKING_VERBS</c>.</summary>
    private static readonly string[] Verbs =
    [
        "pondering", "contemplating", "musing", "cogitating", "ruminating",
        "deliberating", "mulling", "reflecting", "processing", "reasoning",
        "analyzing", "computing", "synthesizing", "formulating", "brainstorming",
    ];

    /// <summary>From <c>agent/display.py: KawaiiSpinner.KAWAII_THINKING</c>.</summary>
    private static readonly string[] Faces =
    [
        "(｡•́︿•̀｡)", "(◔_◔)", "(¬‿¬)", "( •_•)>⌐■-■", "(⌐■_■)",
        "(´･_･`)", "◉_◉", "(°ロ°)", "( ˘⌣˘)♡", "ヽ(>∀<☆)☆",
        "٩(๑❛ᴗ❛๑)۶", "(⊙_⊙)", "(¬_¬)", "( ͡° ͜ʖ ͡°)", "ಠ_ಠ",
    ];

    /// <summary>
    /// <see cref="Random.Shared"/> is thread-safe and we never seed
    /// deterministically (the flavor is the whole point).
    /// </summary>
    public static string Pick()
    {
        var face = Faces[Random.Shared.Next(Faces.Length)];
        var verb = Verbs[Random.Shared.Next(Verbs.Length)];
        return $"{face} {verb}…";
    }
}
