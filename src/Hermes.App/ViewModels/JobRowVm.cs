using CommunityToolkit.Mvvm.ComponentModel;
using Hermes.ApiClient.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Hermes.App.ViewModels;

/// <summary>
/// Row view-model for a single job in the jobs list. The server publishes
/// state via <c>state</c> + <c>paused_at</c> + <c>enabled</c> rather than a
/// single boolean, so the status pill maps from <c>state</c> primarily and
/// falls back to <c>enabled</c> for the disabled case.
///
/// <para>Derives from <see cref="ObservableObject"/> so the per-row
/// <see cref="IsSelected"/> flag — which drives the selected-card highlight
/// in the master pane — can change after the row is created without a
/// full list rebuild.</para>
/// </summary>
public sealed partial class JobRowVm : ObservableObject
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PromptPreview { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string ScheduleLabel { get; set; } = "";
    public string NextRunLabel { get; set; } = "";
    public string NextRunTooltip { get; set; } = "";
    public string LastRunLabel { get; set; } = "";
    public string LastRunTooltip { get; set; } = "";
    public string StatusLabel { get; set; } = "";
    public string DeliverLabel { get; set; } = "";
    public string? ModelLabel { get; set; }
    public string? LastErrorLabel { get; set; }
    public string? LastDeliveryErrorLabel { get; set; }
    public bool HasLastError => !string.IsNullOrEmpty(LastErrorLabel);
    public bool HasLastDeliveryError => !string.IsNullOrEmpty(LastDeliveryErrorLabel);
    public bool IsPaused { get; set; }
    public bool IsRunning { get; set; }
    public bool IsEnabled { get; set; }
    public Brush StatusBackground { get; set; } = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
    public Brush StatusForeground { get; set; } = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    /// <summary>
    /// Drives the selected-card highlight in the left master pane. Set by
    /// the page when the user clicks the row (or when a toast deep-link
    /// re-selects after a refresh).
    /// </summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

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

    /// <summary>
    /// Compact "transition snapshot" used by the polling loop to decide
    /// whether a job just finished. Tuple chosen because state alone isn't
    /// reliable — a job can return to <c>scheduled</c> after firing, and the
    /// completion is only legible from <c>last_status</c> + <c>last_run_at</c>
    /// having advanced.
    /// </summary>
    public (string? State, string? LastStatus, string? LastRunAt) Snapshot() =>
        (Source?.State, Source?.LastStatus, Source?.LastRunAt);

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

        // Truncate the inline-error previews independently — last_error and
        // last_delivery_error mean different things (the run failed vs the
        // delivery channel failed) and the detail pane shows them in
        // separate InfoBars.
        var lastError = TruncateForPreview(j.LastError);
        var lastDeliveryError = TruncateForPreview(j.LastDeliveryError);

        return new JobRowVm
        {
            Id = j.Id,
            DisplayName = !string.IsNullOrWhiteSpace(j.Name) ? j.Name! : j.Id,
            Prompt = j.Prompt ?? "",
            PromptPreview = string.IsNullOrEmpty(j.Prompt) ? "(no prompt)"
                            : (j.Prompt!.Length > 160 ? j.Prompt![..160] + "…" : j.Prompt!),
            ScheduleLabel = j.ScheduleDisplay ?? j.Schedule?.Display ?? j.Schedule?.Expr ?? "—",
            NextRunLabel = !string.IsNullOrEmpty(j.NextRunAt) ? $"next {Converters.IsoToRelative(j.NextRunAt)}" : "no next run",
            NextRunTooltip = !string.IsNullOrEmpty(j.NextRunAt) ? Converters.IsoToLocalTime(j.NextRunAt) : "",
            LastRunLabel = BuildLastRunLabel(j),
            LastRunTooltip = !string.IsNullOrEmpty(j.LastRunAt) ? Converters.IsoToLocalTime(j.LastRunAt) : "",
            StatusLabel = label,
            DeliverLabel = string.IsNullOrEmpty(j.Deliver) ? "—" : j.Deliver!,
            ModelLabel = string.IsNullOrEmpty(j.Model) ? null : j.Model,
            LastErrorLabel = lastError,
            LastDeliveryErrorLabel = lastDeliveryError,
            IsPaused = isPaused,
            IsRunning = isRunning,
            IsEnabled = j.Enabled ?? true,
            StatusBackground = (Brush)Application.Current.Resources[bgKey],
            StatusForeground = (Brush)Application.Current.Resources[fgKey],
            Source = j,
        };
    }

    /// <summary>
    /// "last status — relative time" composite for the detail pane.
    /// Falls back to "never run" when there's no last_run_at; that case
    /// still shows on the pane so the user knows the row exists.
    /// </summary>
    private static string BuildLastRunLabel(Job j)
    {
        if (string.IsNullOrEmpty(j.LastRunAt)) return "never run";
        var when = Converters.IsoToRelative(j.LastRunAt);
        return string.IsNullOrEmpty(j.LastStatus) ? $"last run {when}" : $"{j.LastStatus} — {when}";
    }

    private static string? TruncateForPreview(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return value.Length > 600 ? value[..600] + "…" : value;
    }
}
