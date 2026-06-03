using Hermes.ApiClient;

namespace Hermes.ApiClient.Tests;

/// <summary>
/// Verifies that the API client converts raw gateway error bodies into
/// readable strings before throwing. The motivating bug was an InfoBar
/// rendering <c>400 Bad Request: {"error": "Prompt must be \u2264 5000 characters"}</c>
/// — the JSON-escaped <c>\u2264</c> survived because we never parsed
/// the body.
/// </summary>
public class HermesApiClientErrorFormattingTests
{
    [Fact]
    public void HermesError_JsonBody_ReturnsDecodedErrorString()
    {
        const string body = """{"error":"Prompt must be \u2264 5000 characters"}""";
        var msg = HermesApiClient.FormatErrorBody(400, "Bad Request", body);
        // Both checks matter: the decode (raw \u escape gone), and the
        // strip (no HTTP/JSON noise around the message).
        Assert.Equal("Prompt must be ≤ 5000 characters", msg);
    }

    [Fact]
    public void HermesError_JsonBodyWithoutErrorKey_FallsBackToRaw()
    {
        // Some endpoints return JSON without an "error" key — we should
        // still surface *something* useful, not silently drop the body.
        const string body = """{"detail":"thing went wrong"}""";
        var msg = HermesApiClient.FormatErrorBody(500, "Internal Server Error", body);
        Assert.Contains("500", msg);
        Assert.Contains("thing went wrong", msg);
    }

    [Fact]
    public void HermesError_NonJsonBody_FallsBackToRaw()
    {
        // Plain text bodies (proxies, nginx 502s, etc.) shouldn't blow up
        // the JSON parser path — they should round-trip into the message
        // verbatim with a status prefix for context.
        const string body = "upstream connect error";
        var msg = HermesApiClient.FormatErrorBody(502, "Bad Gateway", body);
        Assert.Contains("502", msg);
        Assert.Contains("upstream connect error", msg);
    }

    [Fact]
    public void HermesError_EmptyBody_StillReadable()
    {
        // Some responses (HEAD-ish, 204-class misuses) come back with no
        // body at all. We just want the status, not a crash.
        var msg = HermesApiClient.FormatErrorBody(503, "Service Unavailable", "");
        Assert.Contains("503", msg);
    }

    [Fact]
    public void HermesError_EmptyErrorString_FallsBackToRaw()
    {
        // Defensive: server sends the right shape but with an empty value.
        // Falling through to the raw body is more useful than surfacing
        // nothing at all.
        const string body = """{"error":""}""";
        var msg = HermesApiClient.FormatErrorBody(400, "Bad Request", body);
        Assert.Contains("400", msg);
    }
}
