using System.Text;
using System.Text.RegularExpressions;

namespace Hermes.ApiClient;

/// <summary>
/// Safely edits the gateway's <c>.env</c> file in place. The file is the
/// user's actual config — corrupting it breaks Hermes — so this class is
/// paranoid:
/// <list type="bullet">
///   <item>Validates each managed value against a known-safe charset so we
///   never need to quote / escape on write (and never emit something the
///   gateway can't parse back).</item>
///   <item>Round-trips other keys, comments, blank lines, and the file's
///   original LF / CRLF newline style without touching them.</item>
///   <item>Detects concurrent external edits via a captured last-write
///   timestamp so we don't clobber a user who edited the file in another
///   tool while the Settings page was open.</item>
///   <item>Writes atomically: data is staged in a uniquely-named temp file,
///   flushed to physical storage, then swapped in via <see cref="File.Replace(string, string, string)"/>
///   which also drops a <c>.bak</c> alongside.</item>
/// </list>
/// </summary>
public static class EnvFileWriter
{
    public enum SaveStatus
    {
        Ok,
        /// <summary>File on disk changed since the caller's snapshot — abort
        /// to avoid clobbering external edits.</summary>
        ConcurrentExternalEdit,
    }

    public readonly record struct ManagedValue(string Key, string Value);

    /// <summary>
    /// Validation rules for each editable key. Only allow charsets the
    /// gateway and downstream tools accept without quoting, so the writer
    /// never has to inject quotes / escapes.
    /// </summary>
    private static readonly Dictionary<string, Func<string, bool>> KeyValidators =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Hostname / IPv4 / loopback. RFC-style chars only.
            ["API_SERVER_HOST"] = v =>
                !string.IsNullOrEmpty(v) && v.Length <= 253 &&
                Regex.IsMatch(v, @"^[A-Za-z0-9.\-_]+$"),

            // 1-65535
            ["API_SERVER_PORT"] = v =>
                int.TryParse(v, out var p) && p >= 1 && p <= 65535,

            // base64 / base64url / hex / urlsafe — accept the common token
            // shapes. Allow EMPTY so the user can clear auth on a local box.
            ["API_SERVER_KEY"] = v =>
                v.Length == 0 ||
                (v.Length is >= 16 and <= 256 && Regex.IsMatch(v, @"^[A-Za-z0-9+/=_\-]+$")),

