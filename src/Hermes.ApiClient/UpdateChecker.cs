using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hermes.ApiClient;

/// <summary>
/// Status the About pane shows in its InfoBar after a "Check for updates" click.
/// </summary>
public enum UpdateStatus
{
    /// <summary>Current build matches the latest released tag.</summary>
    UpToDate,
    /// <summary>A newer release than the current build exists.</summary>
    UpdateAvailable,
    /// <summary>Current build is newer than the latest released tag —
    /// expected for in-development builds between releases.</summary>
    AheadOfReleased,
    /// <summary>Repo has no published releases yet (GitHub returned 404).</summary>
    NoReleases,
    /// <summary>GitHub returned 403/429 (rate limit or abuse detection).</summary>
    RateLimited,
    /// <summary>Anything else — network failure, malformed payload, timeout.</summary>
    NetworkError,
}

/// <summary>Result of a single <see cref="UpdateChecker.CheckAsync"/> call.</summary>
public sealed record UpdateCheckResult(
    UpdateStatus Status,
    string? LatestVersion = null,
    string? LatestUrl = null,
    string? Message = null);

/// <summary>
/// Asks GitHub for the latest published release of the Hermes WinUI
/// companion and compares it to the current build's version. Designed
/// for hand-driven "Check for updates" clicks from the About pane —
/// not for background polling. The caller controls cancellation; we
/// time out at 8s if GitHub is slow.
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _repoFullName;

    /// <summary>Default repo. Kept in one place so the about-pane wiring
    /// and the tests don't drift.</summary>
    public const string DefaultRepo = "abudhu/hermes-winui";

    public UpdateChecker(string repoFullName = DefaultRepo, HttpMessageHandler? handler = null)
    {
        _repoFullName = repoFullName;
        if (handler is null)
        {
            _http = new HttpClient();
            _ownsHttp = true;
        }
        else
        {
            // Don't take ownership of the caller's handler — they may
            // want to reuse / inspect it across multiple checks.
            _http = new HttpClient(handler, disposeHandler: false);
            _ownsHttp = true;
        }
        // GitHub's REST API requires a User-Agent or returns 403.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Hermes-WinUI/1.0 (+https://github.com/abudhu/hermes-winui)");
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.Timeout = TimeSpan.FromSeconds(8);
    }

    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        var url = $"https://api.github.com/repos/{_repoFullName}/releases/latest";
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                return new(UpdateStatus.NoReleases,
                    Message: "This repository hasn't published a release yet.");
            }
            // GitHub returns 403 with X-RateLimit-Remaining: 0 for rate
            // limiting and 429 for secondary abuse limits. Both map to
            // "try again later" from the user's POV.
            if ((int)resp.StatusCode == 403 || (int)resp.StatusCode == 429)
            {
                return new(UpdateStatus.RateLimited,
                    Message: "GitHub API rate limit reached. Try again in a few minutes.");
            }
            if (!resp.IsSuccessStatusCode)
            {
                return new(UpdateStatus.NetworkError,
                    Message: $"GitHub returned {(int)resp.StatusCode} {resp.ReasonPhrase}.");
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var release = await JsonSerializer.DeserializeAsync<GithubRelease>(
                stream, JsonOpts, ct).ConfigureAwait(false);

            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            {
                return new(UpdateStatus.NetworkError,
                    Message: "GitHub returned an empty or malformed release payload.");
            }

            return CompareVersions(currentVersion, release.TagName, release.HtmlUrl);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return new(UpdateStatus.NetworkError,
                Message: "GitHub didn't respond in time — check your connection and try again.");
        }
        catch (HttpRequestException ex)
        {
            return new(UpdateStatus.NetworkError,
                Message: $"Couldn't reach GitHub: {ex.Message}");
        }
        catch (JsonException ex)
        {
            return new(UpdateStatus.NetworkError,
                Message: $"GitHub release payload wasn't valid JSON: {ex.Message}");
        }
    }

    /// <summary>Strips a leading "v"/"V" from a release tag if present.
    /// Public so the comparison can be tested directly.</summary>
    internal static string NormalizeTag(string tag) =>
        (tag.Length > 0 && (tag[0] == 'v' || tag[0] == 'V')) ? tag[1..] : tag;

    /// <summary>Compares two version strings (with optional leading "v")
    /// using <see cref="System.Version"/>. Falls back to a soft
    /// "update available" result if either side can't be parsed,
    /// because telling the user about a tag they can go look at is
    /// more useful than silently failing.</summary>
    internal static UpdateCheckResult CompareVersions(string current, string latestTag, string? releaseUrl)
    {
        var latestNorm = NormalizeTag(latestTag);
        var currentNorm = NormalizeTag(current);

        if (!Version.TryParse(currentNorm, out var cur) || !Version.TryParse(latestNorm, out var lat))
        {
            return new(UpdateStatus.UpdateAvailable, latestNorm, releaseUrl,
                $"Latest release is {latestTag}; couldn't auto-compare against v{current}. " +
                "Visit the release page to verify.");
        }

        if (lat > cur)
        {
            return new(UpdateStatus.UpdateAvailable, latestNorm, releaseUrl,
                $"v{latestNorm} is available — you're on v{currentNorm}.");
        }
        if (lat == cur)
        {
            return new(UpdateStatus.UpToDate, latestNorm, releaseUrl,
                $"You're on the latest release (v{latestNorm}).");
        }
        return new(UpdateStatus.AheadOfReleased, latestNorm, releaseUrl,
            $"This is a dev build (v{currentNorm}) ahead of the latest release (v{latestNorm}).");
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Minimal shape of GitHub's <c>/releases/latest</c>
    /// payload. The endpoint returns many more fields — we model
    /// only what the UI needs and let System.Text.Json ignore the rest.</summary>
    private sealed record GithubRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("html_url")] string? HtmlUrl);
}
