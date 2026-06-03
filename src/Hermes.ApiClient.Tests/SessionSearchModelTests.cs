using System.Text.Json;
using Hermes.ApiClient;
using Hermes.ApiClient.Models;

namespace Hermes.ApiClient.Tests;

/// <summary>
/// Contract tests for <c>/api/sessions/search</c>. The shape and field
/// names here come from the live Hermes gateway (<c>web_server.py</c>
/// in NousResearch/hermes-agent). If the gateway renames a field these
/// tests blow up at the contract layer instead of the UI silently
/// dropping the snippet or session id.
/// </summary>
public class SessionSearchModelTests
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Lifted from the shape <c>web_server.py</c> emits for FTS5 hits.
    /// The <c>&gt;&gt;&gt;…&lt;&lt;&lt;</c> markers are part of the
    /// gateway's snippet config and must survive deserialization verbatim
    /// (the view layer strips them — not the contract layer).
    /// </summary>
    private const string LiveSearchResponseJson = """
    {
      "results": [
        {
          "session_id": "abc123",
          "lineage_root": "abc000",
          "snippet": "...we fixed the >>>scroll<<< bouncing by...",
          "role": "assistant",
          "source": "cli",
          "model": "anthropic/claude-sonnet-4",
          "session_started": 1733175600.5
        },
        {
          "session_id": "def456",
          "lineage_root": null,
          "snippet": ">>>scroll<<< sticky offset",
          "role": "user",
          "source": null,
          "model": null,
          "session_started": null
        }
      ]
    }
    """;

    [Fact]
    public void Deserialize_LiveResponse_PopulatesSnakeCaseFields()
    {
        var resp = JsonSerializer.Deserialize<SessionSearchResponse>(LiveSearchResponseJson, Opts);
        Assert.NotNull(resp);
        Assert.Equal(2, resp!.Results.Count);

        var first = resp.Results[0];
        Assert.Equal("abc123", first.SessionId);
        Assert.Equal("abc000", first.LineageRoot);
        Assert.Equal("...we fixed the >>>scroll<<< bouncing by...", first.Snippet);
        Assert.Equal("assistant", first.Role);
        Assert.Equal("cli", first.Source);
        Assert.Equal("anthropic/claude-sonnet-4", first.Model);
        Assert.Equal(1733175600.5, first.SessionStarted);

        // Nullable fields really come through as null, not empty strings.
        var second = resp.Results[1];
        Assert.Null(second.LineageRoot);
        Assert.Null(second.Source);
        Assert.Null(second.Model);
        Assert.Null(second.SessionStarted);
    }

    [Fact]
    public void Deserialize_EmptyResults_RoundTripsToEmptyList()
    {
        const string body = """{"results":[]}""";
        var resp = JsonSerializer.Deserialize<SessionSearchResponse>(body, Opts);
        Assert.NotNull(resp);
        Assert.Empty(resp!.Results);
    }

    [Fact]
    public void BuildSearchPath_EscapesQueryAndClampsLimit()
    {
        // Space, ampersand, hash all need encoding so they don't break the
        // query-string parse on the server.
        var path = HermesApiClient.BuildSearchPath("scroll & bounce #fix", limit: 50);
        Assert.StartsWith("/api/sessions/search?q=", path);
        Assert.Contains("scroll%20%26%20bounce%20%23fix", path);
        Assert.EndsWith("limit=50", path);
    }

    [Fact]
    public void BuildSearchPath_ClampsLimitOutOfRange()
    {
        // Server has no defense against limit=1_000_000 — we cap on the
        // client side at 200 so a misbehaving caller can't ask the gateway
        // for the entire corpus on every keystroke.
        Assert.Contains("limit=200", HermesApiClient.BuildSearchPath("x", limit: 100_000));
        Assert.Contains("limit=1", HermesApiClient.BuildSearchPath("x", limit: 0));
        Assert.Contains("limit=1", HermesApiClient.BuildSearchPath("x", limit: -5));
    }

    [Fact]
    public void BuildSearchPath_TrimsQuery()
    {
        // Leading/trailing whitespace would otherwise become %20 wrappers
        // that the gateway treats as literal — kill them at the boundary.
        var path = HermesApiClient.BuildSearchPath("   docker   ", limit: 10);
        Assert.Contains("q=docker&", path);
    }
}
