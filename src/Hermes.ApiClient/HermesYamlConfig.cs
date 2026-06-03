using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Hermes.ApiClient;

/// <summary>
/// One MCP server entry as seen by the UI: a name plus an opaque body
/// (any JSON object). The body round-trips into the YAML untouched
/// except for type-preserving format changes (e.g. plain int vs quoted
/// int). Order of properties in the body is preserved.
/// </summary>
public sealed record McpServerEntry(string Name, JsonElement Body);

/// <summary>
/// Captured at <see cref="HermesYamlConfig.Load"/> time. Re-presented on
/// <see cref="HermesYamlConfig.Save"/> so we can detect external edits
/// before clobbering them. Carries the original file text so callers
/// don't have to re-read it.
/// </summary>
public sealed class ConcurrencyToken
{
    public required string Path { get; init; }
    public required DateTime LastWriteUtc { get; init; }
    public required string Sha256Hex { get; init; }
    public required string OriginalText { get; init; }
}

/// <summary>Outcome of a save attempt.</summary>
public enum YamlSaveStatus
{
    Saved,
    /// <summary>Input matched what was already on disk; no write performed.</summary>
    Unchanged,
    /// <summary>File on disk changed externally since the snapshot.</summary>
    ConflictExternalEdit,
    /// <summary>Post-write validation rejected the candidate.</summary>
    ValidationFailed,
}

/// <summary>
/// Result of a save call. Includes a human-readable detail message for
/// non-OK statuses so the UI can surface a clear error without
/// re-parsing.
/// </summary>
public sealed record YamlSaveResult(YamlSaveStatus Status, string? Detail = null);

/// <summary>
/// Reads / writes the <c>mcp_servers</c> block of the Hermes
/// <c>config.yaml</c> file using a subtree-splice strategy that
/// preserves everything outside the block byte-for-byte. Designed to
/// touch only the section we own — comments, folded multi-line scalars,
/// Unicode strings, and exotic formatting elsewhere in the file MUST
/// survive a round-trip unchanged.
///
/// See plan.md (Settings: MCP servers area) section "(b) YAML round-trip
/// strategy: SUBTREE SPLICE" for the full design rationale.
/// </summary>
public static class HermesYamlConfig
{
    private const string McpServersKey = "mcp_servers";
    private const string PlatformToolsetsKey = "platform_toolsets";
    private const string ApiServerToolsetKey = "api_server";

    /// <summary>
    /// The default first entry in <c>platform_toolsets.api_server</c>.
    /// Without this, an api_server platform left to defaults would lose
    /// all its non-MCP tools (web, file, terminal, ...) the moment we
    /// write an explicit api_server entry.
    /// </summary>
    public const string DefaultApiServerToolset = "hermes-api-server";

    /// <summary>
    /// Name of the permanent one-time backup created the first time we
    /// ever write to the file. Never overwritten — this is the user's
    /// escape hatch if a future writer bug ever corrupts both the live
    /// file and the rolling <c>.bak</c>.
    /// </summary>
    public const string PermanentBackupSuffix = ".before-hermes-winui.bak";

    /// <summary>
    /// Reads the current <c>platform_toolsets.api_server</c> list from
    /// the given file. Returns an empty list if the file, the
    /// <c>platform_toolsets</c> key, or the <c>api_server</c> subkey is
    /// absent. Used by the UI to detect "out of sync" state (i.e. saved
    /// MCP servers that aren't yet opted into the API server platform).
    /// </summary>
    /// <exception cref="InvalidDataException">The current file on disk is
    /// not parseable as YAML.</exception>
    public static IReadOnlyList<string> ReadApiServerToolsets(string path)
    {
        if (!File.Exists(path)) return [];
        var text = File.ReadAllText(path, Encoding.UTF8);
        var plat = ExtractPlatformToolsets(text);
        return plat.TryGetValue(ApiServerToolsetKey, out var list) ? list : [];
    }

