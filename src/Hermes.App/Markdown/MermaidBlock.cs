using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Hermes.App.Markdown;

/// <summary>
/// Renders a ```` ```mermaid ```` fenced block as a styled card with an
/// "Open in mermaid.live" launch button. We deliberately don't host a
/// WebView2 per block because:
/// <list type="bullet">
///   <item>The transcript ItemsRepeater uses <c>VerticalCacheLength="100"</c>
///         (intentional, to avoid scroll-bounce), so every realized block
///         stays alive forever — a WebView2 per diagram would balloon
///         process count as the transcript grows.</item>
///   <item>WebView2 async init + sizing flicker fights the one-shot
///         scroll-snap that <c>ChatPage.ViewModel_MessagesChanged</c> uses
///         to land the user at the bottom on each new message.</item>
/// </list>
/// The "Open in mermaid.live" link uses the canonical <c>#pako:</c> share
/// format (state object + zlib deflate + URL-safe base64) so the editor
/// opens with the source pre-loaded and ready to render.
/// </summary>
internal static class MermaidBlock
{
    public static UIElement Build(string source)
    {
        var trimmed = (source ?? string.Empty).TrimEnd();

        var card = new Border
        {
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            Background = (Brush)Application.Current.Resources["SubtleFillColorTertiaryBrush"],
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 4, 0, 4),
        };

        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(BuildHeader(trimmed));
        stack.Children.Add(BuildSourceBody(trimmed));
        card.Child = stack;

        return card;
    }

    private static UIElement BuildHeader(string source)
    {
        var header = new Grid
        {
            ColumnSpacing = 8,
            Padding = new Thickness(12, 8, 8, 8),
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            CornerRadius = new CornerRadius(8, 8, 0, 0),
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            Glyph = "\uE9E9", // org-chart style — fits "diagram"
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
        };
        Grid.SetColumn(icon, 0);
        header.Children.Add(icon);

        var label = new TextBlock
        {
            Text = "Mermaid diagram",
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 1);
        header.Children.Add(label);

        var launch = new Button
        {
            Padding = new Thickness(8, 3, 8, 3),
            MinHeight = 26,
            FontSize = 12,
        };
        ToolTipService.SetToolTip(launch, "Open in mermaid.live (the source is pre-loaded)");
        var launchContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        launchContent.Children.Add(new FontIcon { Glyph = "\uE8A7", FontSize = 11 });
        launchContent.Children.Add(new TextBlock { Text = "Open in mermaid.live" });
        launch.Content = launchContent;
        launch.Click += async (_, _) =>
        {
            try
            {
                var uri = BuildMermaidLiveUri(source);
                await Launcher.LaunchUriAsync(uri);
            }
            catch
            {
                // LaunchUriAsync can fail if no browser is registered or
                // shell denies the launch. There's no useful recovery from
                // a markdown-render context — the source remains visible
                // below for manual copy.
            }
        };
        Grid.SetColumn(launch, 3);
        header.Children.Add(launch);

        return header;
    }

    private static UIElement BuildSourceBody(string source)
    {
        // Mermaid bodies are usually short (< 30 lines); a non-scrolling
        // monospace text block is the right read here. Wrapping is OFF so
        // the indentation in graph syntax stays meaningful.
        var body = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(12, 8, 12, 10),
        };
        var rtb = new RichTextBlock
        {
            IsTextSelectionEnabled = true,
            FontFamily = new FontFamily("Consolas, 'Cascadia Mono', 'Segoe UI Mono', monospace"),
            FontSize = 12,
        };
        var p = new Paragraph();
        p.Inlines.Add(new Run { Text = source });
        rtb.Blocks.Add(p);
        body.Content = rtb;
        return body;
    }

    /// <summary>
    /// Builds a <c>https://mermaid.live/edit#pako:...</c> URL whose payload
    /// is the JSON state object the editor expects (code + mermaid config +
    /// auto-sync flags), zlib-deflated and URL-safe-base64-encoded.
    /// Mermaid.live's share-link generator and js-pako's <c>fromUint8Array</c>
    /// in URL-safe mode produce the same shape — verified against the
    /// editor's live behavior.
    /// </summary>
    public static Uri BuildMermaidLiveUri(string source)
    {
        // Default mermaid config — the editor expects a JSON STRING here,
        // not a nested object. An empty "{}" works; supplying a theme makes
        // the rendered preview look closer to a docs theme.
        var state = new
        {
            code = source ?? string.Empty,
            mermaid = "{\n  \"theme\": \"default\"\n}",
            autoSync = true,
            updateDiagram = true,
        };
        var json = JsonSerializer.Serialize(state);
        var jsonBytes = Encoding.UTF8.GetBytes(json);

        using var ms = new MemoryStream();
        // ZLibStream writes the zlib wrapper (CMF/FLG header + Adler-32
        // checksum) that pako.deflate produces by default — distinct from
        // System.IO.Compression.DeflateStream which is "raw deflate" and
        // would produce a payload mermaid.live can't decode.
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            z.Write(jsonBytes, 0, jsonBytes.Length);
        }
        var compressed = ms.ToArray();

        // URL-safe base64: + -> -, / -> _, strip trailing = padding.
        var b64 = Convert.ToBase64String(compressed)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        return new Uri($"https://mermaid.live/edit#pako:{b64}");
    }
}
