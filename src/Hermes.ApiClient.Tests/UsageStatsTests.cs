using System.Text.Json;
using Hermes.ApiClient.Models;

namespace Hermes.ApiClient.Tests;

/// <summary>
/// Exercises the gating semantics on <see cref="UsageStats"/> after the
/// "explicit zero" fixes. The chip / per-bubble footer rely on
/// <see cref="UsageStats.HasAny"/> for visibility and on
/// <see cref="UsageStats.FromElement"/> returning null for all-zero
/// payloads — if either regresses, the UI starts rendering
/// "↓ 0 in · ↑ 0 out" again, which is what these tests guard against.
/// </summary>
public class UsageStatsTests
{
    [Fact]
    public void HasAny_AllNull_IsFalse()
    {
        var stats = new UsageStats();
        Assert.False(stats.HasAny);
    }

    [Fact]
    public void HasAny_AllZero_IsFalse()
    {
        // Pre-fix this returned true (non-null != non-zero).
        var stats = new UsageStats(
            InputTokens: 0,
            OutputTokens: 0,
            CachedReadTokens: 0,
            CachedWriteTokens: 0,
            ReasoningTokens: 0);
        Assert.False(stats.HasAny);
    }

    [Fact]
    public void HasAny_PositiveInput_IsTrue()
    {
        Assert.True(new UsageStats(InputTokens: 1).HasAny);
    }

    [Fact]
    public void HasAny_PositiveOutput_IsTrue()
    {
        Assert.True(new UsageStats(OutputTokens: 100).HasAny);
    }

    [Fact]
    public void HasAny_PositiveCacheRead_IsTrue()
    {
        Assert.True(new UsageStats(CachedReadTokens: 5).HasAny);
    }

    [Fact]
    public void HasAny_PositiveCacheWrite_IsTrue()
    {
        Assert.True(new UsageStats(CachedWriteTokens: 5).HasAny);
    }

    [Fact]
    public void HasAny_PositiveReasoning_IsTrue()
    {
        Assert.True(new UsageStats(ReasoningTokens: 7).HasAny);
    }

    [Fact]
    public void HasAny_ZeroInput_PositiveOutput_IsTrue()
    {
        // Mixed: zero on one side shouldn't suppress the whole stats.
        Assert.True(new UsageStats(InputTokens: 0, OutputTokens: 200).HasAny);
    }

    [Fact]
    public void FromElement_AllZero_ReturnsNull()
    {
        // Critical regression guard: pre-fix the parser returned a
        // {0,0,0,0,0} stats which then rendered "↓ 0 in · ↑ 0 out".
        var json = """{"input_tokens": 0, "output_tokens": 0}""";
        using var doc = JsonDocument.Parse(json);
        var stats = UsageStats.FromElement(doc.RootElement);
        Assert.Null(stats);
    }

    [Fact]
    public void FromElement_PositiveInput_ReturnsStats()
    {
        var json = """{"input_tokens": 1234}""";
        using var doc = JsonDocument.Parse(json);
        var stats = UsageStats.FromElement(doc.RootElement);
        Assert.NotNull(stats);
        Assert.Equal(1234, stats!.InputTokens);
        Assert.Null(stats.OutputTokens);
    }

    [Fact]
    public void FromElement_OpenAiShape_Parses()
    {
        var json = """{"prompt_tokens": 100, "completion_tokens": 50}""";
        using var doc = JsonDocument.Parse(json);
        var stats = UsageStats.FromElement(doc.RootElement);
        Assert.NotNull(stats);
        Assert.Equal(100, stats!.InputTokens);
        Assert.Equal(50, stats.OutputTokens);
    }

    [Fact]
    public void FromElement_AnthropicCacheShape_Parses()
    {
        var json = """{"cache_creation_input_tokens": 200, "cache_read_input_tokens": 80}""";
        using var doc = JsonDocument.Parse(json);
        var stats = UsageStats.FromElement(doc.RootElement);
        Assert.NotNull(stats);
        Assert.Equal(200, stats!.CachedWriteTokens);
        Assert.Equal(80, stats.CachedReadTokens);
    }

    [Fact]
    public void FromElement_NonObject_ReturnsNull()
    {
        var json = "[]";
        using var doc = JsonDocument.Parse(json);
        var stats = UsageStats.FromElement(doc.RootElement);
        Assert.Null(stats);
    }

    [Fact]
    public void TryParse_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(UsageStats.TryParse(null));
        Assert.Null(UsageStats.TryParse(""));
        Assert.Null(UsageStats.TryParse("   "));
    }

    [Fact]
    public void TryParse_Malformed_ReturnsNullNotThrow()
    {
        // Token telemetry must never crash the chat stream.
        Assert.Null(UsageStats.TryParse("{not valid json"));
    }

    [Fact]
    public void Add_TwoStats_Sums()
    {
        var a = new UsageStats(InputTokens: 100, OutputTokens: 50);
        var b = new UsageStats(InputTokens: 20, OutputTokens: 30, CachedReadTokens: 10);
        var sum = a.Add(b);
        Assert.Equal(120, sum.InputTokens);
        Assert.Equal(80, sum.OutputTokens);
        Assert.Equal(10, sum.CachedReadTokens);
    }

    [Fact]
    public void Add_NullOther_ReturnsSelf()
    {
        var a = new UsageStats(InputTokens: 100);
        var sum = a.Add(null);
        Assert.Equal(100, sum.InputTokens);
    }

    [Fact]
    public void Add_NullField_PreservedAsNullWhenBothNull()
    {
        var a = new UsageStats(InputTokens: 100);
        var b = new UsageStats(OutputTokens: 50);
        var sum = a.Add(b);
        // CachedReadTokens was null in both → stays null
        Assert.Null(sum.CachedReadTokens);
        // InputTokens: 100 + null → 100 (null treated as 0 for sums)
        Assert.Equal(100, sum.InputTokens);
        Assert.Equal(50, sum.OutputTokens);
    }
}