    /// <summary>
    /// Loads the current <c>mcp_servers</c> entries (empty list if the
    /// key is absent or the file does not exist) and a concurrency token
    /// that callers must hand back on <see cref="Save"/>.
    /// </summary>
    public static (IReadOnlyList<McpServerEntry> Servers, ConcurrencyToken Token) Load(string path)
    {
        string text;
        DateTime stamp;
        if (File.Exists(path))
        {
            text = File.ReadAllText(path, Encoding.UTF8);
            stamp = File.GetLastWriteTimeUtc(path);
        }
        else
        {
            text = string.Empty;
            stamp = DateTime.MinValue;
        }

        var token = new ConcurrencyToken
        {
            Path = path,
            LastWriteUtc = stamp,
            Sha256Hex = ComputeSha256Hex(text),
            OriginalText = text,
        };

        var servers = ExtractMcpServers(text);
        return (servers, token);
    }

    /// <summary>
    /// Writes <paramref name="newServers"/> as the file's
    /// <c>mcp_servers</c> block. When <paramref name="apiServerToolsets"/>
    /// is non-null, also splices the <c>platform_toolsets.api_server</c>
    /// entry to that exact list — needed because the Hermes API server
    /// platform does NOT auto-include MCP servers (it passes
    /// <c>include_default_mcp_servers=False</c>), so the WinUI app's chat
    /// won't see them unless they are explicitly named here. The rest of
    /// <c>platform_toolsets</c> (cli, telegram, …) is preserved.
    ///
    /// Returns <see cref="YamlSaveStatus.Unchanged"/> when both the
    /// MCP block and the api_server toolset entry already match the
    /// requested values byte-equivalent; no write is performed in that
    /// case. Returns <see cref="YamlSaveStatus.ConflictExternalEdit"/>
    /// if the file on disk has changed since <paramref name="token"/>
    /// was captured. Throws nothing for the conflict case — callers
    /// inspect the returned status.
    /// </summary>
    /// <exception cref="InvalidDataException">The current file on disk is
    /// not parseable as YAML.</exception>
    public static YamlSaveResult Save(
        string path,
        ConcurrencyToken token,
        IReadOnlyList<McpServerEntry> newServers,
        IReadOnlyList<string>? apiServerToolsets = null)
    {
        // 1. Concurrency check. Re-read the file from disk and compare
        //    BOTH stamp and SHA-256 against the token. A bare timestamp
        //    diff is not enough (touch updates the stamp without
        //    changing content); the SHA defends against that.
        string currentText;
        DateTime currentStamp;
        if (File.Exists(path))
        {
            currentText = File.ReadAllText(path, Encoding.UTF8);
            currentStamp = File.GetLastWriteTimeUtc(path);
        }
        else
        {
            currentText = string.Empty;
            currentStamp = DateTime.MinValue;
        }

        var stampDriftedBeyondSlack =
            currentStamp != DateTime.MinValue &&
            token.LastWriteUtc != DateTime.MinValue &&
            (currentStamp - token.LastWriteUtc) > TimeSpan.FromSeconds(1);

        if (stampDriftedBeyondSlack)
        {
            // Stamp says "changed" — check the hash. If the hash still
            // matches our snapshot the change was content-equivalent
            // (e.g. someone touched the file) and we can safely proceed.
            var currentHash = ComputeSha256Hex(currentText);
            if (!string.Equals(currentHash, token.Sha256Hex, StringComparison.OrdinalIgnoreCase))
            {
                return new YamlSaveResult(
                    YamlSaveStatus.ConflictExternalEdit,
                    "config.yaml was modified outside this app since you opened Settings. " +
                    "Click Revert to reload current values, then re-apply your changes.");
            }
        }

        // 2. Build the new mcp_servers YAML block from the input. Use
        //    LF for internal stitching; we re-normalize newlines at the
        //    end to match what the original file uses.
        string mcpYaml;
        try
        {
            mcpYaml = EmitMcpServersYaml(newServers);
        }
        catch (Exception ex)
        {
            return new YamlSaveResult(
                YamlSaveStatus.ValidationFailed,
                $"Could not build YAML for the new MCP servers block: {ex.Message}");
        }

        // 3. Splice into the original text.
        string candidate;
        try
        {
            candidate = SpliceTopLevelBlock(currentText, McpServersKey, mcpYaml);
        }
        catch (Exception ex)
        {
            return new YamlSaveResult(
                YamlSaveStatus.ValidationFailed,
                $"Could not locate the mcp_servers range in the current file: {ex.Message}");
        }

        // 3b. Optionally splice platform_toolsets.api_server. We do this
        //     against the post-MCP-splice candidate so both edits land in
        //     one atomic write with one backup.
        if (apiServerToolsets is not null)
        {
            string platYaml;
            try
            {
                var platformToolsets = ExtractPlatformToolsets(candidate);
                platformToolsets[ApiServerToolsetKey] = apiServerToolsets.ToList();
                platYaml = EmitPlatformToolsetsYaml(platformToolsets);
            }
            catch (Exception ex)
            {
                return new YamlSaveResult(
                    YamlSaveStatus.ValidationFailed,
                    $"Could not build platform_toolsets YAML: {ex.Message}");
            }

            try
            {
                candidate = SpliceTopLevelBlock(candidate, PlatformToolsetsKey, platYaml);
            }
            catch (Exception ex)
            {
                return new YamlSaveResult(
                    YamlSaveStatus.ValidationFailed,
                    $"Could not splice platform_toolsets into the file: {ex.Message}");
            }
        }

        // 4. No-op short-circuit: semantic compare. Re-emission may
        //    produce subtly different bytes than the original (different
        //    quoting style, flow vs block layout) even when the
        //    structure is unchanged. Skip the write in that case too —
        //    "user opened Settings and clicked Save with no edits"
        //    should never disturb the file.
        var currentServers = ExtractMcpServers(currentText);
        var mcpUnchanged = StructurallyEquivalent(currentServers, newServers);
        var platformUnchanged = apiServerToolsets is null
            || ApiServerToolsetsEqual(
                ExtractPlatformToolsets(currentText).TryGetValue(ApiServerToolsetKey, out var existing)
                    ? existing
                    : [],
                apiServerToolsets);
        if (mcpUnchanged && platformUnchanged)
        {
            return new YamlSaveResult(YamlSaveStatus.Unchanged);
        }

        // 5. Post-write validation: parse the candidate, extract
        //    mcp_servers, semantically compare to the intended input.
        //    This catches writer bugs BEFORE anything lands on disk.
        if (!ValidateCandidate(candidate, newServers, apiServerToolsets, out var validationError))
        {
            return new YamlSaveResult(YamlSaveStatus.ValidationFailed, validationError);
        }

        // 6. Atomic write with rolling .bak + one-time permanent backup.
        WriteAtomically(path, currentText, candidate);

        return new YamlSaveResult(YamlSaveStatus.Saved);
    }

