using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Hermes.App.ViewModels;

public sealed class FeatureFlagVm
{
    public string Name { get; set; } = "";
    public bool IsEnabled { get; set; }
    public string Glyph { get; set; } = "\uE73E"; // checkmark
    public Brush Foreground { get; set; } = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    public static FeatureFlagVm From(string key, bool enabled) => new()
    {
        Name = key.Replace('_', ' '),
        IsEnabled = enabled,
        Glyph = enabled ? "\uE73E" : "\uE711", // CheckMark : Cancel
        Foreground = (Brush)Application.Current.Resources[
            enabled ? "SystemFillColorSuccessBrush" : "TextFillColorTertiaryBrush"],
    };
}
