using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Hermes.App.ViewModels;

/// <summary>
/// One row in the Diagnostics page's "Platform bridges" list — derived
/// from a single entry in <c>DetailedHealth.Platforms</c>. The bridge
/// dictionary maps platform names (e.g. "api_server", "telegram",
/// "discord") to a per-bridge status; this VM presents the row with a
/// state glyph, the platform name, and a human-readable summary
/// combining state + optional error code/message.
/// </summary>
/// <param name="Name">The platform key (e.g. "api_server").</param>
/// <param name="State">Raw state string from the gateway
/// ("running", "stopped", "error", ...).</param>
/// <param name="StateDescription">Human-readable summary. Combines
/// <paramref name="State"/> with the optional error code/message so
/// the row can fit on one line for the common case but expand
/// gracefully when something's wrong.</param>
/// <param name="Glyph">Segoe Fluent Icons glyph reflecting state —
/// green check for running, red cross for error, neutral dot otherwise.</param>
/// <param name="Foreground">Brush used for the glyph; matches the
/// state's semantic colour.</param>
public sealed class PlatformBridgeVm
{
    public string Name { get; set; } = "";
    public string State { get; set; } = "";
    public string StateDescription { get; set; } = "";
    public string Glyph { get; set; } = "\uE91B";
    public Brush Foreground { get; set; } = new SolidColorBrush(Color.FromArgb(0xFF, 0xF5, 0xA8, 0x23));

    public static PlatformBridgeVm From(string name, string state, string? errorCode, string? errorMessage)
    {
        var safeState = state ?? "";
        var (glyph, brush) = StyleFor(safeState);
        return new PlatformBridgeVm
        {
            Name = name,
            State = safeState,
            StateDescription = BuildDescription(safeState, errorCode, errorMessage),
            Glyph = glyph,
            Foreground = brush,
        };
    }

    private static (string glyph, Brush brush) StyleFor(string state) => state?.ToLowerInvariant() switch
    {
        "running" or "ready" or "ok" or "healthy" =>
            ("\uE73E", new SolidColorBrush(Color.FromArgb(0xFF, 0x6C, 0xCB, 0x5F))),
        "error" or "failed" or "crashed" =>
            ("\uE783", new SolidColorBrush(Color.FromArgb(0xFF, 0xE8, 0x1C, 0x1C))),
        "stopped" or "disabled" or "off" =>
            ("\uE711", new SolidColorBrush(Color.FromArgb(0xFF, 0x9E, 0x9E, 0x9E))),
        _ =>
            ("\uE91B", new SolidColorBrush(Color.FromArgb(0xFF, 0xF5, 0xA8, 0x23))),
    };

    private static string BuildDescription(string state, string? errorCode, string? errorMessage)
    {
        var stateText = string.IsNullOrWhiteSpace(state) ? "unknown" : state;
        if (string.IsNullOrWhiteSpace(errorCode) && string.IsNullOrWhiteSpace(errorMessage))
            return stateText;
        if (!string.IsNullOrWhiteSpace(errorMessage) && !string.IsNullOrWhiteSpace(errorCode))
            return $"{stateText} — {errorCode}: {errorMessage}";
        return $"{stateText} — {errorCode ?? errorMessage}";
    }
}
