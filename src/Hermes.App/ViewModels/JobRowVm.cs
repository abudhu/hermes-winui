using System;
using Hermes.ApiClient.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Hermes.App.ViewModels;

public sealed class JobRowVm
{
    public string Id { get; set; } = "";
    public string PromptPreview { get; set; } = "";
    public string ScheduleLabel { get; set; } = "";
    public string NextRunLabel { get; set; } = "";
    public string StatusLabel { get; set; } = "";
    public Brush StatusBackground { get; set; } = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
    public Brush StatusForeground { get; set; } = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    public static JobRowVm FromJob(Job j)
    {
        var (label, bg, fg) = (j.Enabled, j.Paused) switch
        {
            (false, _) =>     ("disabled", "SubtleFillColorSecondaryBrush",          "TextFillColorSecondaryBrush"),
            (_, true) =>      ("paused",   "SystemFillColorCautionBackgroundBrush",  "SystemFillColorCautionBrush"),
            _ =>              ("enabled",  "SystemFillColorSuccessBackgroundBrush",  "SystemFillColorSuccessBrush"),
        };

        return new JobRowVm
        {
            Id = j.Id,
            PromptPreview = string.IsNullOrEmpty(j.Prompt) ? "(no prompt)"
                            : (j.Prompt!.Length > 160 ? j.Prompt![..160] + "…" : j.Prompt!),
            ScheduleLabel = j.Schedule ?? "—",
            NextRunLabel = j.NextRunAt is double n ? $"next: {Converters.EpochSecondsToRelative(n)}" : "no next run",
            StatusLabel = label,
            StatusBackground = (Brush)Application.Current.Resources[bg],
            StatusForeground = (Brush)Application.Current.Resources[fg],
        };
    }
}
