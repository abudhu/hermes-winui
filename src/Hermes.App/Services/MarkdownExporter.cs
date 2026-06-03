using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Hermes.App.ViewModels;
using Hermes.ApiClient.Models;

namespace Hermes.App.Services;

/// <summary>
/// Renders a chat transcript to a portable Markdown string. Pure data
/// helper — no clipboard, no file picker, no dispatcher. Designed this
/// way so the VM-level commands can compose this output and then do their
/// own UI work without the formatter ever touching WinUI APIs.
///
/// <para>Format (matches the spec in the export task):</para>
/// <list type="bullet">
///   <item>H1: session title (or a generated fallback).</item>
///   <item>Optional H2: date + model + session-total tokens.</item>
///   <item>Alternating <c>**You**</c> / <c>**Assistant**</c> blocks with
///         blank-line spacing guards so embedded code fences remain valid.</item>
///   <item>System messages and tool-call internals are skipped (tool args /
///         tool output can be noisy or sensitive; users that want them can
///         copy individual cards from the UI).</item>
/// </list>
/// </summary>
internal static class MarkdownExporter
{
    /// <summary>Snapshot fed into the renderer. Keeps the exporter
    /// decoupled from the live VM so it stays trivially testable.</summary>
    public sealed record ExportInput(
        string? Title,
        string? SessionId,
        string? Model,
        UsageStats? Usage,
        IReadOnlyList<ExportMessage> Messages);

    public sealed record ExportMessage(
        MessageRole Role,
        string Content,
        DateTimeOffset Timestamp);

    /// <summary>Adapter from the live VMs to the pure input type. Lives
    /// here so callers don't have to know the VM shape — they hand us a
    /// snapshot collection and we project it.</summary>
    public static ExportInput FromViewModel(
        string? title,
        string? sessionId,
        string? model,
        UsageStats? usage,
        IReadOnlyList<MessageVm> messages)
    {
        var projected = new List<ExportMessage>(messages.Count);
        foreach (var m in messages)
        {
            // Skip system role entirely — the chat UI never shows them, and
            // the export shouldn't either.
            if (m.Role == MessageRole.System) continue;
            projected.Add(new ExportMessage(m.Role, m.Content ?? string.Empty, m.Timestamp));
        }
        return new ExportInput(title, sessionId, model, usage, projected);
    }

    /// <summary>Renders the input as a single Markdown string. Always
    /// terminates with a trailing newline so a saved .md file ends
    /// cleanly per POSIX convention.</summary>
    public static string Render(ExportInput input)
    {
        var sb = new StringBuilder();

        var titleLine = string.IsNullOrWhiteSpace(input.Title)
            ? "Hermes chat"
            : input.Title!.Trim();
        sb.Append("# ").AppendLine(titleLine);
        sb.AppendLine();

        var subtitle = BuildSubtitleLine(input);
        if (subtitle.Length > 0)
        {
            sb.Append("## ").AppendLine(subtitle);
            sb.AppendLine();
        }

        for (int i = 0; i < input.Messages.Count; i++)
        {
            var msg = input.Messages[i];
            var roleLabel = msg.Role == MessageRole.User ? "You" : "Hermes";
            // Local timestamp in short form alongside the role label — gives
            // the reader a sense of pacing without dominating the heading.
            var stamp = msg.Timestamp.LocalDateTime.ToString("t", CultureInfo.CurrentCulture);

            sb.Append("**").Append(roleLabel).Append("** · ").AppendLine(stamp);
            sb.AppendLine();

            // Don't wrap message content — it's already markdown. Trimming
            // trailing whitespace prevents an "extra" blank line when the
            // model emits a content-terminating \n.
            sb.AppendLine(msg.Content.TrimEnd());
            sb.AppendLine();
        }

        // Ensure a single trailing newline (not two — already AppendLine'd
        // after the last message). StringBuilder.AppendLine appends "\r\n"
        // on Windows; we leave the platform default in place.
        return sb.ToString();
    }

    /// <summary>Date + model + token totals, joined with " · ". Empty when
    /// none of those are populated so the H2 line can be skipped cleanly.</summary>
    private static string BuildSubtitleLine(ExportInput input)
    {
        var parts = new List<string>(3);

        var nowLocal = DateTimeOffset.Now.LocalDateTime;
        // Long date so a re-opened file two months later still reads
        // unambiguously, without forcing a region-specific format.
        parts.Add(nowLocal.ToString("dddd, MMMM d, yyyy 'at' h:mm tt", CultureInfo.CurrentCulture));

        if (!string.IsNullOrWhiteSpace(input.Model))
        {
            parts.Add($"model: {input.Model}");
        }

        // Reuse the VM's compact-mode formatter so the export reads the
        // same as the header chip in the UI.
        var usageLine = MessageVm.FormatUsage(input.Usage, compact: true);
        if (usageLine.Length > 0)
        {
            parts.Add(usageLine);
        }

        return string.Join(" · ", parts);
    }

    /// <summary>Turns an arbitrary session title into a safe filename stem
    /// (no extension). Falls back to a timestamped name when the title is
    /// empty or sanitizes to nothing.</summary>
    public static string SanitizeFileName(string? title)
    {
        var fallback = $"hermes-chat-{DateTimeOffset.Now.LocalDateTime:yyyyMMdd-HHmm}";
        if (string.IsNullOrWhiteSpace(title)) return fallback;

        var trimmed = title!.Trim();
        // Strip filesystem-invalid characters plus a few that confuse shells
        // (quotes, pipes, etc. that aren't in GetInvalidFileNameChars on
        // some platforms but still cause grief).
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
        foreach (var extra in new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' })
        {
            invalid.Add(extra);
        }

        var cleaned = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            if (invalid.Contains(ch) || char.IsControl(ch)) cleaned.Append('-');
            else cleaned.Append(ch);
        }
        // Collapse runs of dashes/spaces so a title like "Foo / Bar" doesn't
        // become "Foo - Bar" with leftover whitespace asymmetry.
        var collapsed = System.Text.RegularExpressions.Regex.Replace(
            cleaned.ToString(), @"[-\s]+", "-").Trim('-', '.');

        // Windows MAX_PATH leaves us some room; 80 chars is generous for a
        // filename stem while staying well under any practical limit.
        if (collapsed.Length > 80) collapsed = collapsed.Substring(0, 80).TrimEnd('-');

        return collapsed.Length == 0 ? fallback : collapsed;
    }
}
