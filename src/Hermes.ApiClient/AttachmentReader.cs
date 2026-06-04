using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Hermes.ApiClient;

/// <summary>
/// Resolves a user-attached file to a stable, agent-readable absolute
/// path. Doesn't read or upload the bytes — the Hermes agent already has
/// file-reading and vision tools, so we just hand it the path and let it
/// decide what to do. Magic-byte sniffing classifies images so the UI
/// can render a distinct chip, but the formatter treats every kind the
/// same way (path on its own line).
/// </summary>
/// <remarks>
/// Lives in <c>Hermes.ApiClient</c> rather than the App because the
/// classification + path-existence logic is straightforward to unit-test
/// without a WinUI host.
/// </remarks>
public static class AttachmentReader
{
    /// <summary>How many bytes from the start of the file we read for
    /// image-magic detection. 16 bytes is more than enough — every
    /// format we recognize has a fixed-length header well under that.</summary>
    private const int MagicSniffBytes = 16;

    public static async Task<AttachmentReadResult> ReadAsync(
        string path,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return AttachmentReadResult.Error(path ?? "", "Path is empty.");

        FileInfo info;
        try { info = new FileInfo(path); }
        catch (Exception ex) { return AttachmentReadResult.Error(path, $"Couldn't open file: {ex.Message}"); }

        if (!info.Exists)
            return AttachmentReadResult.Error(path, "File not found.");

        try
        {
            // Read the head of the file only for magic-byte classification.
            // We deliberately don't slurp the whole file — the agent will
            // re-open it as needed, and avoiding the read keeps multi-
            // megabyte log files cheap to attach.
            var head = new byte[MagicSniffBytes];
            int read = 0;
            await using (var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: MagicSniffBytes, useAsync: true))
            {
                while (read < head.Length)
                {
                    var n = await stream.ReadAsync(head.AsMemory(read, head.Length - read), ct)
                        .ConfigureAwait(false);
                    if (n == 0) break;
                    read += n;
                }
            }
            var headSpan = read == head.Length
                ? (ReadOnlySpan<byte>)head
                : head.AsSpan(0, read);

            var kind = IsKnownImage(headSpan) ? AttachmentKind.Image : AttachmentKind.File;
            return kind == AttachmentKind.Image
                ? AttachmentReadResult.Image(path, info.Name, info.Length)
                : AttachmentReadResult.File(path, info.Name, info.Length);
        }
        catch (OperationCanceledException)
        {
            return AttachmentReadResult.Error(path, "Cancelled.");
        }
        catch (UnauthorizedAccessException ex)
        {
            return AttachmentReadResult.Error(path, $"Access denied: {ex.Message}");
        }
        catch (IOException ex)
        {
            return AttachmentReadResult.Error(path, $"Read failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            return AttachmentReadResult.Error(path, $"Unexpected error: {ex.Message}");
        }
    }

    /// <summary>
    /// True if <paramref name="bytes"/> starts with the magic header of an
    /// image format the agent can almost certainly process with its vision
    /// tool (PNG, JPEG, GIF, WebP, BMP). Sniffing bytes rather than
    /// trusting the extension catches misnamed files (a .png that's
    /// actually a renamed text file would otherwise lie to the agent).
    /// </summary>
    internal static bool IsKnownImage(ReadOnlySpan<byte> bytes)
    {
        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
            return true;

        // JPEG: FF D8 FF
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return true;

        // GIF87a / GIF89a: 47 49 46 38 (37|39) 61
        if (bytes.Length >= 6 &&
            bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38 &&
            (bytes[4] == 0x37 || bytes[4] == 0x39) && bytes[5] == 0x61)
            return true;

        // WebP: "RIFF" .... "WEBP"
        if (bytes.Length >= 12 &&
            bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
            bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
            return true;

        // BMP: "BM"
        if (bytes.Length >= 2 && bytes[0] == 0x42 && bytes[1] == 0x4D)
            return true;

        return false;
    }
}

public enum AttachmentKind { File, Image, Error }

public sealed record AttachmentReadResult(
    AttachmentKind Kind,
    string Path,
    string FileName,
    long Size,
    string? ErrorMessage)
{
    /// <summary>True if the attachment will travel with the outbound
    /// message (i.e. it's a real file the agent can read). Error chips
    /// stay in the UI as feedback but the formatter skips them.</summary>
    public bool IsSuccess => Kind != AttachmentKind.Error;

    public bool IsImage => Kind == AttachmentKind.Image;
    public bool IsFile => Kind == AttachmentKind.File;

    public static AttachmentReadResult File(string path, string fileName, long size)
        => new(AttachmentKind.File, path, fileName, size, null);

    public static AttachmentReadResult Image(string path, string fileName, long size)
        => new(AttachmentKind.Image, path, fileName, size, null);

    public static AttachmentReadResult Error(string path, string message)
        => new(AttachmentKind.Error, path, System.IO.Path.GetFileName(path), 0, message);
}

