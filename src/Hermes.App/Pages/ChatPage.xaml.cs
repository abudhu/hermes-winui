using System;
using System.Collections.Specialized;
using System.ComponentModel;
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

    /// <summary>True while we're subscribed to ViewModel events. Tracks
    /// attach/detach across OnNavigatedTo / OnNavigatedFrom so we don't
    /// double-subscribe (the VM is a DI singleton; the page is recreated
    /// on every navigation and would otherwise pile on handlers).</summary>
    private bool _attached;

    public ChatViewModel ViewModel { get; }

    public ChatPage()
    {
        // Resolve from the singleton container so the VM's state (active
        // SessionId, in-progress stream, current transcript) survives
        // navigation away and back. This is what makes SessionsPage's
        // "Resume conversation" handoff actually land here visibly.
        ViewModel = App.Services.GetRequiredService<ChatViewModel>();

        InitializeComponent();

        // First-render hookup. We also attach on OnNavigatedTo, but on the
        // very first construction OnNavigatedTo and the ctor both fire — the
        // _attached guard keeps us from double-subscribing in that case.
        AttachViewModelEvents();
        UpdateSessionLine();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        AttachViewModelEvents();
        // Reflect any state changes that landed while the page was unloaded
        // (e.g. SessionsPage just called ResumeSessionAsync on the VM).
        UpdateSessionLine();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // Detach so the singleton VM doesn't hold a reference to a stale
        // page instance — the Frame will recreate the page on next nav.
        DetachViewModelEvents();
    }

    private void AttachViewModelEvents()
    {
        if (_attached) return;
        ViewModel.Messages.CollectionChanged += ViewModel_MessagesChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        _attached = true;
    }

    private void DetachViewModelEvents()
    {
        if (!_attached) return;
        ViewModel.Messages.CollectionChanged -= ViewModel_MessagesChanged;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _attached = false;
    }

    private void ViewModel_MessagesChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateSessionLine();

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
    /// Enter sends, Shift+Enter inserts a newline. We use <c>PreviewKeyDown</c>
    /// (tunneling) rather than <c>KeyDown</c> (bubbling) because WinUI 3's
    /// TextBox handles Enter for newline-insertion via a class handler that
    /// runs *before* the bubbling KeyDown event reaches us — at which point
    /// it's too late to set <c>e.Handled = true</c> to suppress the newline.
    /// </summary>
    private void Composer_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
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
    /// Copies the raw markdown content of the assistant message into the
    /// clipboard. Useful because <see cref="Microsoft.UI.Xaml.Controls.RichTextBlock"/>
    /// text selection in WinUI 3 can't span multiple block elements (so the
    /// user can't drag-select an entire reply that has code fences inside).
    /// </summary>
    private void CopyResponse_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not MessageVm vm) return;
        try
        {
            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
            pkg.SetText(vm.Content ?? string.Empty);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
        }
        catch
        {
            // Clipboard contention happens occasionally; the user can just retry.
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

