using System;
using System.Collections.Generic;
using System.Linq;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;
using Windows.System;

namespace Hermes.App.Markdown;

/// <summary>
/// Walks a Markdig <see cref="MarkdownDocument"/> and emits the equivalent
/// WinUI 3 visual tree (paragraphs as <see cref="RichTextBlock"/>, code
/// fences as <see cref="Controls.CodeBlockControl"/>, etc).
///
/// <para>
/// Two render modes are supported:
/// <list type="bullet">
///   <item>Live/streaming: <see cref="RenderToBlocks(string, bool)"/> with
///         <c>highlight=false</c> renders code fences as plain monospace so
///         we don't tokenize them on every 150ms re-render tick.</item>
///   <item>Final: <c>highlight=true</c> applies syntax highlighting via
///         <see cref="SyntaxHighlighter"/>.</item>
/// </list>
/// </para>
/// </summary>
public static class MarkdownRenderer
{
    /// <summary>Reused parser pipeline. Markdig's pipelines are thread-safe
    /// once built so a single static instance is fine.</summary>
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseAutoLinks()
        .Build();

    /// <summary>Font fallback chain for inline and block code. Cached as a
    /// single FontFamily instance so we're not constructing one per inline.</summary>
    private static readonly FontFamily MonospaceFont =
        new("Consolas, 'Cascadia Mono', 'Segoe UI Mono', monospace");

    /// <summary>Parse + emit. Returns block-level <see cref="UIElement"/>s
    /// in document order; the caller stuffs them into a StackPanel.</summary>
    public static IList<UIElement> RenderToBlocks(string markdown, bool highlight)
    {
        var blocks = new List<UIElement>();
        if (string.IsNullOrEmpty(markdown)) return blocks;

        var doc = Markdig.Markdown.Parse(markdown, Pipeline);
        foreach (var block in doc)
        {
            var ui = RenderBlock(block, highlight);
            if (ui is not null) blocks.Add(ui);
        }
        return blocks;
    }

    private static UIElement? RenderBlock(Markdig.Syntax.Block block, bool highlight) => block switch
    {
        HeadingBlock h            => RenderHeading(h),
        ParagraphBlock p          => RenderParagraph(p),
        FencedCodeBlock fc        => RenderFencedCode(fc, highlight),
        CodeBlock cb              => RenderIndentedCode(cb),
        QuoteBlock q              => RenderQuote(q, highlight),
        ListBlock lb              => RenderList(lb, highlight),
        ThematicBreakBlock _      => RenderThematicBreak(),
        Table t                   => RenderTable(t),
        HtmlBlock html            => RenderHtmlAsPlain(html),
        _                         => null,
    };

    private static RichTextBlock RenderHeading(HeadingBlock h)
    {
        var rtb = NewRichTextBlock();
        // Step font size down per level so H1 is biggest and H6 is body-ish.
        var size = h.Level switch
        {
            1 => 22.0,
            2 => 19.0,
            3 => 17.0,
            4 => 15.0,
            5 => 14.0,
            _ => 13.0,
        };
        var para = new Paragraph { Margin = new Thickness(0, h.Level <= 2 ? 8 : 4, 0, 4) };
        AppendInlines(para.Inlines, h.Inline, baseFontSize: size, semibold: true);
        rtb.Blocks.Add(para);
        return rtb;
    }

    private static RichTextBlock RenderParagraph(ParagraphBlock p)
    {
        var rtb = NewRichTextBlock();
        var para = new Paragraph();
        AppendInlines(para.Inlines, p.Inline, baseFontSize: 14);
        rtb.Blocks.Add(para);
        return rtb;
    }

    private static UIElement RenderFencedCode(FencedCodeBlock fc, bool highlight)
    {
        return new Hermes.App.Controls.CodeBlockControl
        {
            Code = JoinCodeLines(fc.Lines),
            CodeLanguage = fc.Info ?? "",
            HighlightEnabled = highlight,
        };
    }

    private static UIElement RenderIndentedCode(CodeBlock cb)
    {
        return new Hermes.App.Controls.CodeBlockControl
        {
            Code = JoinCodeLines(cb.Lines),
            CodeLanguage = "",
            HighlightEnabled = false,
        };
    }

