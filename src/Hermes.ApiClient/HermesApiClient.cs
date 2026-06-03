using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Hermes.ApiClient.Models;

namespace Hermes.ApiClient;

/// <summary>
/// Thin typed wrapper over the Hermes gateway REST API. Owns its own HttpClient,
/// applies the bearer key from <see cref="HermesConfig"/>, and exposes the
/// endpoints the tray currently needs. All methods are cancellable so the
/// polling loop can abort cleanly on shutdown.
/// </summary>
public sealed class HermesApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        // Omit null fields from outgoing JSON bodies. Matters for
        // CreateSessionRequest.Model in particular — passing the field as
        // an explicit JSON null could be interpreted by the gateway as
        // "force model = null" rather than "no preference, use the server
        // default". The matching test in CreateSessionRequestTests already
        // assumes this behaviour; this keeps prod aligned with that.
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public HermesConfig Config { get; }

    public HermesApiClient(HermesConfig config)
    {
        Config = config;
        _http = new HttpClient
        {
            BaseAddress = config.BaseAddress,
            // The gateway is local; if it's wedged we want to surface that fast,
            // not block the tray polling loop for 100s.
            Timeout = TimeSpan.FromSeconds(6),
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        }
    }

    public Task<DetailedHealth?> GetDetailedHealthAsync(CancellationToken ct = default) =>
        GetJsonAsync<DetailedHealth>("/health/detailed", ct);

    public Task<ModelList?> GetModelsAsync(CancellationToken ct = default) =>
        GetJsonAsync<ModelList>("/v1/models", ct);

    public Task<Capabilities?> GetCapabilitiesAsync(CancellationToken ct = default) =>
        GetJsonAsync<Capabilities>("/v1/capabilities", ct);

    /// <summary>
    /// Lists recent sessions across every source (cli, api, cron, message platforms).
    /// Crucially this sees sessions running in <i>external</i> processes (e.g. a `hermes`
    /// CLI session you started in PowerShell) — `/health/detailed.active_agents` only
    /// counts agents inside the gateway process itself.
    /// </summary>
    public Task<SessionList?> GetSessionsAsync(int limit = 25, bool includeChildren = true, CancellationToken ct = default) =>
        GetJsonAsync<SessionList>($"/api/sessions?limit={limit}&include_children={(includeChildren ? "true" : "false")}", ct);

    public Task<SessionDetailEnvelope?> GetSessionAsync(string id, CancellationToken ct = default) =>
        GetJsonAsync<SessionDetailEnvelope>($"/api/sessions/{Uri.EscapeDataString(id)}", ct);

    public Task<SessionMessageList?> GetSessionMessagesAsync(string id, CancellationToken ct = default) =>
        GetJsonAsync<SessionMessageList>($"/api/sessions/{Uri.EscapeDataString(id)}/messages", ct);

    /// <summary>
    /// Full-text search across stored session messages. The gateway runs
    /// an FTS5 query with auto-added prefix wildcards (so "scroll" matches
    /// "scrollbar") and dedupes hits by compression lineage so one logical
    /// chat doesn't appear as N rows. Empty / whitespace queries short-
    /// circuit to an empty result envelope without hitting the server —
    /// the gateway behaves the same way, but we save the round-trip.
    /// </summary>
    /// <param name="query">User-typed search string. Will be URL-encoded.</param>
    /// <param name="limit">Maximum number of distinct conversations to
    /// return. Clamped to [1, 200] so a runaway caller can't ask the
    /// gateway for the whole corpus.</param>
    public Task<SessionSearchResponse?> SearchSessionsAsync(string query, int limit = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult<SessionSearchResponse?>(new SessionSearchResponse([]));
        }
        return GetJsonAsync<SessionSearchResponse>(BuildSearchPath(query, limit), ct);
    }

    /// <summary>
    /// Builds the <c>/api/sessions/search</c> path with the user query
    /// URL-encoded and the limit clamped to a sensible range. Extracted
    /// (and made <c>internal</c>) so URL-building can be unit-tested
    /// without standing up an HttpClient with a routable BaseAddress.
    /// </summary>
    internal static string BuildSearchPath(string query, int limit)
    {
        var clampedLimit = Math.Clamp(limit, 1, 200);
        var escapedQuery = Uri.EscapeDataString(query.Trim());
        return $"/api/sessions/search?q={escapedQuery}&limit={clampedLimit}";
    }

    public Task<SkillList?> GetSkillsAsync(CancellationToken ct = default) =>
        GetJsonAsync<SkillList>("/v1/skills", ct);

    public Task<JobList?> GetJobsAsync(CancellationToken ct = default) =>
        GetJsonAsync<JobList>("/api/jobs", ct);

    /// <summary>
    /// Single-job fetch via <c>GET /api/jobs/{id}</c>. The server wraps the
    /// payload in the same <c>{"job": {...}}</c> envelope that the mutation
    /// endpoints use, so we route through <see cref="SendForJobAsync"/> to
    /// share the unwrap + error-formatting code path. Used by the detail-pane
    /// "Refresh now" button on JobsPage; the polling loop still uses the
    /// list endpoint to detect transitions across all jobs in one round-trip.
    /// </summary>
    public Task<Job?> GetJobAsync(string id, CancellationToken ct = default)
    {
        var msg = new HttpRequestMessage(HttpMethod.Get, $"/api/jobs/{Uri.EscapeDataString(id)}");
        return SendForJobAsync(msg, ct);
    }

    public async Task<Job?> CreateJobAsync(CreateJobRequest req, CancellationToken ct = default)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/api/jobs")
        {
            Content = JsonContent.Create(req, options: JsonOpts),
        };
        return await SendForJobAsync(msg, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Partial update against <c>PATCH /api/jobs/{id}</c>. The server
    /// rejected PUT with 405 on probe; PATCH is the only update verb.
    /// Returns the (now-updated) job — including server-recomputed
    /// fields like <c>next_run_at</c> when the schedule changes — so
    /// callers can swap the row in place without an extra refresh.
    /// </summary>
    public async Task<Job?> UpdateJobAsync(string id, UpdateJobRequest req, CancellationToken ct = default)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Patch, $"/api/jobs/{Uri.EscapeDataString(id)}")
        {
            Content = JsonContent.Create(req, options: JsonOpts),
        };
        return await SendForJobAsync(msg, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Triggers an immediate run of <paramref name="id"/>. The server
    /// enqueues the run and returns the (now updated) job; the actual
    /// agent output is delivered asynchronously to the job's
    /// <c>deliver</c> channel, not in this response.
    /// </summary>
    public Task<Job?> RunJobAsync(string id, CancellationToken ct = default) =>
        PostForJobAsync($"/api/jobs/{Uri.EscapeDataString(id)}/run", ct);

    public Task<Job?> PauseJobAsync(string id, CancellationToken ct = default) =>
        PostForJobAsync($"/api/jobs/{Uri.EscapeDataString(id)}/pause", ct);

    public Task<Job?> ResumeJobAsync(string id, CancellationToken ct = default) =>
        PostForJobAsync($"/api/jobs/{Uri.EscapeDataString(id)}/resume", ct);

    /// <summary>
    /// Deletes the job. Server returns <c>{"ok": true}</c>; we just check
    /// for a 2xx and surface anything else as an exception so callers don't
    /// have to disambiguate.
    /// </summary>
    public async Task DeleteJobAsync(string id, CancellationToken ct = default)
    {
        using var resp = await _http.DeleteAsync($"/api/jobs/{Uri.EscapeDataString(id)}", ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(FormatErrorBody((int)resp.StatusCode, resp.ReasonPhrase, body));
        }
    }

    private Task<Job?> PostForJobAsync(string path, CancellationToken ct)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, path);
        return SendForJobAsync(msg, ct);
    }

    private async Task<Job?> SendForJobAsync(HttpRequestMessage msg, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.SendAsync(msg, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new HttpRequestException(FormatErrorBody((int)resp.StatusCode, resp.ReasonPhrase, body));
            }
            var env = await resp.Content.ReadFromJsonAsync<JobEnvelope>(JsonOpts, ct).ConfigureAwait(false);
            return env?.Job;
        }
        finally
        {
            msg.Dispose();
        }
    }

    /// <summary>
    /// Turns a non-2xx body into a human-readable error string. Hermes
    /// endpoints return failures as <c>{"error": "..."}</c> — when we
    /// can pull that out, we surface just the decoded message (so a
    /// JSON-escaped <c>\u2264</c> renders as the actual <c>≤</c>
    /// character on the InfoBar). For anything else we fall back to
    /// the raw body prefixed with the status so debugging is still
    /// possible.
    /// </summary>
    internal static string FormatErrorBody(int statusCode, string? reasonPhrase, string body)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("error", out var errElem) &&
                    errElem.ValueKind == JsonValueKind.String)
                {
                    var errStr = errElem.GetString();
                    if (!string.IsNullOrWhiteSpace(errStr)) return errStr!;
                }
            }
            catch (JsonException)
            {
                // Body wasn't JSON — fall through to the raw-body branch.
            }
        }
        return $"HTTP {statusCode} {reasonPhrase}: {body}";
    }

    /// <summary>
    /// Creates a fresh session that chat turns can be attached to. Hermes
    /// auto-fills source / model from server config when both are omitted;
    /// pass <paramref name="model"/> to override the global default for
    /// this one session. The gateway will auto-generate a title from the
    /// first message so we don't need to invent one (which used to collide —
    /// titles are unique).
    /// </summary>
    public async Task<SessionDetail?> CreateSessionAsync(string? title, string? model, CancellationToken ct = default)
    {
        // Drop a hard-coded title — duplicate titles return 400 invalid_title,
        // and the server will name the session itself based on the first message.
        // Model is opt-in: null means "let the server pick its current default",
        // which preserves server-side default behavior if the gateway config
        // changes between when we read it at app start and when we actually
        // create a session.
        var req = new CreateSessionRequest(
            Title: string.IsNullOrWhiteSpace(title) ? null : title,
            Source: null,
            Model: string.IsNullOrWhiteSpace(model) ? null : model);

        using var msg = new HttpRequestMessage(HttpMethod.Post, "/api/sessions")
        {
            Content = JsonContent.Create(req, options: JsonOpts),
        };
        using var resp = await _http.SendAsync(msg, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"{(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");
        }
        var env = await resp.Content.ReadFromJsonAsync<CreateSessionResponse>(JsonOpts, ct).ConfigureAwait(false);
        return env?.Session;
    }

    private async Task<T?> GetJsonAsync<T>(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOpts, ct).ConfigureAwait(false);
    }

    public void Dispose() => _http.Dispose();
}