            // Model identifier — provider/model:tag style.
            ["API_SERVER_MODEL_NAME"] = v =>
                !string.IsNullOrEmpty(v) && v.Length <= 128 &&
                Regex.IsMatch(v, @"^[A-Za-z0-9.\-_:/]+$"),
        };

    public static bool IsValid(string key, string value) =>
        KeyValidators.TryGetValue(key, out var v) && v(value);

    /// <summary>
    /// Returns the file's UTC last-write timestamp, or <see langword="null"/>
    /// if it doesn't exist yet. Callers capture this when they first read
    /// the file and pass it back to <see cref="Save"/> as a poor-man's
    /// concurrency token.
    /// </summary>
    public static DateTime? GetSnapshotStamp(string envPath) =>
        File.Exists(envPath) ? File.GetLastWriteTimeUtc(envPath) : null;

    /// <summary>
    /// Rewrites <paramref name="envPath"/> so each entry in
    /// <paramref name="updates"/> has the given value, preserving everything
    /// else (other keys, comments, blank lines, newline style). Creates the
    /// file (and parent directory) if missing.
    /// </summary>
    /// <param name="envPath">Full path to <c>.env</c>.</param>
    /// <param name="updates">Managed key/value pairs to apply. All must pass
    /// <see cref="IsValid"/> or this throws <see cref="ArgumentException"/>
    /// before anything is written.</param>
    /// <param name="expectedSnapshot">Optional last-write stamp captured by
    /// the caller when it read the file. If the file on disk is newer than
    /// this (allowing 1s for FS timestamp granularity), the save is aborted
    /// with <see cref="SaveStatus.ConcurrentExternalEdit"/>.</param>
    public static SaveStatus Save(
        string envPath,
        IEnumerable<ManagedValue> updates,
        DateTime? expectedSnapshot = null)
    {
        // Validate up front — once we start writing we don't want to fail
        // partway and leave a half-edited file.
        var updateList = updates.ToList();
        foreach (var u in updateList)
        {
            if (!IsValid(u.Key, u.Value))
            {
                throw new ArgumentException(
                    $"Invalid value for {u.Key}.", nameof(updates));
            }
        }

        var dir = Path.GetDirectoryName(envPath)
                  ?? throw new ArgumentException("envPath has no directory.", nameof(envPath));
        Directory.CreateDirectory(dir);

        var fileExists = File.Exists(envPath);

        if (fileExists && expectedSnapshot is DateTime expected)
        {
            // Allow 1s of slack: NTFS / FAT round timestamps; we don't want
            // a false-positive conflict for a stamp captured under a
            // different precision.
            var actual = File.GetLastWriteTimeUtc(envPath);
            if (actual - expected > TimeSpan.FromSeconds(1))
            {
                return SaveStatus.ConcurrentExternalEdit;
            }
        }

        var original = fileExists ? File.ReadAllText(envPath) : "";

        // Newline preservation: pick whatever the file already uses. New
        // files default to the platform style (CRLF on Windows).
        var newline = DetectNewline(original) ?? Environment.NewLine;
        var endsWithNewline = original.Length > 0 &&
                              (original[^1] == '\n' || original[^1] == '\r');

        // Split on detected newlines so we don't mangle anything. Using
        // StringSplitOptions.None keeps trailing blank lines.
        var lines = original.Length == 0
            ? new List<string>()
            : original.Split(new[] { newline }, StringSplitOptions.None).ToList();

        // If file ended with a newline, the split produces a trailing
        // empty string. Drop it so we don't render two blank lines on
        // round-trip; we re-add the final newline at the end if needed.
        if (lines.Count > 0 && endsWithNewline && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        // Track which managed keys still need writing. Anything left over
        // after the in-place pass gets appended.
        var remaining = updateList.ToDictionary(
            u => u.Key, u => u.Value, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < lines.Count; i++)
        {
            var raw = lines[i];
            var trimmed = raw.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;

            var eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;

            var key = trimmed[..eq].Trim();
            if (!remaining.TryGetValue(key, out var newValue)) continue;

            var leadingWs = raw[..(raw.Length - trimmed.Length)];
            lines[i] = $"{leadingWs}{key}={newValue}";
            remaining.Remove(key);
        }

        if (remaining.Count > 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
            {
                // Separator blank line between unmanaged content and
                // newly-appended managed keys.
                lines.Add("");
            }
            foreach (var (key, value) in remaining)
            {
                lines.Add($"{key}={value}");
            }
        }

        var sb = new StringBuilder(original.Length + 256);
        for (int i = 0; i < lines.Count; i++)
        {
            sb.Append(lines[i]);
            // Always end every line with a newline (matches typical
            // .env conventions and the dominant style we detected).
            if (i < lines.Count - 1 || endsWithNewline || !fileExists)
            {
                sb.Append(newline);
            }
        }

        // Atomic write: stage in a uniquely-named sibling temp file, flush
        // to physical storage, then replace via File.Replace which is the
        // only API that gets us both the swap *and* a .bak in one call.
        var tmpName = $".env.{Guid.NewGuid():N}".Substring(0, 13) + ".tmp";
        var tmpPath = Path.Combine(dir, tmpName);
        var bakPath = envPath + ".bak";

        try
        {
            using (var fs = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var sw = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                sw.Write(sb.ToString());
                sw.Flush();
                fs.Flush(flushToDisk: true);
            }

            if (fileExists)
            {
                // ignoreMetadataErrors=true lets the swap succeed even on
                // FAT/exFAT, network shares, or when source ACLs differ.
                File.Replace(tmpPath, envPath, bakPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tmpPath, envPath);
            }
        }
        catch
        {
            // Best-effort cleanup so we don't leave .tmp orphans on errors.
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* swallow */ }
            throw;
        }

        return SaveStatus.Ok;
    }

    /// <summary>Detects whether the file uses CRLF, LF, or CR line endings.
    /// Returns the dominant style, or <see langword="null"/> for empty /
    /// single-line files (caller picks platform default).</summary>
    private static string? DetectNewline(string content)
    {
        int crlf = 0, lf = 0, cr = 0;
        for (int i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (c == '\r')
            {
                if (i + 1 < content.Length && content[i + 1] == '\n') { crlf++; i++; }
                else { cr++; }
            }
            else if (c == '\n') { lf++; }
        }
        if (crlf == 0 && lf == 0 && cr == 0) return null;
        if (crlf >= lf && crlf >= cr) return "\r\n";
        if (lf >= cr) return "\n";
        return "\r";
    }
}
