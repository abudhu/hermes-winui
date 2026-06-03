using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Hermes.App;

/// <summary>
/// x:Bind function helpers used throughout the app for visibility / formatting
/// binding without standing up DependencyProperty-based IValueConverters.
/// All members are static and side-effect-free — safe to call from XAML.
/// </summary>
public static class Converters
{
    public static Visibility BoolToVisible(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility BoolToCollapsed(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility NotEmpty(string? value) =>
        string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility EmptyToVisible(string? value) =>
        string.IsNullOrEmpty(value) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Unix epoch seconds (double) → friendly local time string.</summary>
    public static string EpochSecondsToTime(double? epoch)
    {
        if (epoch is null) return "—";
        return DateTimeOffset.FromUnixTimeMilliseconds((long)(epoch.Value * 1000))
            .ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm");
    }

    public static string EpochSecondsToRelative(double? epoch)
    {
        if (epoch is null) return "—";
        var ts = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds((long)(epoch.Value * 1000));
        if (ts.TotalSeconds < 0) ts = TimeSpan.Zero;
        return ts.TotalSeconds < 60 ? $"{(int)ts.TotalSeconds}s ago"
             : ts.TotalMinutes < 60 ? $"{(int)ts.TotalMinutes}m ago"
             : ts.TotalHours < 24 ? $"{(int)ts.TotalHours}h ago"
             : $"{(int)ts.TotalDays}d ago";
    }

    public static string OrDash(string? value) => string.IsNullOrEmpty(value) ? "—" : value!;

    /// <summary>
    /// ISO 8601 timestamp (the Hermes job API uses these for created_at /
    /// next_run_at / last_run_at) → "now", "in 2h", "5m ago", etc.
    /// Returns "—" on null/unparseable.
    /// </summary>
    public static string IsoToRelative(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return "—";
        if (!DateTimeOffset.TryParse(iso, out var when)) return "—";
        var delta = when - DateTimeOffset.UtcNow;
        var abs = delta.Duration();
        var suffix = delta.TotalSeconds < 0 ? " ago" : "";
        var prefix = delta.TotalSeconds < 0 ? "" : "in ";
        if (abs.TotalSeconds < 60) return delta.TotalSeconds < 0 ? "just now" : "now";
        var label = abs.TotalMinutes < 60 ? $"{(int)abs.TotalMinutes}m"
                  : abs.TotalHours < 24 ? $"{(int)abs.TotalHours}h"
                  : abs.TotalDays < 7 ? $"{(int)abs.TotalDays}d"
                  : abs.TotalDays < 30 ? $"{(int)(abs.TotalDays / 7)}w"
                  : $"{(int)(abs.TotalDays / 30)}mo";
        return prefix + label + suffix;
    }

    /// <summary>ISO 8601 → "yyyy-MM-dd HH:mm" local. Used in tooltips.</summary>
    public static string IsoToLocalTime(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return "—";
        if (!DateTimeOffset.TryParse(iso, out var when)) return iso!;
        return when.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    public static string OrZero(int? value) => (value ?? 0).ToString();
    public static string OrZero(long? value) => (value ?? 0L).ToString("N0");

    public static string FormatTokens(long? value) =>
        value is null ? "—" : value.Value.ToString("N0");

    public static string FormatUsd(double? value) =>
        value is null ? "—" : $"${value.Value:F4}";

    /// <summary>
    /// Picks the row card's outer-border brush based on selection. Accent
    /// when selected, default card stroke otherwise. Pulled into a function
    /// so the JobsPage data template can express selection visuals with a
    /// single x:Bind without a heavyweight per-row Style.
    /// </summary>
    public static Brush SelectionBorder(bool isSelected)
    {
        var key = isSelected ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush";
        return (Brush)Application.Current.Resources[key];
    }
}