    // ---- internals ---------------------------------------------------------

    private static IReadOnlyList<McpServerEntry> ExtractMcpServers(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        YamlStream stream;
        try
        {
            stream = new YamlStream();
            using var reader = new StringReader(text);
            stream.Load(reader);
        }
        catch (YamlException ex)
        {
            throw new InvalidDataException(
                $"config.yaml is not valid YAML: {ex.Message}", ex);
        }

        if (stream.Documents.Count == 0) return [];
        if (stream.Documents[0].RootNode is not YamlMappingNode root) return [];

        // Find the mcp_servers key. Top-level only.
        foreach (var kv in root.Children)
        {
            if (kv.Key is YamlScalarNode keyScalar && keyScalar.Value == McpServersKey)
            {
                if (kv.Value is YamlMappingNode mcpMap)
                {
                    var list = new List<McpServerEntry>();
                    foreach (var serverKv in mcpMap.Children)
                    {
                        if (serverKv.Key is not YamlScalarNode nameNode || nameNode.Value is null)
                            continue;
                        var body = YamlToJson(serverKv.Value);
                        list.Add(new McpServerEntry(nameNode.Value, body));
                    }
                    return list;
                }
                // mcp_servers exists but is null / empty → no servers.
                return [];
            }
        }
        return [];
    }

