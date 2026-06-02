using System;
using System.ComponentModel;
using Hermes.ApiClient;
using Hermes.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace Hermes.App.Pages;

public sealed partial class ChatPage : Page
{
    /// <summary>Pixel slack for "is the user near the bottom?". Generous so a slight scroll-back doesn't break auto-follow.</summary>
    private const double StickyBottomSlackPx = 48;

    private bool _stickToBottom = true;
    private bool _suppressViewChanged;

    public ChatViewModel ViewModel { get; }

    public ChatPage()
    {
        ViewModel = new ChatViewModel(
            App.Services.GetRequiredService<HermesApiClient>(),
            App.Services.GetRequiredService<HermesStreamingClient>(),
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());

        ViewModel.Messages.CollectionChanged += (_, __) => UpdateSessionLine();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;

        InitializeComponent();
        UpdateSessionLine();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // Don't dispose — page may navigate back. Leave the stream running.
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.SessionId) ||
            e.PropertyName == nameof(ChatViewModel.SessionTitle))
        {
            UpdateSessionLine();
        }
    }

    private void UpdateSessionLine()
    {
        if (SessionLine is null) return;
        SessionLine.Text = string.IsNullOrEmpty(ViewModel.SessionId)
            ? "No session yet — first message starts one"
            : $"Session {Shorten(ViewModel.SessionId)} · {ViewModel.Messages.Count} message{(ViewModel.Messages.Count == 1 ? "" : "s")}";
    }

    private static string Shorten(string id) => id.Length > 16 ? id[..16] + "…" : id;

    /// <summary>
    /// Enter sends, Shift+Enter inserts a newline. We swallow the bare-Enter
    /// keystroke so the TextBox doesn't also add the line break.
    /// </summary>
    private void Composer_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;

        var shift = (Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (shift) return; // let the TextBox handle Shift+Enter as newline

        e.Handled = true;
        if (ViewModel.SendCommand.CanExecute(null))
        {
            ViewModel.SendCommand.Execute(null);
        }
    }

    /// <summary>
    /// Tracks whether the user is "near the bottom" so we can keep auto-scrolling
    /// when assistant tokens stream in, but stop fighting them if they scrolled up
    /// to read history.
    /// </summary>
    private void TranscriptScroll_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_suppressViewChanged) return;
        var sv = TranscriptScroll;
        var nearBottom = sv.VerticalOffset + sv.ViewportHeight >= sv.ScrollableHeight - StickyBottomSlackPx;
        _stickToBottom = nearBottom;
    }

    /// <summary>
    /// Content size grew (new message, more tokens, expander toggled). If the
    /// user was already near the bottom, snap back to it.
    /// </summary>
    private void TranscriptContent_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_stickToBottom) return;
        var sv = TranscriptScroll;
        // Defer one tick so layout pass settles before we measure ScrollableHeight.
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(() =>
        {
            _suppressViewChanged = true;
            sv.ChangeView(null, sv.ScrollableHeight, null, disableAnimation: true);
            _suppressViewChanged = false;
        });
    }
}
