using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Hermes.ApiClient;

/// <summary>
/// Builds the outbound user-message string from the composer prose plus
/// a list of attachments. Every successfully-classified attachment — file
/// or image — is appended as a bare absolute path on its own line.
/// That's the format the Hermes agent already recognizes (matches the
/// "path-in-the-composer" behavior the CLI historically used), so the
/// agent can read or vision-analyze the file using its own tools without
/// us needing to inline contents into the prompt.
/// </summary>
public static class AttachmentFormatter
{
    /// <summary>
    /// Composes a user message from <paramref name="text"/> + every
    /// successfully-read attachment in <paramref name="attachments"/>.
    /// Attachments are emitted as absolute paths, one per line. Returns
    /// an empty string when there's nothing to send — caller should
    /// refuse to dispatch.
    /// </summary>
    public static string BuildMessage(string? text, IReadOnlyList<AttachmentReadResult> attachments)
    {
        var trimmed = (text ?? "").TrimEnd();
        var ok = new List<AttachmentReadResult>(attachments.Count);
        foreach (var a in attachments)
        {
            if (a.IsSuccess) ok.Add(a);
        }

        if (ok.Count == 0) return trimmed;

        var sb = new StringBuilder();
        if (trimmed.Length > 0)
        {
            sb.Append(trimmed);
            sb.Append("\n\n");
        }
        else
        {
            // No prose at all — give the agent a tiny stand-in line so the
            // path doesn't arrive as a context-free token.
            sb.Append("I'm attaching ");
            sb.Append(ok.Count == 1 ? "a file" : $"{ok.Count} files");
            sb.Append(":\n\n");
        }

        for (var i = 0; i < ok.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(ok[i].Path);
        }
        return sb.ToString();
    }

    /// <summary>Human-readable byte size: "812 B", "12.4 KB", "3.1 MB".</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        var kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb.ToString("0.#", CultureInfo.InvariantCulture)} KB";
        var mb = kb / 1024.0;
        return $"{mb.ToString("0.##", CultureInfo.InvariantCulture)} MB";
    }
}

