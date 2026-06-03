using Hermes.ApiClient.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Hermes.App.ViewModels;

/// <summary>
/// Row view-model for a single job in the jobs list. The server publishes
/// state via <c>state</c> + <c>paused_at</c> + <c>enabled</c> rather than a
/// single boolean, so the status pill maps from <c>state</c> primarily and
/// falls back to <c>enabled</c> for the disabled case.
/// </summary>
public sealed class JobRowVm
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PromptPreview { get; set; } = "";
    public string ScheduleLabel { get; set; } = "";
    public string NextRunLabel { get; set; } = "";
    public string NextRunTooltip { get; set; } = "";
    public string LastRunLabel { get; set; } = "";
    public string StatusLabel { get; set; } = "";
    public string? LastErrorLabel { get; set; }
    public bool HasLastError => !string.IsNullOrEmpty(LastErrorLabel);
    public bool IsPaused { get; set; }
    public bool IsRunning { get; set; }
    public Brush StatusBackground { get; set; } = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
    public Brush StatusForeground { get; set; } = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    /// <summary>
    /// The full server-side <see cref="Job"/> this row was built from. Held
    /// so the edit pane can prefill its form from the original values
    /// without needing a second round-trip. Not bound directly by any view.
    /// </summary>
    public Job? Source { get; set; }

    /// <summary>
    /// Source of truth for action-button availability. The pause/resume
    /// command flips its label off this, the delete + run-now buttons are
    /// always shown for any job.
    /// </summary>
    public string PauseResumeLabel => IsPaused ? "Resume" : "Pause";

    public static JobRowVm FromJob(Job j)
    {
        // Status derivation. Precedence:
        //   1. enabled == false  → "disabled" (overrides state — server can
        //      report an old state on a disabled job)
        //   2. state == "running" → live indicator
        //   3. paused_at present  → "paused"
        //   4. otherwise          → state verbatim (typically "scheduled")
        var isPaused = !string.IsNullOrEmpty(j.PausedAt) || string.Equals(j.State, "paused", StringComparison.OrdinalIgnoreCase);
        var isRunning = string.Equals(j.State, "running", StringComparison.OrdinalIgnoreCase);
        var (label, bgKey, fgKey) = (j.Enabled, isRunning, isPaused, j.State) switch
        {
            (false, _, _, _)       => ("disabled", "SubtleFillColorSecondaryBrush",          "TextFillColorSecondaryBrush"),
            (_, true, _, _)        => ("running",  "SystemFillColorAttentionBackgroundBrush","SystemFillColorAttentionBrush"),
            (_, _, true, _)        => ("paused",   "SystemFillColorCautionBackgroundBrush",  "SystemFillColorCautionBrush"),
            (_, _, _, var s) when !string.IsNullOrEmpty(s)
                                   => (s!,        "SystemFillColorSuccessBackgroundBrush",  "SystemFillColorSuccessBrush"),
            _                      => ("scheduled","SystemFillColorSuccessBackgroundBrush",  "SystemFillColorSuccessBrush"),
        };

        // Surface the last delivery error if there's no last_error — both
        // are meaningful failures, the user just cares which one fired.
        var errorPreview = j.LastError ?? j.LastDeliveryError;
        if (!string.IsNullOrEmpty(errorPreview) && errorPreview.Length > 160)
            errorPreview = errorPreview[..160] + "…";

        return new JobRowVm
        {
            Id = j.Id,
            DisplayName = !string.IsNullOrWhiteSpace(j.Name) ? j.Name! : j.Id,
            PromptPreview = string.IsNullOrEmpty(j.Prompt) ? "(no prompt)"
                            : (j.Prompt!.Length > 160 ? j.Prompt![..160] + "…" : j.Prompt!),
            ScheduleLabel = j.ScheduleDisplay ?? j.Schedule?.Display ?? j.Schedule?.Expr ?? "—",
            NextRunLabel = !string.IsNullOrEmpty(j.NextRunAt) ? $"next {Converters.IsoToRelative(j.NextRunAt)}" : "no next run",
            NextRunTooltip = !string.IsNullOrEmpty(j.NextRunAt) ? Converters.IsoToLocalTime(j.NextRunAt) : "",
            LastRunLabel = !string.IsNullOrEmpty(j.LastRunAt) ? $"last {Converters.IsoToRelative(j.LastRunAt)}" : "never run",
            StatusLabel = label,
            LastErrorLabel = errorPreview,
            IsPaused = isPaused,
            IsRunning = isRunning,
            StatusBackground = (Brush)Application.Current.Resources[bgKey],
            StatusForeground = (Brush)Application.Current.Resources[fgKey],
            Source = j,
        };
    }
}
