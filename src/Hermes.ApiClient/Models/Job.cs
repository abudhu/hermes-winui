using System.Text.Json.Serialization;

namespace Hermes.ApiClient.Models;

/// <summary>
/// Wraps the <c>GET /api/jobs</c> response. The gateway returns
/// <c>{"jobs": [...]}</c> rather than the OpenAI-style <c>{object, data}</c>
/// envelope.
/// </summary>
public sealed record JobList(
    [property: JsonPropertyName("jobs")] List<Job> Jobs
);

/// <summary>
/// Envelope for single-job responses (<c>POST /api/jobs</c>,
/// <c>GET /api/jobs/{id}</c>, mutation endpoints). The server consistently
/// wraps individual jobs in <c>{"job": {...}}</c>.
/// </summary>
public sealed record JobEnvelope(
    [property: JsonPropertyName("job")] Job? Job
);

/// <summary>
/// A single scheduled job as returned by the gateway. Field set mirrors the
/// real server response observed against a live gateway — most fields are
/// nullable because the server only populates them once the job has actually
/// run (or once the user has set the relevant config).
/// </summary>
public sealed record Job(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("prompt")] string? Prompt,
    [property: JsonPropertyName("schedule")] JobSchedule? Schedule,
    // The server pre-renders the schedule into a human-readable string in
    // `schedule_display`. We prefer this over re-formatting the structured
    // form on the client — the server knows about interval normalisation
    // and one-shot ISO timestamps that we'd otherwise reimplement here.
    [property: JsonPropertyName("schedule_display")] string? ScheduleDisplay,
    [property: JsonPropertyName("enabled")] bool? Enabled,
    [property: JsonPropertyName("state")] string? State,
    // ISO 8601 strings — not epoch seconds. The previous model used `double?`
    // and silently dropped these fields, which is why the "next run" line
    // always rendered as "no next run" before this fix.
    [property: JsonPropertyName("created_at")] string? CreatedAt,
    [property: JsonPropertyName("next_run_at")] string? NextRunAt,
    [property: JsonPropertyName("last_run_at")] string? LastRunAt,
    [property: JsonPropertyName("paused_at")] string? PausedAt,
    [property: JsonPropertyName("paused_reason")] string? PausedReason,
    [property: JsonPropertyName("last_status")] string? LastStatus,
    [property: JsonPropertyName("last_error")] string? LastError,
    [property: JsonPropertyName("last_delivery_error")] string? LastDeliveryError,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("provider")] string? Provider,
    [property: JsonPropertyName("deliver")] string? Deliver,
    [property: JsonPropertyName("workdir")] string? Workdir,
    [property: JsonPropertyName("skills")] List<string>? Skills,
    [property: JsonPropertyName("enabled_toolsets")] List<string>? EnabledToolsets,
    [property: JsonPropertyName("no_agent")] bool? NoAgent,
    [property: JsonPropertyName("script")] string? Script,
    [property: JsonPropertyName("repeat")] JobRepeat? Repeat
);

/// <summary>
/// Structured form of a job's schedule. <see cref="Kind"/> distinguishes
/// "cron" / "interval" / "once" so the UI can render different affordances
/// if it wants to; for v1 we only show <see cref="Display"/>.
/// </summary>
public sealed record JobSchedule(
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("expr")] string? Expr,
    [property: JsonPropertyName("display")] string? Display
);

/// <summary>
/// Optional repeat cap. <see cref="Times"/> null means "infinite";
/// <see cref="Completed"/> tracks how many fires have happened.
/// </summary>
public sealed record JobRepeat(
    [property: JsonPropertyName("times")] int? Times,
    [property: JsonPropertyName("completed")] int? Completed
);

/// <summary>
/// Body for <c>POST /api/jobs</c>. The shared <c>JsonOpts</c> on
/// <see cref="HermesApiClient"/> omits null properties when serialising, so
/// optional fields left null are simply not sent — the server applies its
/// own defaults rather than seeing an explicit null.
///
/// <para>
/// The server accepts <see cref="Schedule"/> as a plain string in one of
/// three flavours: a cron expression (<c>"0 9 * * 1-5"</c>), an interval
/// (<c>"30m"</c>, <c>"every 2h"</c>), or a one-shot ISO timestamp. The
/// response inflates this into the structured <see cref="JobSchedule"/>.
/// </para>
/// </summary>
public sealed record CreateJobRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("schedule")] string Schedule,
    [property: JsonPropertyName("prompt")] string? Prompt = null,
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("deliver")] string? Deliver = null,
    [property: JsonPropertyName("enabled")] bool? Enabled = null,
    [property: JsonPropertyName("skills")] List<string>? Skills = null,
    [property: JsonPropertyName("enabled_toolsets")] List<string>? EnabledToolsets = null,
    [property: JsonPropertyName("workdir")] string? Workdir = null,
    [property: JsonPropertyName("script")] string? Script = null,
    [property: JsonPropertyName("no_agent")] bool? NoAgent = null
);

/// <summary>
/// Body for <c>PATCH /api/jobs/{id}</c>. The gateway treats this as a
/// partial update — fields left null (and therefore omitted by the
/// shared serializer) are not changed on the server. Send only what
/// the user actually edited.
///
/// <para>
/// Note this is shape-identical to <see cref="CreateJobRequest"/>
/// minus the required-name/schedule fields. It's kept as a separate
/// record so the type system communicates "everything is optional"
/// vs "name + schedule are required" at the call site.
/// </para>
/// </summary>
public sealed record UpdateJobRequest(
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("schedule")] string? Schedule = null,
    [property: JsonPropertyName("prompt")] string? Prompt = null,
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("deliver")] string? Deliver = null,
    [property: JsonPropertyName("enabled")] bool? Enabled = null
);
