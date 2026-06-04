using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Hermes.ApiClient;

/// <summary>
/// Runs a minimal MCP <c>initialize</c> handshake against a configured
/// server entry and reports whether the server is reachable. Supports
/// stdio (process spawn + newline-delimited JSON-RPC) and a simple
/// streamable HTTP transport (POST initialize + parse first JSON
/// response, with rudimentary SSE <c>data:</c> framing).
/// </summary>
/// <remarks>
/// This is a "does it answer the door" check, not a full MCP client.
/// V1 explicitly does not implement legacy bidirectional SSE (open
/// stream, discover endpoint, POST messages, read responses from the
/// stream) — servers that require that flow will surface as a generic
/// protocol error and the user is told to fall back to a real
/// connection from the Hermes gateway.
/// </remarks>
public sealed class McpServerTester
{
    private const string McpProtocolVersion = "2024-11-05";
    private const string ClientName = "hermes-winui";
    private const string ClientVersion = "0.5.0";

    private static readonly TimeSpan DefaultStdioTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan DefaultHttpTimeout = TimeSpan.FromSeconds(12);
    private const int MaxStdoutScanLines = 200;
    private const int MaxStdoutScanBytes = 256 * 1024;

    private static readonly Regex PlaceholderRegex = new(
        @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient _http;
    private readonly IReadOnlyDictionary<string, string> _dotEnv;

    /// <param name="handler">
    /// Optional HTTP handler injection for tests. Caller retains ownership.
    /// </param>
    /// <param name="dotEnv">
    /// Optional dictionary of <c>.env</c> values used to expand
    /// <c>${KEY}</c> placeholders in <c>env</c> / <c>headers</c> /
    /// <c>url</c> / <c>args</c>. Defaults to the values parsed by
    /// <see cref="HermesConfig.Load"/>. Passing an empty dictionary
    /// disables expansion entirely (useful for tests).
    /// </param>
    public McpServerTester(
        HttpMessageHandler? handler = null,
        IReadOnlyDictionary<string, string>? dotEnv = null)
    {
        _http = handler is null
            ? new HttpClient()
            : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = DefaultHttpTimeout;
        _dotEnv = dotEnv ?? TryLoadDotEnv();
    }

    private static IReadOnlyDictionary<string, string> TryLoadDotEnv()
    {
        try { return HermesConfig.Load().EnvVars; }
        catch { return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
    }

    public async Task<McpTestResult> TestAsync(JsonElement body, CancellationToken ct = default)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return McpTestResult.Failure(McpTestStatus.ConfigInvalid,
                "Config not an object", "Server body must be a JSON object.");

        var hasCommand = body.TryGetProperty("command", out _);
        var hasUrl = body.TryGetProperty("url", out _);

        if (hasCommand && hasUrl)
            return McpTestResult.Failure(McpTestStatus.ConfigInvalid,
                "Ambiguous transport", "Server has both 'command' and 'url' — pick one.");

        if (hasCommand) return await TestStdioAsync(body, ct).ConfigureAwait(false);
        if (hasUrl) return await TestHttpAsync(body, ct).ConfigureAwait(false);

        return McpTestResult.Failure(McpTestStatus.ConfigInvalid,
            "No transport configured",
            "Server needs either 'command' (stdio) or 'url' (http).");
    }

    // ====================================================================
    // STDIO
    // ====================================================================

    private async Task<McpTestResult> TestStdioAsync(JsonElement body, CancellationToken ct)
    {
        var command = ExtractCommand(body);
        if (string.IsNullOrWhiteSpace(command))
            return McpTestResult.Failure(McpTestStatus.ConfigInvalid,
                "Missing command", "Stdio servers must set 'command' to a non-empty string.");

        var rawArgs = ExtractArgs(body);
        var rawEnv = ExtractEnv(body);
        var args = rawArgs.Select(a => ExpandPlaceholders(a, _dotEnv)).ToList();
        var env = rawEnv.ToDictionary(
            kv => kv.Key,
            kv => ExpandPlaceholders(kv.Value, _dotEnv),
            StringComparer.Ordinal);

        var resolved = ResolveExecutable(command);
        if (resolved is null)
            return McpTestResult.Failure(McpTestStatus.CommandNotFound,
                $"Command not found: {command}",
                $"'{command}' isn't on PATH. Install it or use a full path.");

        var psi = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Use the parent's stdio encoding so we don't garble UTF-8 JSON.
        psi.StandardInputEncoding = new UTF8Encoding(false);
        psi.StandardOutputEncoding = new UTF8Encoding(false);
        psi.StandardErrorEncoding = new UTF8Encoding(false);

        if (NeedsCmdShim(resolved))
        {
            // .cmd / .bat files can't be exec'd directly on Windows; route
            // through cmd.exe with a single carefully-quoted Arguments
            // string. ArgumentList isn't safe here — cmd has its own quote
            // stripping rules and treating each arg separately breaks
            // shims with spaces in their paths.
            psi.FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe");
            psi.Arguments = BuildCmdShimArguments(resolved, args);
        }
        else
        {
            psi.FileName = resolved;
            foreach (var a in args) psi.ArgumentList.Add(a);
        }

        foreach (var (k, v) in env) psi.Environment[k] = v;

        Process? process = null;
        var stderrBuilder = new StringBuilder();

        try
        {
            process = new Process { StartInfo = psi };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (stderrBuilder)
                {
                    if (stderrBuilder.Length < 8 * 1024)
                        stderrBuilder.AppendLine(e.Data);
                }
            };

            try { process.Start(); }
            catch (Exception ex)
            {
                return McpTestResult.Failure(McpTestStatus.ProcessFailed,
                    "Failed to start", ex.Message);
            }
            process.BeginErrorReadLine();

            using var timeoutCts = new CancellationTokenSource(DefaultStdioTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            // Send initialize.
            var initRequest = BuildInitializeRequest(id: 1);
            await WriteJsonLineAsync(process.StandardInput, initRequest, linked.Token)
                .ConfigureAwait(false);

            // Scan stdout until we find a JSON-RPC response with id=1, or
            // until we hit a budget. Many real-world servers write log
            // garbage to stdout before responding — accept this gracefully
            // rather than failing on the first non-JSON line.
            var (initRespJson, scanError) = await ReadJsonRpcResponseAsync(
                process.StandardOutput, expectedId: 1, linked.Token).ConfigureAwait(false);

            if (initRespJson is null)
            {
                var trimmed = stderrBuilder.ToString().Trim();
                var detail = string.IsNullOrEmpty(trimmed)
                    ? scanError ?? "No JSON-RPC response received before timeout."
                    : $"{scanError ?? "No JSON-RPC response received."} stderr: {Truncate(trimmed, 400)}";
                return McpTestResult.Failure(McpTestStatus.ProtocolError, "No response", detail);
            }

            var (ok, serverName, serverVersion, errMessage) = ParseInitializeResponse(initRespJson);
            if (!ok)
            {
                return McpTestResult.Failure(McpTestStatus.ProtocolError,
                    "Initialize failed", errMessage ?? "Server returned an error.");
            }

            // Initialized notification. Required by spec; failure here is
            // logged but doesn't fail the test.
            try
            {
                await WriteJsonLineAsync(process.StandardInput,
                    new { jsonrpc = "2.0", method = "notifications/initialized" },
                    linked.Token).ConfigureAwait(false);
            }
            catch { /* server may close stdin early; not fatal */ }

            // Best-effort tools/list. Connection is already proven; this
            // just adds a nice "5 tools available" line. Failure → soft.
            int? toolCount = null;
            string? toolsListNote = null;
            try
            {
                await WriteJsonLineAsync(process.StandardInput,
                    new { jsonrpc = "2.0", id = 2, method = "tools/list" },
                    linked.Token).ConfigureAwait(false);
                var (toolsRespJson, _) = await ReadJsonRpcResponseAsync(
                    process.StandardOutput, expectedId: 2, linked.Token).ConfigureAwait(false);
                if (toolsRespJson is not null)
                {
                    toolCount = ParseToolsListResponse(toolsRespJson, out var hasMore);
                    if (hasMore && toolCount.HasValue)
                        toolsListNote = $"{toolCount.Value}+ tools";
                }
            }
            catch { toolsListNote = "tools/list skipped"; }

            return McpTestResult.Success(
                title: BuildSuccessTitle(serverName, serverVersion),
                detail: BuildSuccessDetail(toolCount, toolsListNote),
                serverName: serverName,
                serverVersion: serverVersion,
                toolCount: toolCount);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return McpTestResult.Failure(McpTestStatus.Timeout, "Cancelled", "Test cancelled.");
        }
        catch (OperationCanceledException)
        {
            var trimmed = stderrBuilder.ToString().Trim();
            var detail = string.IsNullOrEmpty(trimmed)
                ? "Server didn't respond within 12s."
                : $"Server didn't respond within 12s. stderr: {Truncate(trimmed, 400)}";
            return McpTestResult.Failure(McpTestStatus.Timeout, "Timed out", detail);
        }
        catch (Exception ex)
        {
            return McpTestResult.Failure(McpTestStatus.ProcessFailed, "Test failed", ex.Message);
        }
        finally
        {
            if (process is not null)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch { /* best-effort */ }
                process.Dispose();
            }
        }
    }