    private static UIElement RenderQuote(QuoteBlock q, bool highlight)
    {
        var panel = new StackPanel { Spacing = 4 };
        foreach (var child in q)
        {
            var rendered = RenderBlock(child, highlight);
            if (rendered is not null) panel.Children.Add(rendered);
        }
        return new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = (Brush)Application.Current.Resources["AccentFillColorTertiaryBrush"],
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 4, 0, 4),
            Child = panel,
        };
    }

    private static UIElement RenderList(ListBlock lb, bool highlight)
    {
        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 2, 0, 2) };
        int index = 1;
        foreach (var item in lb)
        {
            if (item is not ListItemBlock listItem) continue;
            var row = new Grid { ColumnSpacing = 6, Margin = new Thickness(0, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Bullet glyph or numeric label. Task-list checkbox handled by
            // a special leading inline that Markdig stuffs into the first
            // paragraph; we let that flow through normally.
            var marker = new TextBlock
            {
                Text = lb.IsOrdered ? $"{index}." : "•",
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            };
            Grid.SetColumn(marker, 0);
            row.Children.Add(marker);

            var content = new StackPanel { Spacing = 2 };
            foreach (var child in listItem)
            {
                var rendered = RenderBlock(child, highlight);
                if (rendered is not null) content.Children.Add(rendered);
            }
            Grid.SetColumn(content, 1);
            row.Children.Add(content);

            panel.Children.Add(row);
            index++;
        }
        return panel;
    }

    private static UIElement RenderThematicBreak() => new Border
    {
        Height = 1,
        Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
        Margin = new Thickness(0, 8, 0, 8),
    };

    private static UIElement RenderTable(Table t)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        // One column per Markdig column descriptor; fall back to scanning
        // the widest row if descriptors are absent.
        int cols = t.ColumnDefinitions.Count;
        if (cols == 0)
        {
            cols = t.OfType<TableRow>().Select(r => r.Count).DefaultIfEmpty(1).Max();
        }
        for (int c = 0; c < cols; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        int rowIndex = 0;
        foreach (var rowObj in t)
        {
            if (rowObj is not TableRow row) continue;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            int colIndex = 0;
            foreach (var cellObj in row)
            {
                if (cellObj is not TableCell cell) continue;
                var cellPanel = new StackPanel { Spacing = 2 };
                foreach (var child in cell)
                {
                    var rendered = RenderBlock(child, highlight: false);
                    if (rendered is not null) cellPanel.Children.Add(rendered);
                }
                var cellBorder = new Border
                {
                    BorderBrush = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(8, 4, 8, 4),
                    Background = row.IsHeader
                        ? (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"]
                        : null,
                    Child = cellPanel,
                };
                Grid.SetRow(cellBorder, rowIndex);
                Grid.SetColumn(cellBorder, colIndex);
                grid.Children.Add(cellBorder);
                colIndex++;
            }
            rowIndex++;
        }
        return new Border
        {
            BorderBrush = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1, 1, 0, 0),
            Child = grid,
        };
    }

    /// <summary>Hermes occasionally emits raw HTML blocks. We don't render
    /// them — just show the raw text so the user can see what was sent
    /// without us interpreting potentially-untrusted markup.</summary>
    private static UIElement RenderHtmlAsPlain(HtmlBlock html)
    {
        var rtb = NewRichTextBlock();
        var p = new Paragraph();
        p.Inlines.Add(new Run
        {
            Text = JoinCodeLines(html.Lines),
            FontFamily = MonospaceFont,
            FontSize = 13,
        });
        rtb.Blocks.Add(p);
        return rtb;
    }

    /// <summary>Flatten a Markdig <see cref="Markdig.Helpers.StringLineGroup"/>
    /// (a backing array with a logical Count) into a newline-joined string.
    /// Used by every block type that surfaces raw line content — fenced code,
    /// indented code, raw HTML.</summary>
    private static string JoinCodeLines(Markdig.Helpers.StringLineGroup group) =>
        string.Join("\n", group.Lines
            .Take(group.Count)
            .Select(l => l.Slice.ToString() ?? string.Empty));

    // -----------------------------------------------------------------------
    // Inline emission
    // -----------------------------------------------------------------------

    private static void AppendInlines(
        InlineCollection target,
        ContainerInline? container,
        double baseFontSize = 14,
        bool semibold = false)
    {
        if (container is null) return;
        foreach (var inline in container)
            AppendInline(target, inline, baseFontSize, semibold);
    }

    private static void AppendInline(
        InlineCollection target,
        Markdig.Syntax.Inlines.Inline inline,
        double baseFontSize,
        bool semibold)
    {
        switch (inline)
        {
            case LiteralInline lit:
                target.Add(MakeRun(lit.Content.ToString(), baseFontSize, semibold));
                break;

            case EmphasisInline em:
                {
                    var span = new Span();
                    bool strong = em.DelimiterCount >= 2;
                    foreach (var child in em)
                        AppendInline(span.Inlines, child, baseFontSize, semibold);
                    foreach (var run in span.Inlines.OfType<Run>())
                    {
                        if (strong) run.FontWeight = FontWeights.SemiBold;
                        else run.FontStyle = FontStyle.Italic;
                    }
                    target.Add(span);
                    break;
                }

            case CodeInline code:
                {
                    var run = new Run
                    {
                        Text = code.Content,
                        FontFamily = MonospaceFont,
                        FontSize = baseFontSize - 1,
                        // Inline elements don't reliably support a Background
                        // brush in WinUI 3, so we lean on monospace + a subtle
                        // accent foreground tint to set inline code apart.
                        Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
                    };
                    target.Add(run);
                    break;
                }

            case LinkInline link:
                {
                    if (TryMakeHyperlink(link, baseFontSize, out var hyperlink))
                    {
                        target.Add(hyperlink);
                    }
                    else
                    {
                        // Unsupported scheme — render the visible link text
                        // as plain so the user still sees it.
                        var span = new Span();
                        foreach (var child in link)
                            AppendInline(span.Inlines, child, baseFontSize, semibold);
                        target.Add(span);
                    }
                    break;
                }

            case LineBreakInline lb:
                target.Add(lb.IsHard ? new LineBreak() : (Microsoft.UI.Xaml.Documents.Inline)MakeRun(" ", baseFontSize, semibold));
                break;

            case AutolinkInline auto:
                {
                    var l = new LinkInline { Url = auto.Url };
                    l.AppendChild(new LiteralInline(auto.Url));
                    if (TryMakeHyperlink(l, baseFontSize, out var hyperlink))
                        target.Add(hyperlink);
                    else
                        target.Add(MakeRun(auto.Url, baseFontSize, semibold));
                    break;
                }

            case TaskList task:
                {
                    target.Add(MakeRun(task.Checked ? "[x] " : "[ ] ", baseFontSize, semibold));
                    break;
                }

            case HtmlInline hi:
                target.Add(MakeRun(hi.Tag, baseFontSize, semibold));
                break;

            case ContainerInline ci:
                foreach (var child in ci)
                    AppendInline(target, child, baseFontSize, semibold);
                break;
        }
    }

    private static Run MakeRun(string text, double size, bool semibold)
    {
        var r = new Run { Text = text, FontSize = size };
        if (semibold) r.FontWeight = FontWeights.SemiBold;
        return r;
    }

    private static bool TryMakeHyperlink(
        LinkInline link,
        double baseFontSize,
        out Hyperlink? hyperlink)
    {
        hyperlink = null;
        if (string.IsNullOrEmpty(link.Url)) return false;
        if (!Uri.TryCreate(link.Url, UriKind.Absolute, out var uri)) return false;
        // Only allow http/https — protects against weird schemes (file://,
        // ms-settings:, etc.) that Hermes could be tricked into emitting.
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        var h = new Hyperlink { NavigateUri = uri };
        // Click is handled by WinUI which calls LaunchUriAsync internally, but
        // we wire our own click so we can keep the validation in one place.
        h.Click += async (s, e) =>
        {
            try { await Launcher.LaunchUriAsync(uri); } catch { /* ignored */ }
        };
        // Append link-text children as nested inlines so emphasis inside a
        // link still renders correctly.
        foreach (var child in link)
            AppendInline(h.Inlines, child, baseFontSize, semibold: false);
        // If the link had no inline children fall back to showing the URL.
        if (h.Inlines.Count == 0)
            h.Inlines.Add(MakeRun(link.Url, baseFontSize, semibold: false));
        hyperlink = h;
        return true;
    }

    private static RichTextBlock NewRichTextBlock() => new()
    {
        IsTextSelectionEnabled = true,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 14,
    };
}
