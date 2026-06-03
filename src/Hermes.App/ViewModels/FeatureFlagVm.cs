using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Hermes.App.ViewModels;

public sealed class FeatureFlagVm
{
    public string Name { get; set; } = "";
    public bool IsEnabled { get; set; }
    public string Glyph { get; set; } = "\uE73E"; // checkmark
    public Brush Foreground { get; set; } = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    public static FeatureFlagVm From(string key, JsonElement value)
    {
        var enabled = IsEffectivelyEnabled(value);
        return new()
        {
            Name = key.Replace('_', ' '),
            IsEnabled = enabled,
            Glyph = enabled ? "\uE73E" : "\uE711", // CheckMark : Cancel
            Foreground = (Brush)Application.Current.Resources[
                enabled ? "SystemFillColorSuccessBrush" : "TextFillColorTertiaryBrush"],
        };
    }

    /// <summary>
    /// Maps the JSON value the gateway returns for a feature to a
    /// shown / not-shown state. We treat <c>true</c> and any non-trivial
    /// string / number / object / array as "enabled" — newer informational
    /// features such as <c>session_continuity_header</c> return a header
    /// name string and should show as enabled even though they're not a
    /// boolean toggle.
    /// </summary>
    private static bool IsEffectivelyEnabled(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => false,
        JsonValueKind.Undefined => false,
        JsonValueKind.String => !IsDisabledStringLiteral(value.GetString()),
        _ => true, // numbers, objects, arrays — presence = enabled
    };

    private static bool IsDisabledStringLiteral(string? s) =>
        string.IsNullOrEmpty(s)
        || s.Equals("false", StringComparison.OrdinalIgnoreCase)
        || s.Equals("off", StringComparison.OrdinalIgnoreCase)
        || s.Equals("disabled", StringComparison.OrdinalIgnoreCase)
        || s.Equals("none", StringComparison.OrdinalIgnoreCase);
}
