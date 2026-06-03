using System.Text.Json;
using Hermes.ApiClient.Models;

namespace Hermes.ApiClient.Tests;

/// <summary>
/// Round-trip tests against the real Hermes jobs API shape, captured live
/// from a running gateway. These guard against regressions in the JSON
/// contract — particularly the timestamp-typing mistake that lived in
/// the previous <see cref="Job"/> model (epoch-seconds doubles instead
/// of ISO 8601 strings), which silently dropped <c>next_run_at</c> and
/// <c>last_run_at</c> from the rendered list.
/// </summary>
public class JobModelTests
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Lifted verbatim from a live <c>GET /api/jobs</c> response. If the
    /// gateway changes any of these field names or types, this test fires
    /// before the UI silently starts rendering "—" everywhere.
    /// </summary>
    private const string LiveJobsResponseJson = """
    {
      "jobs": [
        {
          "id": "0c3bae422851",
          "name": "probe-job",
          "prompt": "test",
          "skills": [],
          "skill": null,
          "model": null,
          "provider": null,
          "base_url": null,
          "script": null,
          "no_agent": false,
          "context_from": null,
          "schedule": {
            "kind": "cron",
            "expr": "0 9 * * *",
            "display": "0 9 * * *"
          },
          "schedule_display": "0 9 * * *",
          "repeat": {
            "times": null,
            "completed": 0
          },
          "enabled": true,
          "state": "scheduled",
          "paused_at": null,
          "paused_reason": null,
          "created_at": "2026-06-03T12:54:38.638713-07:00",
          "next_run_at": "2026-06-04T09:00:00-07:00",
          "last_run_at": null,
          "last_status": null,
          "last_error": null,
          "last_delivery_error": null,
          "deliver": "local",
          "origin": {
            "platform": "api_server",
            "chat_id": "api"
          },
          "enabled_toolsets": null,
          "workdir": null,
          "profile": null
        }
      ]
    }
    """;

    [Fact]
    public void Deserialize_LiveResponse_PopulatesAllRenderedFields()
    {
        var list = JsonSerializer.Deserialize<JobList>(LiveJobsResponseJson, Opts);
        Assert.NotNull(list);
        Assert.Single(list!.Jobs);
        var j = list.Jobs[0];

        // Identity fields the row renders.
        Assert.Equal("0c3bae422851", j.Id);
        Assert.Equal("probe-job", j.Name);
        Assert.Equal("test", j.Prompt);

        // Schedule — both flat and nested forms are populated.
        Assert.Equal("0 9 * * *", j.ScheduleDisplay);
        Assert.NotNull(j.Schedule);
        Assert.Equal("cron", j.Schedule!.Kind);
        Assert.Equal("0 9 * * *", j.Schedule.Display);

        // Timestamps must round-trip as the original ISO strings — this was
        // the regression that triggered this whole change.
        Assert.Equal("2026-06-03T12:54:38.638713-07:00", j.CreatedAt);
        Assert.Equal("2026-06-04T09:00:00-07:00", j.NextRunAt);
        Assert.Null(j.LastRunAt);

        // State + enabled — both needed for the status pill.
        Assert.True(j.Enabled);
        Assert.Equal("scheduled", j.State);
        Assert.Null(j.PausedAt);

        // Delivery + arrays.
        Assert.Equal("local", j.Deliver);
        Assert.NotNull(j.Skills);
        Assert.Empty(j.Skills!);

        // Repeat nested object.
        Assert.NotNull(j.Repeat);
        Assert.Null(j.Repeat!.Times);
        Assert.Equal(0, j.Repeat.Completed);
    }

    [Fact]
    public void Deserialize_TimestampsParseAsDateTimeOffset()
    {
        // The UI runs the strings through DateTimeOffset.Parse for relative
        // formatting; verify directly here so a bad clock format on the
        // gateway side gets caught at the contract layer, not in the UI.
        var list = JsonSerializer.Deserialize<JobList>(LiveJobsResponseJson, Opts);
        var j = list!.Jobs[0];
        Assert.True(DateTimeOffset.TryParse(j.CreatedAt, out _), "created_at must parse");
        Assert.True(DateTimeOffset.TryParse(j.NextRunAt, out _), "next_run_at must parse");
    }

    [Fact]
    public void Serialize_CreateJobRequest_MinimalShape()
    {
        var req = new CreateJobRequest("morning brief", "0 9 * * 1-5", "Summarize my inbox.");
        var json = JsonSerializer.Serialize(req, Opts);
        Assert.Contains("\"name\":\"morning brief\"", json);
        Assert.Contains("\"schedule\":\"0 9 * * 1-5\"", json);
        Assert.Contains("\"prompt\":\"Summarize my inbox.\"", json);
        // Optional fields stay out unless explicitly set — the server reads
        // missing fields as "use default", which is what we want.
        Assert.DoesNotContain("\"model\"", json);
        Assert.DoesNotContain("\"deliver\"", json);
        Assert.DoesNotContain("\"enabled\"", json);
        Assert.DoesNotContain("\"skills\"", json);
    }

    [Fact]
    public void Serialize_CreateJobRequest_EmitsExplicitFields()
    {
        var req = new CreateJobRequest(
            Name: "j",
            Schedule: "30m",
            Prompt: "p",
            Model: "claude-opus-4",
            Deliver: "telegram",
            Enabled: false);
        var json = JsonSerializer.Serialize(req, Opts);
        Assert.Contains("\"model\":\"claude-opus-4\"", json);
        Assert.Contains("\"deliver\":\"telegram\"", json);
        Assert.Contains("\"enabled\":false", json);
    }

    [Fact]
    public void Deserialize_JobEnvelope_UnwrapsJobKey()
    {
        // POST /api/jobs and the mutation endpoints all wrap their response
        // in {"job": {...}}, in contrast to GET /api/jobs which uses "jobs".
        // The envelope record needs to be on the same drift-detection
        // surface as the list.
        const string envJson = """{"job":{"id":"abc123","name":"x","enabled":true}}""";
        var env = JsonSerializer.Deserialize<JobEnvelope>(envJson, Opts);
        Assert.NotNull(env?.Job);
        Assert.Equal("abc123", env!.Job!.Id);
        Assert.Equal("x", env.Job.Name);
        Assert.True(env.Job.Enabled);
    }

    [Fact]
    public void Serialize_UpdateJobRequest_OmitsNulls()
    {
        // PATCH semantics: only the fields actually populated should hit
        // the wire. Sending an explicit JSON null could be interpreted by
        // the gateway as "clear this field" rather than "no change".
        var req = new UpdateJobRequest(Name: "renamed");
        var json = JsonSerializer.Serialize(req, Opts);
        Assert.Contains("\"name\":\"renamed\"", json);
        Assert.DoesNotContain("\"schedule\"", json);
        Assert.DoesNotContain("\"prompt\"", json);
        Assert.DoesNotContain("\"model\"", json);
        Assert.DoesNotContain("\"deliver\"", json);
        Assert.DoesNotContain("\"enabled\"", json);
    }

    [Fact]
    public void Serialize_UpdateJobRequest_SendsExplicitEnabledFalse()
    {
        // Edit-mode save needs to be able to flip enabled off — the
        // explicit boolean has to survive null-omit serialization.
        var req = new UpdateJobRequest(Enabled: false);
        var json = JsonSerializer.Serialize(req, Opts);
        Assert.Contains("\"enabled\":false", json);
    }

    [Fact]
    public void Serialize_UpdateJobRequest_FullForm()
    {
        // Every editable field present at once — mirrors what the UI
        // sends from a Save click.
        var req = new UpdateJobRequest(
            Name: "n",
            Schedule: "30m",
            Prompt: "p",
            Model: "m",
            Deliver: "telegram",
            Enabled: true);
        var json = JsonSerializer.Serialize(req, Opts);
        Assert.Contains("\"name\":\"n\"", json);
        Assert.Contains("\"schedule\":\"30m\"", json);
        Assert.Contains("\"prompt\":\"p\"", json);
        Assert.Contains("\"model\":\"m\"", json);
        Assert.Contains("\"deliver\":\"telegram\"", json);
        Assert.Contains("\"enabled\":true", json);
    }

    /// <summary>
    /// Lifted verbatim from a live <c>GET /api/jobs/{id}</c> probe against a
    /// running gateway. The single-job endpoint returns the same per-job
    /// shape as the list, wrapped in <c>{"job":{...}}</c>. The detail pane
    /// + the explicit Refresh-now button on JobsPage round-trip through
    /// this shape — guard against the same kind of typing regression we
    /// hit with the list response.
    /// </summary>
    private const string LiveSingleJobJson = """
    {
      "job": {
        "id": "a86879a55d53",
        "name": "HoK Activities",
        "prompt": "Read the file at C:\\Users\\ambudhu\\cron\\hok.md and follow the instructions.",
        "skills": [],
        "skill": null,
        "model": null,
        "provider": null,
        "base_url": null,
        "script": null,
        "no_agent": false,
        "context_from": null,
        "schedule": {
          "kind": "cron",
          "expr": "0 15 * * 5",
          "display": "0 15 * * 5"
        },
        "schedule_display": "0 15 * * 5",
        "repeat": {
          "times": null,
          "completed": 0
        },
        "enabled": true,
        "state": "scheduled",
        "paused_at": null,
        "paused_reason": null,
        "created_at": "2026-06-03T13:59:24.201435-07:00",
        "next_run_at": "2026-06-05T15:00:00-07:00",
        "last_run_at": null,
        "last_status": null,
        "last_error": null,
        "last_delivery_error": null,
        "deliver": "local",
        "origin": {
          "platform": "api_server",
          "chat_id": "api"
        },
        "enabled_toolsets": null,
        "workdir": null,
        "profile": null
      }
    }
    """;

    [Fact]
    public void Deserialize_LiveSingleJobEnvelope_PopulatesAllRenderedFields()
    {
        var env = JsonSerializer.Deserialize<JobEnvelope>(LiveSingleJobJson, Opts);
        Assert.NotNull(env);
        Assert.NotNull(env!.Job);
        var j = env.Job!;

        // Identity + display fields the detail pane renders.
        Assert.Equal("a86879a55d53", j.Id);
        Assert.Equal("HoK Activities", j.Name);
        Assert.StartsWith("Read the file at", j.Prompt);

        // Schedule round-trips both flat and nested forms.
        Assert.Equal("0 15 * * 5", j.ScheduleDisplay);
        Assert.NotNull(j.Schedule);
        Assert.Equal("cron", j.Schedule!.Kind);
        Assert.Equal("0 15 * * 5", j.Schedule.Expr);

        // Timestamps stay as ISO strings — same field-typing trap as the
        // list endpoint, worth pinning at the contract layer.
        Assert.Equal("2026-06-03T13:59:24.201435-07:00", j.CreatedAt);
        Assert.Equal("2026-06-05T15:00:00-07:00", j.NextRunAt);
        Assert.Null(j.LastRunAt);

        // State + enabled — drive the status pill on the detail pane.
        Assert.True(j.Enabled);
        Assert.Equal("scheduled", j.State);
        Assert.Null(j.PausedAt);
        Assert.Null(j.LastStatus);
        Assert.Null(j.LastError);
        Assert.Null(j.LastDeliveryError);
    }

    [Fact]
    public void Deserialize_TerminalJobEnvelope_CarriesLastStatusAndError()
    {
        // Hypothetical "finished + failed" snapshot — the gateway hasn't
        // exposed a log endpoint, so the detail pane and the toast on
        // Running→Failed transitions are driven entirely off these
        // last_* fields. If the contract drifts (e.g. last_status flips
        // to a numeric code, or last_error becomes a structured object),
        // this test fires before the pipeline silently breaks.
        const string finishedJson = """
        {
          "job": {
            "id": "f1",
            "name": "nightly",
            "state": "scheduled",
            "enabled": true,
            "last_status": "failed",
            "last_run_at": "2026-06-03T20:30:00-07:00",
            "last_error": "Connection refused while delivering to telegram",
            "last_delivery_error": "telegram: 401 unauthorized"
          }
        }
        """;
        var env = JsonSerializer.Deserialize<JobEnvelope>(finishedJson, Opts);
        Assert.NotNull(env?.Job);
        var j = env!.Job!;
        Assert.Equal("failed", j.LastStatus);
        Assert.Equal("2026-06-03T20:30:00-07:00", j.LastRunAt);
        Assert.Equal("Connection refused while delivering to telegram", j.LastError);
        Assert.Equal("telegram: 401 unauthorized", j.LastDeliveryError);
    }
}
