using System;
using Hermes.ApiClient.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Hermes.App.ViewModels;

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
    public string MessageCountText => $"{MessageCount ?? 0} msg";

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
