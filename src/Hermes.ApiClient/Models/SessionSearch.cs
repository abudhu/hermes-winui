using System.Text.Json.Serialization;

namespace Hermes.ApiClient.Models;

/// <summary>
/// Envelope returned by <c>GET /api/sessions/search?q=…</c>. The gateway
/// runs an FTS5 query over the message-content index, dedupes by
/// compression lineage, and returns at most <c>limit</c> hits ranked by
/// FTS5 relevance.
/// </summary>
public sealed record SessionSearchResponse(
    [property: JsonPropertyName("results")] List<SessionSearchResult> Results
);

/// <summary>
/// One match from <c>GET /api/sessions/search</c>. Note that we get
/// <i>only</i> the matched message's metadata (snippet, role) plus
/// minimal session attribution (source, model, started-at) — there's no
/// title, no message count, no last-active. Callers wanting the full
/// session row should look up <see cref="SessionId"/> in their existing
/// sessions cache and fall back to a stub row when it's not there.
/// </summary>
/// <param name="SessionId">Live compression-tip session id. Always safe to
/// open / resume by this id — the gateway has already resolved through
/// any compression lineage.</param>
/// <param name="LineageRoot">Original session id at the start of the
/// compression chain. Stable across compressions; useful as a durable
/// key if we ever want to pin or de-dup at the lineage level on the
/// client.</param>
/// <param name="Snippet">FTS5 snippet around the matched term, with the
/// gateway's chosen markers (<c>&gt;&gt;&gt;…&lt;&lt;&lt;</c>) and
/// truncation indicator (<c>...</c>). Strip markers in the view layer.</param>
public sealed record SessionSearchResult(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("lineage_root")] string? LineageRoot,
    [property: JsonPropertyName("snippet")] string Snippet,
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("session_started")] double? SessionStarted
);
