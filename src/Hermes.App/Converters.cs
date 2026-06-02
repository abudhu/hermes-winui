using System;
using Microsoft.UI.Xaml;

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

    public static string OrZero(int? value) => (value ?? 0).ToString();
    public static string OrZero(long? value) => (value ?? 0L).ToString("N0");

    public static string FormatTokens(long? value) =>
        value is null ? "—" : value.Value.ToString("N0");

    public static string FormatUsd(double? value) =>
        value is null ? "—" : $"${value.Value:F4}";
}
