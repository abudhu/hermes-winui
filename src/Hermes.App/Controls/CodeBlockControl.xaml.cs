using System;
using Hermes.App.Markdown;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace Hermes.App.Controls;

/// <summary>
/// Renders one fenced code block: language label + Copy button on top, then
/// a horizontally-scrollable monospace body that's either plain
/// (<see cref="HighlightEnabled"/> = false) or syntax-highlighted.
///
/// <para>Designed to be cheap during streaming — when
/// <see cref="HighlightEnabled"/> is false, code is dumped into a single Run
/// rather than tokenized, so 150ms re-render ticks during a streaming reply
/// don't tokenize on every paint.</para>
/// </summary>
public sealed partial class CodeBlockControl : UserControl
{
    public CodeBlockControl()
    {
        InitializeComponent();
    }

    public string Code
    {
        get => (string)GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    public static readonly DependencyProperty CodeProperty =
        DependencyProperty.Register(nameof(Code), typeof(string), typeof(CodeBlockControl),
            new PropertyMetadata("", OnAnyPropertyChanged));

    public string CodeLanguage
    {
        get => (string)GetValue(CodeLanguageProperty);
        set => SetValue(CodeLanguageProperty, value);
    }

    public static readonly DependencyProperty CodeLanguageProperty =
        DependencyProperty.Register(nameof(CodeLanguage), typeof(string), typeof(CodeBlockControl),
            new PropertyMetadata("", OnAnyPropertyChanged));

    public bool HighlightEnabled
    {
        get => (bool)GetValue(HighlightEnabledProperty);
        set => SetValue(HighlightEnabledProperty, value);
    }

    public static readonly DependencyProperty HighlightEnabledProperty =
        DependencyProperty.Register(nameof(HighlightEnabled), typeof(bool), typeof(CodeBlockControl),
            new PropertyMetadata(false, OnAnyPropertyChanged));

    private static void OnAnyPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CodeBlockControl c) c.Rebuild();
    }

    private void Rebuild()
    {
        var code = Code ?? string.Empty;
        var lang = CodeLanguage ?? string.Empty;
        LanguageLabel.Text = string.IsNullOrWhiteSpace(lang) ? "" : lang.ToLowerInvariant();

        CodeBody.Blocks.Clear();
        var paragraph = new Paragraph();

        if (!HighlightEnabled || string.IsNullOrEmpty(SyntaxHighlighter.NormalizeLanguage(lang)))
        {
            paragraph.Inlines.Add(new Run { Text = code });
        }
        else
        {
            foreach (var token in SyntaxHighlighter.Tokenize(code, lang))
            {
                paragraph.Inlines.Add(new Run
                {
                    Text = token.Text,
                    Foreground = BrushFor(token.Kind),
                });
            }
        }

        CodeBody.Blocks.Add(paragraph);
    }

    private static Brush BrushFor(TokenKind kind)
    {
        var key = kind switch
        {
            TokenKind.Keyword     => "HermesCodeKeywordBrush",
            TokenKind.String      => "HermesCodeStringBrush",
            TokenKind.Number      => "HermesCodeNumberBrush",
            TokenKind.Comment     => "HermesCodeCommentBrush",
            TokenKind.Type        => "HermesCodeTypeBrush",
            TokenKind.Punctuation => "HermesCodePlainBrush",
            _                     => "HermesCodePlainBrush",
        };
        return (Brush)Application.Current.Resources[key];
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var pkg = new DataPackage();
            pkg.SetText(Code ?? string.Empty);
            Clipboard.SetContent(pkg);
            CopyLabel.Text = "Copied";
            CopyIcon.Glyph = "\uE73E"; // checkmark
            _ = ResetCopyLabelAsync();
        }
        catch
        {
            // Clipboard contention is rare but possible; swallow so the
            // button never throws into the UI.
        }
    }

    private async System.Threading.Tasks.Task ResetCopyLabelAsync()
    {
        await System.Threading.Tasks.Task.Delay(1500);
        DispatcherQueue.TryEnqueue(() =>
        {
            CopyLabel.Text = "Copy";
            CopyIcon.Glyph = "\uE8C8"; // copy
        });
    }
}
