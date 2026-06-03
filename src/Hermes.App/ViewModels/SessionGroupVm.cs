using System;
using System.Collections.ObjectModel;
using System.Globalization;

namespace Hermes.App.ViewModels;

/// <summary>
/// One date-bucket of session rows for the grouped session list on
/// <c>SessionsPage</c> (Today / Yesterday / This week / Last week / month).
///
/// <para>
/// Wires up to WinUI's <c>CollectionViewSource</c> with
/// <c>IsSourceGrouped="True"</c> and <c>ItemsPath="Items"</c>: the items
/// in <see cref="Items"/> appear under a header rendered from
/// <see cref="Header"/>. Groups themselves are sorted in
/// <see cref="SessionsPage"/> by <see cref="SortKey"/> descending so newer
/// buckets land at the top.
/// </para>
/// </summary>
public sealed class SessionGroupVm
{
    public string Header { get; }

    /// <summary>Sortable rank; larger means "more recent". Today = the largest,
    /// older months get smaller values. Used to order groups top-to-bottom.</summary>
    public long SortKey { get; }

    public ObservableCollection<SessionRowVm> Items { get; } = [];

    public SessionGroupVm(string header, long sortKey)
    {
        Header = header;
        SortKey = sortKey;
    }
}

/// <summary>
/// Buckets an epoch-seconds timestamp into a (sortKey, header) pair for
/// the session list groupings. Today/Yesterday/This week/Last week always
/// outrank monthly buckets; older months sort by year+month descending.
/// </summary>
public static class DateGroupHelper
{
    // SortKey encoding (larger = more recent):
    //   Today                            = 9_999_999_999
    //   Yesterday                        = 9_999_999_998
    //   This week                        = 9_999_999_997
    //   Last week                        = 9_999_999_996
    //   Older months (this year + before)= year * 100 + month  (e.g. 202604)
    // Using widely-spaced top values keeps "older months" from ever colliding
    // with the fixed buckets.

    private const long TodayKey     = 9_999_999_999L;
    private const long YesterdayKey = 9_999_999_998L;
    private const long ThisWeekKey  = 9_999_999_997L;
    private const long LastWeekKey  = 9_999_999_996L;

    public static (long SortKey, string Header) BucketFor(DateTimeOffset when)
    {
        var localDate = when.LocalDateTime.Date;
        var today = DateTime.Today;

        if (localDate == today) return (TodayKey, "Today");
        if (localDate == today.AddDays(-1)) return (YesterdayKey, "Yesterday");

        // ISO-ish week-start using the current culture (US = Sunday, most of EU = Monday)
        var firstOfWeek = StartOfWeek(today);
        var firstOfLastWeek = firstOfWeek.AddDays(-7);

        if (localDate >= firstOfWeek) return (ThisWeekKey, "This week");
        if (localDate >= firstOfLastWeek) return (LastWeekKey, "Last week");

        // Monthly bucket: header is "April 2026" (or just "April" if it's this year).
        var key = (long)localDate.Year * 100 + localDate.Month;
        var header = localDate.Year == today.Year
            ? localDate.ToString("MMMM", CultureInfo.CurrentCulture)
            : localDate.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        return (key, header);
    }

    /// <summary>Returns the date of the first day of the week containing
    /// <paramref name="d"/>, using the current culture's <c>FirstDayOfWeek</c>.</summary>
    public static DateTime StartOfWeek(DateTime d)
    {
        var first = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var diff = ((int)d.DayOfWeek - (int)first + 7) % 7;
        return d.AddDays(-diff);
    }

    /// <summary>Helper for callers holding epoch-seconds (the API's
    /// <c>last_active</c> field is a JSON double of seconds-since-1970).</summary>
    public static (long SortKey, string Header) BucketForEpochSeconds(double? epoch)
    {
        if (epoch is not double e || double.IsNaN(e)) return (0, "Unknown");
        var dto = DateTimeOffset.FromUnixTimeMilliseconds((long)(e * 1000));
        return BucketFor(dto);
    }
}
