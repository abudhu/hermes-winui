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
}
