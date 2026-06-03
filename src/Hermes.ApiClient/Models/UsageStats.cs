using System.Text.Json;

namespace Hermes.ApiClient.Models;

/// <summary>
/// Per-run token accounting parsed out of the <c>usage</c> object on a
/// <c>run.completed</c> SSE event (and from <see cref="SessionDetail"/> for
/// session totals).
///
/// <para>The parser is defensive about field naming: Hermes' own gateway
/// uses snake_case <c>input_tokens / output_tokens / cache_read_tokens /
/// cache_write_tokens / reasoning_tokens</c>, but the SSE payload may also
/// surface OpenAI-style (<c>prompt_tokens / completion_tokens /
/// cached_tokens</c>) or Anthropic-style
/// (<c>cache_creation_input_tokens / cache_read_input_tokens</c>) depending
/// on the upstream model adapter. We try all the common variants and merge
/// whatever's present rather than insisting on one schema.</para>
/// </summary>
public sealed record UsageStats(
    long? InputTokens = null,
    long? OutputTokens = null,
    long? CachedReadTokens = null,
    long? CachedWriteTokens = null,
    long? ReasoningTokens = null)
{
    /// <summary>Sum of <see cref="InputTokens"/> + <see cref="OutputTokens"/>
    /// when either is present; <see langword="null"/> when both are missing
    /// (so callers can distinguish "0 tokens" from "no usage reported").</summary>
    public long? TotalTokens =>
        InputTokens is null && OutputTokens is null
            ? null
            : (InputTokens ?? 0) + (OutputTokens ?? 0);

    /// <summary>True only when at least one field reports a positive count.
    /// Treats both <see langword="null"/> and <c>0</c> as "nothing to show"
    /// — explicit zeros from the gateway (e.g. <c>{"input_tokens": 0}</c>)
    /// are noise, not signal, and the chip / footer should stay hidden
    /// rather than render "↓ 0 in · ↑ 0 out".</summary>
    public bool HasAny => InputTokens is > 0
                       || OutputTokens is > 0
                       || CachedReadTokens is > 0
                       || CachedWriteTokens is > 0
                       || ReasoningTokens is > 0;

    /// <summary>
    /// Combines two usage snapshots. Used to roll per-turn usage into a
    /// running session total in the chat header. <see langword="null"/>
    /// operands behave as zero so callers don't have to null-check before
    /// summing.
    /// </summary>
    public UsageStats Add(UsageStats? other)
    {
        if (other is null) return this;
        return new UsageStats(
            Sum(InputTokens, other.InputTokens),
            Sum(OutputTokens, other.OutputTokens),
            Sum(CachedReadTokens, other.CachedReadTokens),
            Sum(CachedWriteTokens, other.CachedWriteTokens),
            Sum(ReasoningTokens, other.ReasoningTokens));

        static long? Sum(long? a, long? b) =>
            (a is null && b is null) ? null : (a ?? 0) + (b ?? 0);
    }

    /// <summary>
    /// Parses a raw JSON <c>usage</c> object (as captured by
    /// <see cref="ChatStreamEvent.RawData"/> / <see cref="RunCompletedEvent.UsageJson"/>)
    /// into a typed snapshot. Returns <see langword="null"/> on null / empty /
    /// invalid input rather than throwing, because losing token telemetry
    /// must never crash a chat stream.
    /// </summary>
    public static UsageStats? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return FromElement(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Same as <see cref="TryParse"/> but takes an already-parsed
    /// element. Used by the streaming client to avoid round-tripping
    /// through JSON twice.</summary>
    public static UsageStats? FromElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        // Input / prompt tokens — accept all common spellings.
        var input = FirstLong(element, "input_tokens", "prompt_tokens");

        // Output / completion tokens.
        var output = FirstLong(element, "output_tokens", "completion_tokens");

        // Cache-read: Hermes uses cache_read_tokens, Anthropic uses
        // cache_read_input_tokens, OpenAI uses prompt_tokens_details.cached_tokens
        // and also surfaces a top-level cached_tokens on some adapters.
        var cacheRead = FirstLong(element,
            "cache_read_tokens",
            "cache_read_input_tokens",
            "cached_tokens");
        if (cacheRead is null &&
            element.TryGetProperty("prompt_tokens_details", out var ptd) &&
            ptd.ValueKind == JsonValueKind.Object)
        {
            cacheRead = FirstLong(ptd, "cached_tokens");
        }

        // Cache-write: Hermes / Anthropic spellings.
        var cacheWrite = FirstLong(element,
            "cache_write_tokens",
            "cache_creation_input_tokens");

        // Reasoning: Hermes top-level OR OpenAI's nested
        // completion_tokens_details.reasoning_tokens.
        var reasoning = FirstLong(element, "reasoning_tokens");
        if (reasoning is null &&
            element.TryGetProperty("completion_tokens_details", out var ctd) &&
            ctd.ValueKind == JsonValueKind.Object)
        {
            reasoning = FirstLong(ctd, "reasoning_tokens");
        }

        var stats = new UsageStats(input, output, cacheRead, cacheWrite, reasoning);
        return stats.HasAny ? stats : null;
    }

    /// <summary>Returns the first numeric value found under any of the named
    /// properties. Tolerates both integer and floating-point JSON numbers
    /// (e.g. some adapters emit <c>1234.0</c>).</summary>
    private static long? FirstLong(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (!obj.TryGetProperty(name, out var prop)) continue;
            if (prop.ValueKind == JsonValueKind.Number)
            {
                if (prop.TryGetInt64(out var i)) return i;
                if (prop.TryGetDouble(out var d)) return (long)d;
            }
        }
        return null;
    }
}