    /// <summary>
    /// Builds the full <c>mcp_servers:</c> block as a YAML string ending
    /// in a single LF. Always starts at column 0 with the literal
    /// <c>mcp_servers:</c> key followed by 2-space-indented children.
    /// </summary>
    private static string EmitMcpServersYaml(IReadOnlyList<McpServerEntry> servers)
    {
        // Build wrapper mapping { "mcp_servers": { name1: body1, ... } }
        var inner = new YamlMappingNode();
        foreach (var s in servers)
        {
            ValidateServerName(s.Name);
            inner.Add(new YamlScalarNode(s.Name), JsonToYaml(s.Body));
        }
        var wrapper = new YamlMappingNode();
        wrapper.Add(new YamlScalarNode(McpServersKey), inner);

        var doc = new YamlDocument(wrapper);
        var stream = new YamlStream(doc);

        var sw = new StringWriter { NewLine = "\n" };
        stream.Save(sw, assignAnchors: false);
        var yaml = sw.ToString();

        // Strip the "---\n" document-start marker (YamlStream.Save
        // always emits it) and any trailing "...\n" end marker.
        if (yaml.StartsWith("---\n", StringComparison.Ordinal))
            yaml = yaml[4..];
        else if (yaml.StartsWith("---\r\n", StringComparison.Ordinal))
            yaml = yaml[5..];

        if (yaml.EndsWith("...\n", StringComparison.Ordinal))
            yaml = yaml[..^4];
        else if (yaml.EndsWith("...\r\n", StringComparison.Ordinal))
            yaml = yaml[..^5];

        // Normalise all line endings to LF for splice math; the splice
        // step re-applies the original file's newline style.
        yaml = yaml.Replace("\r\n", "\n");

        // Guarantee exactly one trailing newline.
        yaml = yaml.TrimEnd('\n') + "\n";
        return yaml;
    }