    private static async Task WriteJsonLineAsync(StreamWriter stdin, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        await stdin.WriteAsync(json.AsMemory(), ct).ConfigureAwait(false);
        await stdin.WriteAsync("\n".AsMemory(), ct).ConfigureAwait(false);
        await stdin.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Read newline-delimited output from <paramref name="reader"/> and
    /// return the first line that parses as a JSON-RPC response with the
    /// requested <paramref name="expectedId"/>. Lines that don't parse
    /// (server log output) are skipped silently. Notifications and
    /// responses with the wrong id are skipped. Stops at the byte/line/
    /// time budgets and returns (null, reason).
    /// </summary>
    private static async Task<(string? Json, string? Reason)> ReadJsonRpcResponseAsync(
        StreamReader reader, int expectedId, CancellationToken ct)
    {
        var totalBytes = 0;
        var totalLines = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            string? line;
            try { line = await reader.ReadLineAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return (null, $"stdout read failed: {ex.Message}"); }

            if (line is null) return (null, "Server closed stdout without a response.");
            totalLines++;
            totalBytes += line.Length;

            if (totalLines > MaxStdoutScanLines)
                return (null, $"Too many non-JSON lines on stdout (>{MaxStdoutScanLines}).");
            if (totalBytes > MaxStdoutScanBytes)
                return (null, "Too much stdout output before a JSON-RPC response.");

            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed[0] != '{') continue; // log line

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                if (!doc.RootElement.TryGetProperty("jsonrpc", out _)) continue;
                if (!doc.RootElement.TryGetProperty("id", out var idEl)) continue; // notification
                if (idEl.ValueKind != JsonValueKind.Number) continue;
                if (!idEl.TryGetInt32(out var id) || id != expectedId) continue;
                return (trimmed, null);
            }
            catch (JsonException) { continue; }
        }
    }

    // ====================================================================
    // HTTP
    // ====================================================================

    private async Task<McpTestResult> TestHttpAsync(JsonElement body, CancellationToken ct)
    {
        var rawUrl = ExtractUrl(body);
        if (string.IsNullOrWhiteSpace(rawUrl))
            return McpTestResult.Failure(McpTestStatus.ConfigInvalid,
                "Missing url", "HTTP servers must set 'url' to a non-empty string.");

        var expandedUrl = ExpandPlaceholders(rawUrl, _dotEnv);
        if (!Uri.TryCreate(expandedUrl, UriKind.Absolute, out var uri))
            return McpTestResult.Failure(McpTestStatus.ConfigInvalid,
                "Invalid url", $"Couldn't parse '{expandedUrl}' as an absolute URL.");

        var initRequest = BuildInitializeRequest(id: 1);
        var json = JsonSerializer.Serialize(initRequest, JsonOpts);

        using var req = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue(ClientName, ClientVersion));

        foreach (var (k, v) in ExtractHeaders(body))
        {
            var expanded = ExpandPlaceholders(v, _dotEnv);
            // Custom headers go on the request, not the content. Try the
            // request first; fall back to content for things like
            // Content-Length / Content-Type overrides.
            if (!req.Headers.TryAddWithoutValidation(k, expanded))
                req.Content?.Headers.TryAddWithoutValidation(k, expanded);
        }

        using var timeoutCts = new CancellationTokenSource(DefaultHttpTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            using var resp = await _http.SendAsync(
                req, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
            var rawBody = await resp.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                return McpTestResult.Failure(McpTestStatus.NetworkError,
                    $"HTTP {(int)resp.StatusCode}",
                    $"{resp.ReasonPhrase}. {Truncate(rawBody.Trim(), 300)}");
            }

            var jsonText = ExtractFirstJsonObject(rawBody, contentType);
            if (jsonText is null)
            {
                return McpTestResult.Failure(McpTestStatus.ProtocolError,
                    "No JSON response",
                    "Server returned 200 but the body didn't contain a JSON-RPC message. " +
                    "If this server uses bidirectional SSE (stream-then-POST), " +
                    "this tester doesn't support that transport yet — try it from the Hermes gateway directly.");
            }

            var (ok, serverName, serverVersion, errMessage) = ParseInitializeResponse(jsonText);
            if (!ok)
            {
                return McpTestResult.Failure(McpTestStatus.ProtocolError,
                    "Initialize failed", errMessage ?? "Server returned an error.");
            }

            return McpTestResult.Success(
                title: BuildSuccessTitle(serverName, serverVersion),
                detail: BuildSuccessDetail(null, null) + " (HTTP transport; tool count not probed)",
                serverName: serverName,
                serverVersion: serverVersion,
                toolCount: null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return McpTestResult.Failure(McpTestStatus.Timeout, "Cancelled", "Test cancelled.");
        }
        catch (OperationCanceledException)
        {
            return McpTestResult.Failure(McpTestStatus.Timeout, "Timed out", "Server didn't respond within 12s.");
        }
        catch (HttpRequestException ex)
        {
            return McpTestResult.Failure(McpTestStatus.NetworkError, "Network error", ex.Message);
        }
        catch (Exception ex)
        {
            return McpTestResult.Failure(McpTestStatus.ProtocolError, "Test failed", ex.Message);
        }
    }

    /// <summary>
    /// Pull the first JSON object out of an HTTP response body. Handles
    /// both plain <c>application/json</c> and a single SSE <c>data:</c>
    /// event prefix. Returns null if no top-level JSON object is found.
    /// </summary>
    internal static string? ExtractFirstJsonObject(string body, string contentType)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        if (contentType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
        {
            // SSE: scan for the first non-empty "data: { ... }" line.
            // Multi-line data: blocks are concatenated per the spec.
            var sb = new StringBuilder();
            foreach (var raw in body.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0)
                {
                    if (sb.Length > 0) break; // end of event
                    continue;
                }
                if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append(line[5..].TrimStart());
                }
            }
            var sseJson = sb.ToString().Trim();
            return TryReturnObject(sseJson);
        }

        return TryReturnObject(body.Trim());

        static string? TryReturnObject(string text)
        {
            if (text.Length == 0 || text[0] != '{') return null;
            try
            {
                using var doc = JsonDocument.Parse(text);
                return doc.RootElement.ValueKind == JsonValueKind.Object ? text : null;
            }
            catch (JsonException) { return null; }
        }
    }

    // ====================================================================
    // Pure helpers (testable in isolation)
    // ====================================================================

    internal static string? ExtractCommand(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return null;
        if (!body.TryGetProperty("command", out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    internal static IReadOnlyList<string> ExtractArgs(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return Array.Empty<string>();
        if (!body.TryGetProperty("args", out var el)) return Array.Empty<string>();
        if (el.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        var result = new List<string>(el.GetArrayLength());
        foreach (var item in el.EnumerateArray())
        {
            // Coerce numbers/bools to strings so a malformed config doesn't
            // silently drop args. Only objects/arrays get skipped.
            switch (item.ValueKind)
            {
                case JsonValueKind.String:
                    result.Add(item.GetString() ?? "");
                    break;
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    result.Add(item.ToString());
                    break;
            }
        }
        return result;
    }

    internal static IReadOnlyDictionary<string, string> ExtractEnv(JsonElement body)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (body.ValueKind != JsonValueKind.Object) return result;
        if (!body.TryGetProperty("env", out var el)) return result;
        if (el.ValueKind != JsonValueKind.Object) return result;
        foreach (var prop in el.EnumerateObject())
        {
            result[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString() ?? "",
                JsonValueKind.Number => prop.Value.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => "",
            };
        }
        return result;
    }

    internal static string? ExtractUrl(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return null;
        if (!body.TryGetProperty("url", out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    internal static IReadOnlyDictionary<string, string> ExtractHeaders(JsonElement body)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (body.ValueKind != JsonValueKind.Object) return result;
        if (!body.TryGetProperty("headers", out var el)) return result;
        if (el.ValueKind != JsonValueKind.Object) return result;
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
                result[prop.Name] = prop.Value.GetString() ?? "";
        }
        return result;
    }

    /// <summary>
    /// Substitute <c>${KEY}</c> placeholders in <paramref name="value"/>
    /// using <paramref name="env"/> first then process environment.
    /// Unknown keys are left as-is (so the user sees the literal string
    /// in any failure message rather than a confusing empty value).
    /// </summary>
    internal static string ExpandPlaceholders(
        string value, IReadOnlyDictionary<string, string> env)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (!value.Contains("${", StringComparison.Ordinal)) return value;
        return PlaceholderRegex.Replace(value, m =>
        {
            var key = m.Groups[1].Value;
            if (env.TryGetValue(key, out var v)) return v;
            var processVal = Environment.GetEnvironmentVariable(key);
            return processVal ?? m.Value;
        });
    }

    internal static object BuildInitializeRequest(int id)
    {
        return new
        {
            jsonrpc = "2.0",
            id,
            method = "initialize",
            @params = new
            {
                protocolVersion = McpProtocolVersion,
                capabilities = new { },
                clientInfo = new { name = ClientName, version = ClientVersion },
            },
        };
    }

    /// <summary>
    /// Returns (success, serverName?, serverVersion?, errorMessage?).
    /// </summary>
    internal static (bool Ok, string? Name, string? Version, string? Error) ParseInitializeResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (false, null, null, "Response was not a JSON object.");

            if (root.TryGetProperty("error", out var errEl))
            {
                var msg = errEl.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString()
                    : errEl.ToString();
                var code = errEl.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number
                    ? c.GetInt32().ToString()
                    : null;
                var full = code is null ? msg : $"({code}) {msg}";
                return (false, null, null, full);
            }

            if (!root.TryGetProperty("result", out var resultEl))
                return (false, null, null, "Response missing 'result' or 'error' field.");

            string? name = null, version = null;
            if (resultEl.TryGetProperty("serverInfo", out var info)
                && info.ValueKind == JsonValueKind.Object)
            {
                if (info.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    name = n.GetString();
                if (info.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                    version = v.GetString();
            }
            return (true, name, version, null);
        }
        catch (JsonException ex)
        {
            return (false, null, null, $"Couldn't parse response as JSON: {ex.Message}");
        }
    }

    internal static int? ParseToolsListResponse(string json, out bool hasMore)
    {
        hasMore = false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("result", out var resultEl)) return null;
            if (resultEl.ValueKind != JsonValueKind.Object) return null;
            if (!resultEl.TryGetProperty("tools", out var tools)) return null;
            if (tools.ValueKind != JsonValueKind.Array) return null;
            if (resultEl.TryGetProperty("nextCursor", out var cursor)
                && cursor.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(cursor.GetString()))
            {
                hasMore = true;
            }
            return tools.GetArrayLength();
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Search PATH (and PATHEXT on Windows) for an executable. Paths
    /// containing a separator are returned as-is if they exist.
    /// </summary>
    internal static string? ResolveExecutable(
        string command, string? pathOverride = null, string? pathExtOverride = null)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        if (command.Contains(Path.DirectorySeparatorChar)
            || command.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(command) ? Path.GetFullPath(command) : null;
        }

        var path = pathOverride ?? Environment.GetEnvironmentVariable("PATH") ?? "";
        var dirs = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        string[] exts;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var pathExt = pathExtOverride
                ?? Environment.GetEnvironmentVariable("PATHEXT")
                ?? ".COM;.EXE;.BAT;.CMD";
            exts = pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries);
        }
        else
        {
            exts = new[] { "" };
        }

        var commandHasExt = Path.HasExtension(command);

        foreach (var dir in dirs)
        {
            var trimmedDir = dir.Trim().Trim('"');
            if (string.IsNullOrEmpty(trimmedDir)) continue;

            // If the user already specified an extension, try the bare name first.
            if (commandHasExt)
            {
                var full = Path.Combine(trimmedDir, command);
                if (File.Exists(full)) return Path.GetFullPath(full);
            }

            foreach (var ext in exts)
            {
                var full = Path.Combine(trimmedDir, command + ext);
                if (File.Exists(full)) return Path.GetFullPath(full);
            }
        }
        return null;
    }

    /// <summary>
    /// True when the resolved executable needs <c>cmd.exe /c</c> to run
    /// on Windows (.cmd / .bat shims, plus Python's stub launchers that
    /// some package managers ship).
    /// </summary>
    internal static bool NeedsCmdShim(string resolvedPath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
        var ext = Path.GetExtension(resolvedPath);
        return ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds the single <c>Arguments</c> string for <c>cmd.exe</c>:
    /// <c>/d /s /c "&quot;&lt;exe&gt;&quot; &quot;arg1&quot; &quot;arg2&quot;"</c>.
    /// <para>
    /// cmd.exe with <c>/s</c> + <c>/c</c> uses a single rule for the
    /// outer quotes: keep the first and last quote, leave everything
    /// between alone. We exploit that by wrapping the whole inner command
    /// in quotes and then quoting each token individually using Win32
    /// CommandLineToArgvW conventions (which is what <c>npx.cmd</c> and
    /// its parsers expect).
    /// </para>
    /// </summary>
    internal static string BuildCmdShimArguments(string exePath, IReadOnlyList<string> args)
    {
        var inner = new StringBuilder();
        inner.Append(QuoteForCmd(exePath));
        foreach (var arg in args)
        {
            inner.Append(' ');
            inner.Append(QuoteForCmd(arg));
        }
        // /d disables AutoRun; /s + outer quotes is the "keep both quotes
        // unchanged" mode that lets us safely embed quoted tokens.
        return $"/d /s /c \"{inner}\"";
    }

    /// <summary>
    /// Quote <paramref name="arg"/> using the CommandLineToArgvW algorithm
    /// (the rules MSVCRT and .NET both follow). Embedded backslashes
    /// followed by a quote get doubled; embedded quotes get backslash-
    /// escaped. We also escape cmd.exe meta-characters by always quoting,
    /// because the call site wraps everything in <c>cmd /s /c "..."</c>
    /// which suppresses cmd's internal quote stripping.
    /// </summary>
    internal static string QuoteForCmd(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return "\"\"";
        var sb = new StringBuilder();
        sb.Append('"');
        var backslashes = 0;
        foreach (var ch in arg)
        {
            if (ch == '\\') { backslashes++; continue; }
            if (ch == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                backslashes = 0;
                continue;
            }
            if (backslashes > 0) { sb.Append('\\', backslashes); backslashes = 0; }
            sb.Append(ch);
        }
        if (backslashes > 0) sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }

    // ====================================================================
    // Display helpers
    // ====================================================================

    private static string BuildSuccessTitle(string? name, string? version)
    {
        if (string.IsNullOrEmpty(name)) return "Connected";
        return string.IsNullOrEmpty(version)
            ? $"Connected to {name}"
            : $"Connected to {name} {version}";
    }

    private static string BuildSuccessDetail(int? toolCount, string? note)
    {
        var sb = new StringBuilder();
        sb.Append("MCP initialize handshake succeeded.");
        if (note is not null)
        {
            sb.Append(' ').Append(note).Append('.');
        }
        else if (toolCount.HasValue)
        {
            sb.Append(' ');
            sb.Append(toolCount.Value == 1 ? "1 tool available." : $"{toolCount.Value} tools available.");
        }
        return sb.ToString();
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}

public enum McpTestStatus
{
    Success,
    ConfigInvalid,
    CommandNotFound,
    ProcessFailed,
    Timeout,
    ProtocolError,
    NetworkError,
}

public sealed record McpTestResult(
    McpTestStatus Status,
    string Title,
    string Detail,
    string? ServerName = null,
    string? ServerVersion = null,
    int? ToolCount = null)
{
    public bool IsSuccess => Status == McpTestStatus.Success;

    public static McpTestResult Success(string title, string detail,
        string? serverName, string? serverVersion, int? toolCount)
        => new(McpTestStatus.Success, title, detail, serverName, serverVersion, toolCount);

    public static McpTestResult Failure(McpTestStatus status, string title, string detail)
        => new(status, title, detail);
}
