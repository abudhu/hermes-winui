using System;
using Hermes.ApiClient.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Hermes.App.ViewModels;

/// <summary>Why this row is in the visible list. Drives the per-row
/// "matched by message content" indicator + snippet display on the
/// Sessions sidebar.</summary>
public enum SessionMatchKind
{
    /// <summary>No active search query — just a normal sidebar row.</summary>
    None,

    /// <summary>Matched by something the sidebar already shows (title,
    /// preview, model, source). The badge isn't needed because the user
    /// can already see why the row matched.</summary>
    Metadata,

    /// <summary>Matched by FTS5 over message content. Snippet under the
    /// title shows the matched fragment; the badge tells the user that's
    /// what surfaced this conversation.</summary>
    Content,
}

/// <summary>Row model for the Sessions page list. Flattens a SessionSummary for binding.</summary>
public sealed class SessionRowVm
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Source { get; set; } = "";
    public string? Model { get; set; }
    public int? MessageCount { get; set; }
    public bool IsOpen { get; set; }
    public string Preview { get; set; } = "";
    public string LastActiveRelative { get; set; } = "—";

    /// <summary>Epoch-seconds last-active timestamp, kept around so the
    /// SessionsPage can re-bucket rows by date after filtering without
    /// having to hold on to the raw <see cref="SessionSummary"/>.</summary>
    public double? LastActiveEpoch { get; set; }

    /// <summary>Set on rows that were produced by a search. Drives the
    /// content-match icon and the snippet vs preview swap in the row
    /// template.</summary>
    public SessionMatchKind MatchKind { get; set; } = SessionMatchKind.None;

    /// <summary>The matched fragment from FTS5, with the gateway's
    /// <c>&gt;&gt;&gt;…&lt;&lt;&lt;</c> highlight markers stripped. Only
    /// populated for <see cref="SessionMatchKind.Content"/> rows.</summary>
    public string MatchSnippet { get; set; } = "";

    public bool IsContentMatch => MatchKind == SessionMatchKind.Content;

    /// <summary>What the row's secondary line shows. If a server-side
    /// content hit produced a snippet — regardless of whether this row
    /// also matched by metadata — show the snippet (it's strictly more
    /// informative than the row's normal preview for explaining the
    /// match). Otherwise fall back to the existing preview behaviour.</summary>
    public string DisplaySnippet =>
        !string.IsNullOrEmpty(MatchSnippet) ? MatchSnippet : Preview;

    public string MessageCountText => MessageCount is int n ? $"{n} msg" : "—";

    public static SessionRowVm FromSummary(SessionSummary s)
    {
        var title = !string.IsNullOrWhiteSpace(s.Title) ? s.Title!
                  : !string.IsNullOrWhiteSpace(s.Preview) ? (s.Preview!.Length > 60 ? s.Preview![..60] + "…" : s.Preview!)
                  : $"Session {s.Id}";
        return new SessionRowVm
        {
            Id = s.Id,
            Title = title,
            Source = s.Source ?? "?",
            Model = s.Model,
            MessageCount = s.MessageCount,
            IsOpen = s.IsOpen,
            Preview = s.Preview ?? "",
            LastActiveRelative = Converters.EpochSecondsToRelative(s.LastActive),
            LastActiveEpoch = s.LastActive ?? s.StartedAt,
        };
    }

    /// <summary>
    /// Builds a row from a search hit whose <c>session_id</c> isn't in
    /// the already-loaded sidebar list. Search responses only carry
    /// minimal session attribution (source / model / started-at), so the
    /// row gets a stub title and an unknown-message-count "—" — clicking
    /// it will load the real title via <c>GetSessionAsync</c> the same
    /// way any other row does.
    /// </summary>
    public static SessionRowVm FromSearchResult(SessionSearchResult r)
    {
        var shortId = r.SessionId.Length > 8 ? r.SessionId[..8] : r.SessionId;
        return new SessionRowVm
        {
            Id = r.SessionId,
            Title = $"Session {shortId}",
            Source = r.Source ?? "?",
            Model = r.Model,
            MessageCount = null,
            IsOpen = false,
            Preview = "",
            LastActiveRelative = Converters.EpochSecondsToRelative(r.SessionStarted),
            LastActiveEpoch = r.SessionStarted,
            MatchKind = SessionMatchKind.Content,
            MatchSnippet = StripSnippetMarkers(r.Snippet),
        };
    }

    /// <summary>
    /// Clones an existing sidebar row and overlays a content-match
    /// snippet on top. Used in two cases:
    /// 1. A session is in <c>_allRows</c> AND in the server-side
    ///    <i>content-only</i> results — the cloned row becomes a Content
    ///    match (icon visible, snippet visible).
    /// 2. A metadata-match row from <c>_allRows</c> ALSO got a server-side
    ///    content hit — the cloned row stays a Metadata match (no icon,
    ///    title already explains the match) but still surfaces the
    ///    snippet for context.
    /// Cloning (rather than mutating in place) keeps the source row in
    /// <c>_allRows</c> pristine so clearing the search restores the
    /// original sidebar view without bookkeeping.
    /// </summary>
    public static SessionRowVm WithContentSnippet(SessionRowVm baseRow, string rawSnippet, SessionMatchKind kind = SessionMatchKind.Content) => new()
    {
        Id = baseRow.Id,
        Title = baseRow.Title,
        Source = baseRow.Source,
        Model = baseRow.Model,
        MessageCount = baseRow.MessageCount,
        IsOpen = baseRow.IsOpen,
        Preview = baseRow.Preview,
        LastActiveRelative = baseRow.LastActiveRelative,
        LastActiveEpoch = baseRow.LastActiveEpoch,
        MatchKind = kind,
        MatchSnippet = StripSnippetMarkers(rawSnippet),
    };

    /// <summary>Strips the FTS5 highlight delimiters the gateway wraps
    /// around the matched term (configured as <c>&gt;&gt;&gt;</c> /
    /// <c>&lt;&lt;&lt;</c> in <c>web_server.py</c>). The truncation
    /// indicator (<c>...</c>) is intentionally preserved — it tells the
    /// user "this is a fragment".</summary>
    private static string StripSnippetMarkers(string? snippet)
    {
        if (string.IsNullOrEmpty(snippet)) return "";
        return snippet.Replace(">>>", "").Replace("<<<", "");
    }
}

/// <summary>Row model for a single message inside the selected-session detail pane.</summary>
public sealed class MessageRowVm
{
    public string Role { get; }
    public string Content { get; }
    public DateTimeOffset When { get; }

    public MessageRowVm(string role, string content, DateTimeOffset when)
    {
        Role = role;
        Content = content;
        When = when;
    }

    public string RoleLabel => Role switch
    {
        "user" => "You",
        "assistant" => "Hermes",
        "tool" => "Tool",
        "system" => "System",
        _ => Role,
    };

    public string TimeLabel => When.ToLocalTime().ToString("HH:mm:ss");

    public HorizontalAlignment Alignment => Role == "user" ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    public Brush BubbleBrush => Role == "user"
        ? new SolidColorBrush(Color.FromArgb(0x18, 0x00, 0x78, 0xD4))
        : (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];

    public static MessageRowVm FromMessage(SessionMessage m)
    {
        var when = m.Timestamp is double t
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)(t * 1000))
            : DateTimeOffset.Now;
        return new MessageRowVm(m.Role, m.Content ?? string.Empty, when);
    }
}