    private static void ValidateServerName(string name)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("MCP server name must be non-empty.", nameof(name));
        if (!Regex.IsMatch(name, @"^[A-Za-z0-9_.\-]+$"))
            throw new ArgumentException(
                $"MCP server name '{name}' contains invalid characters; allowed: A-Z a-z 0-9 _ . -",
                nameof(name));
    }

    /// <summary>
    /// Splices <paramref name="block"/> (a fully-formed top-level YAML
    /// block ending in LF whose first line is "<paramref name="keyName"/>:")
    /// into <paramref name="original"/>, replacing the existing block if
    /// present or appending if not.
    /// </summary>
    private static string SpliceTopLevelBlock(string original, string keyName, string block)
    {
        // Detect dominant newline style so we can re-apply it.
        var newline = DetectNewline(original) ?? Environment.NewLine;
        // block is internally LF-normalised; re-apply file's newline.
        var blockForFile = newline == "\n" ? block : block.Replace("\n", newline);

        if (string.IsNullOrEmpty(original))
        {
            // Empty file or no file: just emit the block.
            return blockForFile;
        }

        // Locate the byte range of the existing block.
        var (start, end, exists) = LocateTopLevelKeyRange(original, keyName);

        if (!exists)
        {
            // Append. Ensure separation from prior content.
            var sb = new StringBuilder(original.Length + blockForFile.Length + 4);
            sb.Append(original);
            if (!original.EndsWith('\n') && !original.EndsWith('\r'))
            {
                sb.Append(newline);
            }
            // Leading blank line for visual separation from prior block,
            // but only if the existing content has something on the
            // immediately-preceding line.
            if (!EndsWithBlankLine(original))
            {
                sb.Append(newline);
            }
            sb.Append(blockForFile);
            return sb.ToString();
        }

        // Replace [start, end) with blockForFile.
        var result = new StringBuilder(original.Length - (end - start) + blockForFile.Length);
        result.Append(original, 0, start);
        result.Append(blockForFile);
        if (end < original.Length)
        {
            result.Append(original, end, original.Length - end);
        }
        return result.ToString();
    }

    private static bool EndsWithBlankLine(string text)
    {
        // Look for at least two consecutive newlines at the end.
        if (text.Length < 2) return false;
        return text.EndsWith("\n\n", StringComparison.Ordinal)
            || text.EndsWith("\r\n\r\n", StringComparison.Ordinal);
    }

    /// <summary>
    /// Finds the byte range of the named top-level key in
    /// <paramref name="text"/>. Range starts at column-1 of the line
    /// containing the key and ends at column-1 of the line containing
    /// the NEXT top-level key (or at EOF if no next key). Returns
    /// <c>exists=false</c> with <c>start=end=text.Length</c> if the key
    /// is not present.
    /// </summary>
    /// <remarks>
    /// We deliberately use the NEXT key's Start (not the current node's
    /// End) because YamlDotNet's End-mark on block-style nodes is known
    /// to be unreliable.
    /// </remarks>
    private static (int Start, int End, bool Exists) LocateTopLevelKeyRange(string text, string keyName)
    {
        YamlStream stream;
        try
        {
            stream = new YamlStream();
            using var reader = new StringReader(text);
            stream.Load(reader);
        }
        catch (YamlException ex)
        {
            throw new InvalidDataException(
                $"config.yaml is not valid YAML: {ex.Message}", ex);
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            return (text.Length, text.Length, false);
        }

        // Collect top-level key ranges in source order.
        var topKeys = new List<(string Key, int KeyIndex)>(root.Children.Count);
        foreach (var kv in root.Children)
        {
            if (kv.Key is YamlScalarNode scalar && scalar.Value is string name)
            {
                topKeys.Add((name, (int)kv.Key.Start.Index));
            }
        }
        topKeys.Sort((a, b) => a.KeyIndex.CompareTo(b.KeyIndex));

        var idx = topKeys.FindIndex(t => t.Key == keyName);
        if (idx < 0) return (text.Length, text.Length, false);

        var keyIndex = topKeys[idx].KeyIndex;
        var lineStart = LineStartFor(text, keyIndex);

        int rangeEnd;
        if (idx + 1 < topKeys.Count)
        {
            rangeEnd = LineStartFor(text, topKeys[idx + 1].KeyIndex);
        }
        else
        {
            rangeEnd = text.Length;
        }

        return (lineStart, rangeEnd, true);
    }

    /// <summary>
    /// Reads the entire <c>platform_toolsets:</c> mapping into a
    /// preserve-order dictionary of <c>platform name → toolset list</c>.
    /// Skips entries whose value is not a YAML sequence of scalars
    /// (defensive — Hermes only writes sequence-of-scalars there).
    /// Returns an empty dictionary if the key is absent or null.
    /// </summary>
    private static Dictionary<string, List<string>> ExtractPlatformToolsets(string text)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return result;

        YamlStream stream;
        try
        {
            stream = new YamlStream();
            using var reader = new StringReader(text);
            stream.Load(reader);
        }
        catch (YamlException ex)
        {
            throw new InvalidDataException(
                $"config.yaml is not valid YAML: {ex.Message}", ex);
        }

        if (stream.Documents.Count == 0) return result;
        if (stream.Documents[0].RootNode is not YamlMappingNode root) return result;

        foreach (var kv in root.Children)
        {
            if (kv.Key is YamlScalarNode keyScalar && keyScalar.Value == PlatformToolsetsKey)
            {
                if (kv.Value is YamlMappingNode mapping)
                {
                    foreach (var inner in mapping.Children)
                    {
                        if (inner.Key is not YamlScalarNode nameNode || nameNode.Value is null)
                            continue;
                        if (inner.Value is not YamlSequenceNode seq)
                            continue;
                        var list = new List<string>(seq.Children.Count);
                        foreach (var item in seq.Children)
                        {
                            if (item is YamlScalarNode s && s.Value is not null)
                                list.Add(s.Value);
                        }
                        result[nameNode.Value] = list;
                    }
                }
                return result;
            }
        }
        return result;
    }

    /// <summary>
    /// Builds the full <c>platform_toolsets:</c> block as a YAML string
    /// ending in a single LF. Always starts at column 0 with the literal
    /// <c>platform_toolsets:</c> key followed by 2-space-indented
    /// children.
    /// </summary>
    private static string EmitPlatformToolsetsYaml(Dictionary<string, List<string>> mapping)
    {
        var inner = new YamlMappingNode();
        foreach (var kv in mapping)
        {
            var seq = new YamlSequenceNode();
            foreach (var entry in kv.Value)
            {
                seq.Add(new YamlScalarNode(entry));
            }
            inner.Add(new YamlScalarNode(kv.Key), seq);
        }
        var wrapper = new YamlMappingNode();
        wrapper.Add(new YamlScalarNode(PlatformToolsetsKey), inner);

        var doc = new YamlDocument(wrapper);
        var stream = new YamlStream(doc);

        var sw = new StringWriter { NewLine = "\n" };
        stream.Save(sw, assignAnchors: false);
        var yaml = sw.ToString();

        if (yaml.StartsWith("---\n", StringComparison.Ordinal))
            yaml = yaml[4..];
        else if (yaml.StartsWith("---\r\n", StringComparison.Ordinal))
            yaml = yaml[5..];

        if (yaml.EndsWith("...\n", StringComparison.Ordinal))
            yaml = yaml[..^4];
        else if (yaml.EndsWith("...\r\n", StringComparison.Ordinal))
            yaml = yaml[..^5];

        yaml = yaml.Replace("\r\n", "\n");
        yaml = yaml.TrimEnd('\n') + "\n";
        return yaml;
    }

    private static int LineStartFor(string text, int index)
    {
        if (index <= 0) return 0;
        var nl = text.LastIndexOf('\n', index - 1);
        return nl < 0 ? 0 : nl + 1;
    }

    private static string? DetectNewline(string text)
    {
        int crlf = 0, lf = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                if (i > 0 && text[i - 1] == '\r') crlf++;
                else lf++;
            }
        }
        if (crlf == 0 && lf == 0) return null;
        return crlf >= lf ? "\r\n" : "\n";
    }

    /// <summary>
    /// Parses <paramref name="candidate"/>, locates the <c>mcp_servers</c>
    /// subtree, and asserts it semantically matches
    /// <paramref name="intended"/>. Also validates the
    /// <c>platform_toolsets.api_server</c> list when
    /// <paramref name="intendedApiServer"/> is non-null. Catches writer
    /// bugs (dropped fields, type changes, reordering) before anything
    /// lands on disk.
    /// </summary>
    private static bool ValidateCandidate(
        string candidate,
        IReadOnlyList<McpServerEntry> intended,
        IReadOnlyList<string>? intendedApiServer,
        out string? error)
    {
        IReadOnlyList<McpServerEntry> parsed;
        try
        {
            parsed = ExtractMcpServers(candidate);
        }
        catch (Exception ex)
        {
            error = $"Re-parse of the candidate file failed: {ex.Message}";
            return false;
        }

        if (parsed.Count != intended.Count)
        {
            error = $"Round-trip lost servers: intended {intended.Count}, " +
                    $"file would have {parsed.Count}.";
            return false;
        }

        // Compare by canonical JSON. Order matters (YAML mappings are
        // ordered) so do an in-order pairwise compare.
        for (int i = 0; i < intended.Count; i++)
        {
            if (!string.Equals(intended[i].Name, parsed[i].Name, StringComparison.Ordinal))
            {
                error = $"Round-trip reordered servers at index {i}: " +
                        $"intended '{intended[i].Name}', got '{parsed[i].Name}'.";
                return false;
            }
            var a = CanonicalJson(intended[i].Body);
            var b = CanonicalJson(parsed[i].Body);
            if (!string.Equals(a, b, StringComparison.Ordinal))
            {
                error = $"Round-trip mutated server '{intended[i].Name}'. " +
                        $"This is a bug — the file was not written.";
                return false;
            }
        }

        if (intendedApiServer is not null)
        {
            Dictionary<string, List<string>> parsedPlat;
            try
            {
                parsedPlat = ExtractPlatformToolsets(candidate);
            }
            catch (Exception ex)
            {
                error = $"Re-parse of platform_toolsets in candidate failed: {ex.Message}";
                return false;
            }
            if (!parsedPlat.TryGetValue(ApiServerToolsetKey, out var parsedApi))
            {
                error = "Round-trip lost platform_toolsets.api_server entry.";
                return false;
            }
            if (!ApiServerToolsetsEqual(parsedApi, intendedApiServer))
            {
                error = "Round-trip mutated platform_toolsets.api_server. " +
                        "This is a bug — the file was not written.";
                return false;
            }
        }

        error = null;
        return true;
    }

    private static bool ApiServerToolsetsEqual(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static bool StructurallyEquivalent(
        IReadOnlyList<McpServerEntry> a,
        IReadOnlyList<McpServerEntry> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].Name, b[i].Name, StringComparison.Ordinal))
                return false;
            if (!string.Equals(CanonicalJson(a[i].Body), CanonicalJson(b[i].Body), StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static string CanonicalJson(JsonElement el)
    {
        // System.Text.Json with no whitespace; property order preserved
        // from the JsonElement which preserves insertion order.
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            el.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteAtomically(string path, string originalText, string candidate)
    {
        var dir = Path.GetDirectoryName(path)
                  ?? throw new ArgumentException("path has no directory.", nameof(path));
        Directory.CreateDirectory(dir);

        var tmpName = $".cfg.{Guid.NewGuid():N}.tmp";
        var tmpPath = Path.Combine(dir, tmpName);
        var rollingBak = path + ".bak";
        var permanentBak = path + PermanentBackupSuffix;
        var fileExists = File.Exists(path);

        try
        {
            using (var fs = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var sw = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                sw.Write(candidate);
                sw.Flush();
                fs.Flush(flushToDisk: true);
            }

            // One-time permanent backup of the PRE-EDIT file. CreateNew
            // ensures we never overwrite an existing permanent backup.
            // We snapshot the bytes as they were when Load read them
            // (originalText), not the freshly-re-read currentText —
            // they will be byte-identical because we passed the
            // concurrency check, but using originalText keeps the
            // semantics clear: "what the user was looking at when they
            // opened Settings".
            if (fileExists && !File.Exists(permanentBak))
            {
                try
                {
                    using var bfs = new FileStream(
                        permanentBak, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    using var bsw = new StreamWriter(
                        bfs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    bsw.Write(originalText);
                    bsw.Flush();
                    bfs.Flush(flushToDisk: true);
                }
                catch (IOException)
                {
                    // Racing with another writer or filesystem refusing
                    // — not worth aborting the main save for.
                }
            }

            if (fileExists)
            {
                File.Replace(tmpPath, path, rollingBak, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tmpPath, path);
            }
        }
        catch
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* best effort */ }
            throw;
        }
    }

    // ---- YAML <-> JSON conversion ------------------------------------------

    private static JsonElement YamlToJson(YamlNode node)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteYamlAsJson(node, writer);
        }
        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }

    private static void WriteYamlAsJson(YamlNode node, Utf8JsonWriter writer)
    {
        switch (node)
        {
            case YamlMappingNode map:
                writer.WriteStartObject();
                foreach (var kv in map.Children)
                {
                    var keyText = (kv.Key as YamlScalarNode)?.Value ?? kv.Key.ToString();
                    writer.WritePropertyName(keyText ?? string.Empty);
                    WriteYamlAsJson(kv.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case YamlSequenceNode seq:
                writer.WriteStartArray();
                foreach (var child in seq.Children) WriteYamlAsJson(child, writer);
                writer.WriteEndArray();
                break;
            case YamlScalarNode scalar:
                WriteScalarAsJson(scalar, writer);
                break;
            default:
                writer.WriteStringValue(node.ToString());
                break;
        }
    }

    private static void WriteScalarAsJson(YamlScalarNode scalar, Utf8JsonWriter writer)
    {
        var value = scalar.Value;
        // If the scalar was explicitly quoted in source, treat as
        // string. If it was plain (no quotes), attempt type inference
        // per YAML 1.2 core schema.
        var isQuoted = scalar.Style == ScalarStyle.SingleQuoted
                       || scalar.Style == ScalarStyle.DoubleQuoted;

        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }
        if (isQuoted)
        {
            writer.WriteStringValue(value);
            return;
        }
        // Plain / folded / literal — try typed parse.
        if (value.Length == 0 || value == "~" || value.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            writer.WriteNullValue();
            return;
        }
        if (value == "true" || value == "True" || value == "TRUE")
        {
            writer.WriteBooleanValue(true);
            return;
        }
        if (value == "false" || value == "False" || value == "FALSE")
        {
            writer.WriteBooleanValue(false);
            return;
        }
        if (long.TryParse(value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var ll))
        {
            writer.WriteNumberValue(ll);
            return;
        }
        if (double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var dd))
        {
            writer.WriteNumberValue(dd);
            return;
        }
        writer.WriteStringValue(value);
    }

    private static YamlNode JsonToYaml(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                var map = new YamlMappingNode();
                foreach (var prop in el.EnumerateObject())
                {
                    map.Add(new YamlScalarNode(prop.Name), JsonToYaml(prop.Value));
                }
                return map;
            case JsonValueKind.Array:
                var seq = new YamlSequenceNode();
                foreach (var child in el.EnumerateArray())
                {
                    seq.Add(JsonToYaml(child));
                }
                return seq;
            case JsonValueKind.String:
                // Default Style "Any" lets the emitter pick quoting; for
                // strings that look like bools / ints we explicitly
                // double-quote to avoid YAML re-typing them on parse.
                var sv = el.GetString() ?? string.Empty;
                if (LooksLikeOtherType(sv))
                {
                    return new YamlScalarNode(sv) { Style = ScalarStyle.DoubleQuoted };
                }
                return new YamlScalarNode(sv);
            case JsonValueKind.Number:
                return new YamlScalarNode(el.GetRawText()) { Style = ScalarStyle.Plain };
            case JsonValueKind.True:
                return new YamlScalarNode("true") { Style = ScalarStyle.Plain };
            case JsonValueKind.False:
                return new YamlScalarNode("false") { Style = ScalarStyle.Plain };
            case JsonValueKind.Null:
                return new YamlScalarNode("null") { Style = ScalarStyle.Plain };
            default:
                throw new InvalidOperationException($"Unsupported JSON kind {el.ValueKind}");
        }
    }

    private static bool LooksLikeOtherType(string s)
    {
        if (s.Length == 0) return false;
        if (s == "~") return true;
        if (s.Equals("null", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.Equals("false", StringComparison.OrdinalIgnoreCase)) return true;
        if (long.TryParse(s, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out _)) return true;
        if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out _)) return true;
        return false;
    }

    private static string ComputeSha256Hex(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
