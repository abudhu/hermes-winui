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

    public Task<SkillList?> GetSkillsAsync(CancellationToken ct = default) =>
        GetJsonAsync<SkillList>("/v1/skills", ct);

    public Task<JobList?> GetJobsAsync(CancellationToken ct = default) =>
        GetJsonAsync<JobList>("/api/jobs", ct);

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
