using System;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI.Text;

namespace Hermes.App.Markdown;

/// <summary>
/// Renders <c>$$ ... $$</c> block math as a styled card with the LaTeX
/// source visible in monospace and a "Copy LaTeX" button on the right.
/// We do <em>not</em> host a WebView2 with KaTeX for the same reasons as
/// <see cref="MermaidBlock"/> — the always-realized transcript would balloon
/// process count, and async sizing fights the scroll-snap.
///
/// <para>Inline math (<c>$ ... $</c>) is rendered differently — it lives
/// inside paragraph inlines and can't host an arbitrary control, so the
/// inline path falls back to a styled monospace run. See
/// <see cref="MarkdownRenderer"/>'s inline switch.</para>
/// </summary>
internal static class MathBlock
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

        var icon = new TextBlock
        {
            Text = "ƒ",
            FontFamily = new FontFamily("Cambria Math, Cambria, serif"),
            FontStyle = FontStyle.Italic,
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
        };
        Grid.SetColumn(icon, 0);
        header.Children.Add(icon);

        var label = new TextBlock
        {
            Text = "Math (LaTeX)",
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 1);
        header.Children.Add(label);

        var copy = new Button
        {
            Padding = new Thickness(8, 3, 8, 3),
            MinHeight = 26,
            FontSize = 12,
        };
        ToolTipService.SetToolTip(copy, "Copy LaTeX source");
        var copyContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var copyIcon = new FontIcon { Glyph = "\uE8C8", FontSize = 11 };
        var copyLabel = new TextBlock { Text = "Copy LaTeX" };
        copyContent.Children.Add(copyIcon);
        copyContent.Children.Add(copyLabel);
        copy.Content = copyContent;
        copy.Click += (_, _) =>
        {
            try
            {
                var pkg = new DataPackage();
                pkg.SetText(source);
                Clipboard.SetContent(pkg);
                copyLabel.Text = "Copied";
                copyIcon.Glyph = "\uE73E";
            }
            catch
            {
                // Clipboard contention is rare but possible; swallow so the
                // button never throws into the UI.
            }
        };
        Grid.SetColumn(copy, 3);
        header.Children.Add(copy);

        return header;
    }

    private static UIElement BuildSourceBody(string source)
    {
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
            FontSize = 13,
        };
        var p = new Paragraph();
        p.Inlines.Add(new Run { Text = source });
        rtb.Blocks.Add(p);
        body.Content = rtb;
        return body;
    }
}
