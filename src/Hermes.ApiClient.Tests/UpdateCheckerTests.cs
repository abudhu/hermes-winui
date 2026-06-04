using System.Net;
using System.Net.Http;
using System.Text;
using Hermes.ApiClient;

namespace Hermes.ApiClient.Tests;

/// <summary>
/// Covers <see cref="UpdateChecker"/>'s response-shape handling and
/// version comparison. Network is stubbed via <see cref="FakeHandler"/>
/// so these tests run offline and deterministically.
/// </summary>
public class UpdateCheckerTests
{
    [Theory]
    [InlineData("v0.5.0", "0.5.0")]
    [InlineData("V0.5.0", "0.5.0")]
    [InlineData("0.5.0", "0.5.0")]
    [InlineData("", "")]
    public void NormalizeTag_Strips_Leading_V(string input, string expected)
    {
        Assert.Equal(expected, UpdateChecker.NormalizeTag(input));
    }

    [Fact]
    public void CompareVersions_UpToDate_When_Equal()
    {
        var r = UpdateChecker.CompareVersions("0.5.0", "v0.5.0", "https://example/release");
        Assert.Equal(UpdateStatus.UpToDate, r.Status);
        Assert.Equal("0.5.0", r.LatestVersion);
    }

    [Fact]
    public void CompareVersions_UpdateAvailable_When_Latest_Greater()
    {
        var r = UpdateChecker.CompareVersions("0.5.0", "v0.6.0", "https://example/release");
        Assert.Equal(UpdateStatus.UpdateAvailable, r.Status);
        Assert.Equal("0.6.0", r.LatestVersion);
        Assert.Contains("0.6.0", r.Message);
    }

    [Fact]
    public void CompareVersions_AheadOfReleased_When_Current_Greater()
    {
        var r = UpdateChecker.CompareVersions("0.7.0", "v0.6.0", null);
        Assert.Equal(UpdateStatus.AheadOfReleased, r.Status);
        Assert.Equal("0.6.0", r.LatestVersion);
    }

