using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Hermes.ApiClient.Models;

namespace Hermes.ApiClient;

/// <summary>
/// Streaming surface of the Hermes gateway: SSE chat turns and run lifecycle.
///
/// Lives separately from <see cref="HermesApiClient"/> because:
/// 1. SSE needs <c>Timeout = Timeout.InfiniteTimeSpan</c>, which would break
///    the polling client (6s timeout protects the tray from hangs).
/// 2. The polling client must NEVER buffer a response — SSE requires
///    <c>HttpCompletionOption.ResponseHeadersRead</c> so we get the stream
///    handle while the server keeps writing.
/// 3. Cancellation semantics differ: a Stop request must use an independent
///    CTS so cancelling the stream doesn't cancel the stop POST.
/// </summary>
public sealed class HermesStreamingClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    public HermesConfig Config { get; }

    public HermesStreamingClient(HermesConfig config)
    {
        Config = config;
        _http = new HttpClient
        {
            BaseAddress = config.BaseAddress,
            Timeout = Timeout.InfiniteTimeSpan, // SSE streams stay open indefinitely
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        }
    }

    /// <summary>
    /// Streams one synchronous chat turn against <paramref name="sessionId"/>.
    /// Yields parsed events; the call returns when the server closes the
    /// stream (run.completed / error) or the caller cancels.
    /// </summary>
    public async IAsyncEnumerable<ChatStreamEvent> StreamSessionChatAsync(
        string sessionId,
        string input,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{sessionId}/chat/stream")
        {
            Content = JsonContent.Create(new { input }, options: JsonOpts),
        };

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        await foreach (var frame in SseFrameReader.ReadAsync(stream, ct).ConfigureAwait(false))
        {
            yield return Parse(frame);
        }
    }

    /// <summary>
    /// Stops a run with its own short-timeout CTS. Callers must NOT pass the
    /// same token they used for the stream — that token is already cancelled
    /// by the time Stop is wanted, which would cancel the stop POST itself
    /// before it even leaves the box.
    /// </summary>
    public async Task<bool> StopRunAsync(string runId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/v1/runs/{runId}/stop");
        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static ChatStreamEvent Parse(SseFrameReader.Frame frame)
    {
        var name = frame.Event ?? "message";
        var data = frame.Data ?? "";

        try
        {
            return name switch
            {
                "assistant.delta" or "delta" or "message.delta" =>
                    ParseDelta(data),
                "tool.started" or "tool_call.started" =>
                    ParseToolStarted(data),
                "tool.completed" or "tool_call.completed" =>
                    ParseToolCompleted(data),
                "tool.progress" =>
                    ParseToolProgress(data),
                "assistant.completed" or "message.completed" =>
                    ParseAssistantCompleted(data),
                "run.completed" or "response.completed" =>
                    ParseRunCompleted(data),
                "error" or "run.error" or "response.error" =>
                    ParseError(data),
                _ => new UnknownStreamEvent { RawEvent = name, RawData = data },
            };
        }
        catch
        {
            // Defensive: if a payload doesn't parse, surface it as Unknown rather
            // than crashing the whole stream. The UI will log + keep going.
            return new UnknownStreamEvent { RawEvent = name, RawData = data };
        }
    }

    private static AssistantDeltaEvent ParseDelta(string data)
    {
        // Two shapes seen in practice: a bare string OR { "text": "...", "delta": "..." }
        string text = data;
        if (data.Length > 0 && (data[0] == '{' || data[0] == '['))
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    text = t.GetString() ?? "";
                else if (root.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String)
                    text = d.GetString() ?? "";
                else if (root.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    text = c.GetString() ?? "";
            }
        }
        return new AssistantDeltaEvent { RawEvent = "assistant.delta", RawData = data, Text = text };
    }

    private static ToolStartedEvent ParseToolStarted(string data)
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        return new ToolStartedEvent
        {
            RawEvent = "tool.started",
            RawData = data,
            // Hermes has two emission paths: one uses "tool_name" (api_server
            // tool_executor wrapper), the other uses "tool" (api_server direct
            // gateway callback). The legacy "name" is kept as a last resort.
            Name = TryGetString(root, "tool_name") ?? TryGetString(root, "tool") ?? TryGetString(root, "name"),
            CallId = TryGetString(root, "call_id") ?? TryGetString(root, "id"),
            ArgumentsJson = TryGetJson(root, "args") ?? TryGetJson(root, "arguments") ?? TryGetJson(root, "input"),
            Preview = TryGetString(root, "preview"),
        };
    }

    private static ToolCompletedEvent ParseToolCompleted(string data)
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        var isErr = false;
        if (root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True) isErr = true;
        if (root.TryGetProperty("error", out var er))
        {
            // Gateway "error" can be either a bool flag (api_server direct path)
            // or an error payload object (tool_executor path). Both indicate failure.
            if (er.ValueKind == JsonValueKind.True) isErr = true;
            else if (er.ValueKind == JsonValueKind.Object || er.ValueKind == JsonValueKind.String) isErr = true;
        }

        string? outText = TryGetString(root, "output") ?? TryGetString(root, "result");
        string? outJson = outText is null ? TryGetJson(root, "output") ?? TryGetJson(root, "result") : null;

        double? duration = null;
        if (root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number)
            duration = d.GetDouble();

        return new ToolCompletedEvent
        {
            RawEvent = "tool.completed",
            RawData = data,
            Name = TryGetString(root, "tool_name") ?? TryGetString(root, "tool") ?? TryGetString(root, "name"),
            CallId = TryGetString(root, "call_id") ?? TryGetString(root, "id"),
            OutputText = outText,
            OutputJson = outJson,
            IsError = isErr,
            DurationSeconds = duration,
        };
    }

    private static ToolProgressEvent ParseToolProgress(string data)
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        return new ToolProgressEvent
        {
            RawEvent = "tool.progress",
            RawData = data,
            Name = TryGetString(root, "name") ?? TryGetString(root, "tool_name") ?? TryGetString(root, "tool"),
            CallId = TryGetString(root, "call_id") ?? TryGetString(root, "id"),
            Message = TryGetString(root, "message") ?? TryGetString(root, "text") ?? TryGetString(root, "delta"),
        };
    }

    private static RunCompletedEvent ParseRunCompleted(string data)
    {
        if (data.Length == 0 || data[0] != '{')
        {
            return new RunCompletedEvent { RawEvent = "run.completed", RawData = data, Output = data };
        }
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;

        // Hermes shape: { messages: [{ role: "assistant", content: "..." }, ...] }
        // The final assistant message content is our last-resort source of truth.
        string? finalContent = null;
        if (root.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
        {
            for (int i = msgs.GetArrayLength() - 1; i >= 0; i--)
            {
                var m = msgs[i];
                if (m.TryGetProperty("role", out var r) &&
                    r.ValueKind == JsonValueKind.String &&
                    r.GetString() == "assistant" &&
                    m.TryGetProperty("content", out var c) &&
                    c.ValueKind == JsonValueKind.String)
                {
                    finalContent = c.GetString();
                    break;
                }
            }
        }

        return new RunCompletedEvent
        {
            RawEvent = "run.completed",
            RawData = data,
            Output = TryGetString(root, "output"),
            UsageJson = TryGetJson(root, "usage"),
            Usage = root.TryGetProperty("usage", out var usageEl)
                ? UsageStats.FromElement(usageEl)
                : null,
            RunId = TryGetString(root, "run_id"),
            FinalAssistantContent = finalContent,
        };
    }

    private static AssistantCompletedEvent ParseAssistantCompleted(string data)
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        return new AssistantCompletedEvent
        {
            RawEvent = "assistant.completed",
            RawData = data,
            Content = TryGetString(root, "content"),
            MessageId = TryGetString(root, "message_id"),
        };
    }

    private static StreamErrorEvent ParseError(string data)
    {
        string msg = data;
        if (data.Length > 0 && data[0] == '{')
        {
            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                msg = TryGetString(root, "message") ?? TryGetString(root, "error") ?? data;
            }
            catch { /* fall through to raw data */ }
        }
        return new StreamErrorEvent { RawEvent = "error", RawData = data, Message = msg };
    }

    private static string? TryGetString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            JsonValueKind.True or JsonValueKind.False => v.ToString(),
            _ => null,
        };
    }

    private static string? TryGetJson(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (v.ValueKind == JsonValueKind.String) return v.GetString();
        return v.GetRawText();
    }

    public void Dispose() => _http.Dispose();
}
