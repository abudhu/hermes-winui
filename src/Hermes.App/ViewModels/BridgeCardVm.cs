using System;
using Hermes.ApiClient.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Hermes.App.ViewModels;

public sealed class BridgeCardVm
{
    public string Name { get; set; } = "";
    public string State { get; set; } = "";
    public string? ErrorMessage { get; set; }
    public string UpdatedLabel { get; set; } = "";
    public Brush StateBrush { get; set; } = new SolidColorBrush(Colors.Gray);
    public Brush StateBackground { get; set; } = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
    public Brush StateForeground { get; set; } = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    public static BridgeCardVm From(string name, PlatformStatus status)
    {
        var state = (status.State ?? "unknown").ToLowerInvariant();
        var (dotColor, bgKey, fgKey) = state switch
        {
            "connected" =>     (Color.FromArgb(0xFF, 0x10, 0x88, 0x3E), "SystemFillColorSuccessBackgroundBrush", "SystemFillColorSuccessBrush"),
            "connecting" =>    (Color.FromArgb(0xFF, 0xF7, 0x63, 0x0C), "SystemFillColorCautionBackgroundBrush", "SystemFillColorCautionBrush"),
            "disabled" =>      (Color.FromArgb(0xFF, 0x80, 0x80, 0x80), "SubtleFillColorSecondaryBrush",         "TextFillColorSecondaryBrush"),
            "error" or "disconnected" or "failed" =>
                               (Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C), "SystemFillColorCriticalBackgroundBrush","SystemFillColorCriticalBrush"),
            _ =>               (Color.FromArgb(0xFF, 0x80, 0x80, 0x80), "SubtleFillColorSecondaryBrush",         "TextFillColorSecondaryBrush"),
        };

        return new BridgeCardVm
        {
            Name = name,
            State = state,
            ErrorMessage = !string.IsNullOrEmpty(status.ErrorMessage) ? $"{status.ErrorCode}: {status.ErrorMessage}" : null,
            UpdatedLabel = string.IsNullOrEmpty(status.UpdatedAt) ? "" : $"updated {status.UpdatedAt}",
            StateBrush = new SolidColorBrush(dotColor),
            StateBackground = (Brush)Application.Current.Resources[bgKey],
            StateForeground = (Brush)Application.Current.Resources[fgKey],
        };
    }
}
