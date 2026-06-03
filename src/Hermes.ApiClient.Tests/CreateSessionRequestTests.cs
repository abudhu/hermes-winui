using System.Text.Json;
using Hermes.ApiClient.Models;

namespace Hermes.ApiClient.Tests;

/// <summary>
/// Serialization sanity around <see cref="CreateSessionRequest"/>. The
/// new <c>model</c> field is the one the chat header picker depends on;
/// if its JSON name regresses (e.g. someone changes the attribute or
/// renames the property without updating the attribute) the gateway
/// would silently fall back to its global default and the picker would
/// look like it works but actually wouldn't.
/// </summary>
public class CreateSessionRequestTests
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void Serialize_WithModel_EmitsModelField()
    {
        var req = new CreateSessionRequest(Title: null, Source: null, Model: "claude-opus-4");
        var json = JsonSerializer.Serialize(req, Opts);
        Assert.Contains("\"model\"", json);
        Assert.Contains("claude-opus-4", json);
    }

    [Fact]
    public void Serialize_WithoutModel_OmitsModelField()
    {
        // Null model means "let server pick default" — must NOT be sent as
        // an empty string or "null" literal that the server might interpret
        // as a deliberate override.
        var req = new CreateSessionRequest(Title: null, Source: null, Model: null);
        var json = JsonSerializer.Serialize(req, Opts);
        Assert.DoesNotContain("\"model\"", json);
    }

    [Fact]
    public void Roundtrip_PreservesModel()
    {
        var original = new CreateSessionRequest(Title: "t", Source: "api", Model: "gpt-5");
        var json = JsonSerializer.Serialize(original, Opts);
        var roundtripped = JsonSerializer.Deserialize<CreateSessionRequest>(json, Opts);
        Assert.NotNull(roundtripped);
        Assert.Equal("t", roundtripped!.Title);
        Assert.Equal("api", roundtripped.Source);
        Assert.Equal("gpt-5", roundtripped.Model);
    }
}