    [Fact]
    public void CompareVersions_Falls_Back_For_Unparseable_Tag()
    {
        // Real-world tag we'd legitimately see: pre-release tags like "v1.0.0-beta1".
        var r = UpdateChecker.CompareVersions("0.5.0", "v1.0.0-beta1", "https://example/release");
        // System.Version can't parse "1.0.0-beta1", so we soft-fail to
        // "update available" with a guidance message rather than just
        // claiming things are fine.
        Assert.Equal(UpdateStatus.UpdateAvailable, r.Status);
        Assert.Contains("couldn't auto-compare", r.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_Returns_UpdateAvailable_For_Newer_Tag()
    {
        var handler = FakeHandler.Json(HttpStatusCode.OK, """
            { "tag_name": "v0.6.0", "html_url": "https://github.com/x/y/releases/tag/v0.6.0" }
            """);
        using var sut = new UpdateChecker("owner/repo", handler);
        var r = await sut.CheckAsync("0.5.0");
        Assert.Equal(UpdateStatus.UpdateAvailable, r.Status);
        Assert.Equal("0.6.0", r.LatestVersion);
        Assert.Equal("https://github.com/x/y/releases/tag/v0.6.0", r.LatestUrl);
    }

    [Fact]
    public async Task CheckAsync_Returns_NoReleases_On_404()
    {
        var handler = FakeHandler.Status(HttpStatusCode.NotFound);
        using var sut = new UpdateChecker("owner/repo", handler);
        var r = await sut.CheckAsync("0.5.0");
        Assert.Equal(UpdateStatus.NoReleases, r.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData((HttpStatusCode)429)]
    public async Task CheckAsync_Returns_RateLimited_For_403_And_429(HttpStatusCode code)
    {
        var handler = FakeHandler.Status(code);
        using var sut = new UpdateChecker("owner/repo", handler);
        var r = await sut.CheckAsync("0.5.0");
        Assert.Equal(UpdateStatus.RateLimited, r.Status);
    }

    [Fact]
    public async Task CheckAsync_Returns_NetworkError_On_500()
    {
        var handler = FakeHandler.Status(HttpStatusCode.InternalServerError);
        using var sut = new UpdateChecker("owner/repo", handler);
        var r = await sut.CheckAsync("0.5.0");
        Assert.Equal(UpdateStatus.NetworkError, r.Status);
    }

    [Fact]
    public async Task CheckAsync_Returns_NetworkError_When_Handler_Throws()
    {
        var handler = FakeHandler.Throws(new HttpRequestException("offline"));
        using var sut = new UpdateChecker("owner/repo", handler);
        var r = await sut.CheckAsync("0.5.0");
        Assert.Equal(UpdateStatus.NetworkError, r.Status);
        Assert.Contains("offline", r.Message);
    }

    [Fact]
    public async Task CheckAsync_Returns_NetworkError_For_Malformed_Payload()
    {
        var handler = FakeHandler.Json(HttpStatusCode.OK, "{ this is not json");
        using var sut = new UpdateChecker("owner/repo", handler);
        var r = await sut.CheckAsync("0.5.0");
        Assert.Equal(UpdateStatus.NetworkError, r.Status);
    }

    [Fact]
    public async Task CheckAsync_Returns_NetworkError_When_Tag_Missing()
    {
        var handler = FakeHandler.Json(HttpStatusCode.OK, "{ \"html_url\": \"x\" }");
        using var sut = new UpdateChecker("owner/repo", handler);
        var r = await sut.CheckAsync("0.5.0");
        Assert.Equal(UpdateStatus.NetworkError, r.Status);
    }

    [Fact]
    public async Task CheckAsync_Sends_UserAgent_Header()
    {
        // GitHub returns 403 to requests with no UA. Catch that we set
        // one, rather than waiting for prod to discover it.
        var handler = FakeHandler.Json(HttpStatusCode.OK, """
            { "tag_name": "v0.5.0", "html_url": "https://x" }
            """);
        using var sut = new UpdateChecker("owner/repo", handler);
        _ = await sut.CheckAsync("0.5.0");
        Assert.NotNull(handler.LastRequest);
        Assert.True(handler.LastRequest!.Headers.UserAgent.Count > 0,
            "UpdateChecker must send a User-Agent header (GitHub rejects requests without one).");
    }

    [Fact]
    public async Task CheckAsync_Sends_Accept_Github_Json_Header()
    {
        var handler = FakeHandler.Json(HttpStatusCode.OK, """
            { "tag_name": "v0.5.0", "html_url": "https://x" }
            """);
        using var sut = new UpdateChecker("owner/repo", handler);
        _ = await sut.CheckAsync("0.5.0");
        Assert.Contains(handler.LastRequest!.Headers.Accept,
            h => h.MediaType == "application/vnd.github+json");
    }

    [Fact]
    public async Task CheckAsync_Hits_Correct_Repo_Endpoint()
    {
        var handler = FakeHandler.Json(HttpStatusCode.OK, """
            { "tag_name": "v0.5.0", "html_url": "https://x" }
            """);
        using var sut = new UpdateChecker("foo/bar", handler);
        _ = await sut.CheckAsync("0.5.0");
        Assert.Equal(
            "https://api.github.com/repos/foo/bar/releases/latest",
            handler.LastRequest!.RequestUri?.ToString());
    }

    // ---- helpers ----------------------------------------------------------

    private sealed class FakeHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        private FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public static FakeHandler Status(HttpStatusCode code) =>
            new(_ => new HttpResponseMessage(code));

        public static FakeHandler Json(HttpStatusCode code, string body) =>
            new(_ => new HttpResponseMessage(code)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });

        public static FakeHandler Throws(Exception ex) =>
            new(_ => throw ex);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_respond(request));
        }
    }
}
