using System;
using System.Collections.Generic;
using Hermes.App.Markdown;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hermes.App.Controls;

/// <summary>
/// Drop-in markdown view for chat bubbles. Re-renders the visual tree from
/// the latest <see cref="Markdown"/> string, throttled while
/// <see cref="IsStreaming"/> is true so we don't pay the parse + tree-build
/// cost on every 60ms text-flush tick.
///
/// <para>Render lifecycle:</para>
/// <list type="number">
///   <item>Markdown changes while streaming → schedule a 150ms throttled
///         re-render with syntax highlighting OFF.</item>
///   <item>IsStreaming flips false → flush the pending throttle and do a
///         final re-render with syntax highlighting ON.</item>
/// </list>
/// </summary>
public sealed partial class MarkdownPresenter : UserControl
{
    public MarkdownPresenter()
    {
        InitializeComponent();
        _renderTimer = DispatcherQueue.CreateTimer();
        _renderTimer.IsRepeating = false;
        _renderTimer.Interval = TimeSpan.FromMilliseconds(150);
        _renderTimer.Tick += (s, e) => RenderNow();
        Unloaded += (s, e) => _renderTimer.Stop();
    }

    private readonly DispatcherQueueTimer _renderTimer;
    private string _lastRendered = "";
    private bool _lastRenderedHighlighted;

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public static readonly DependencyProperty MarkdownProperty =
        DependencyProperty.Register(nameof(Markdown), typeof(string), typeof(MarkdownPresenter),
            new PropertyMetadata("", OnMarkdownChanged));

    public bool IsStreaming
    {
        get => (bool)GetValue(IsStreamingProperty);
        set => SetValue(IsStreamingProperty, value);
    }

    public static readonly DependencyProperty IsStreamingProperty =
        DependencyProperty.Register(nameof(IsStreaming), typeof(bool), typeof(MarkdownPresenter),
            new PropertyMetadata(false, OnStreamingChanged));

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MarkdownPresenter p) return;
        if (p.IsStreaming)
        {
            // Re-arm throttle. We use a non-repeating timer so each change
            // resets the 150ms window.
            p._renderTimer.Stop();
            p._renderTimer.Start();
        }
        else
        {
            p.RenderNow();
        }
    }

    private static void OnStreamingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MarkdownPresenter p) return;
        // Stream just ended → cancel any pending throttled render and do the
        // final highlighted pass.
        if (e.NewValue is bool nowStreaming && !nowStreaming)
        {
            p._renderTimer.Stop();
            p.RenderNow();
        }
    }

    private void RenderNow()
    {
        var md = Markdown ?? string.Empty;
        // Final pass = highlighted; intermediate streaming passes skip
        // tokenization to keep the timer cheap.
        var highlight = !IsStreaming;
        if (md == _lastRendered && highlight == _lastRenderedHighlighted) return;

        // Build the new visual tree off-screen first so the swap is atomic
        // from the layout system's POV.
        IList<UIElement> blocks;
        try
        {
            blocks = MarkdownRenderer.RenderToBlocks(md, highlight);
        }
        catch (Exception ex)
        {
            // Markdig itself doesn't generally throw, but inline emission
            // can if Hermes hands us pathological content. Don't take down
            // the chat — just show plain text.
            System.Diagnostics.Debug.WriteLine($"MarkdownPresenter render error: {ex}");
            blocks = new List<UIElement>
            {
                new TextBlock { Text = md, TextWrapping = TextWrapping.Wrap, FontSize = 14 },
            };
        }

        Host.Children.Clear();
        foreach (var block in blocks)
            Host.Children.Add(block);

        _lastRendered = md;
        _lastRenderedHighlighted = highlight;
    }
}
