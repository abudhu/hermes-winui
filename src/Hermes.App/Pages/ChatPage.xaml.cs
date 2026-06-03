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
    /// <summary>How close to the very bottom (in px) the user has to be for
    /// us to re-engage auto-follow. Deliberately tiny — any deliberate scroll
    /// up by even a single wheel notch should break the follow.</summary>
    private const double ReStickSlackPx = 4;

    /// <summary>Tolerance for "did the offset actually change?" — filters out
    /// sub-pixel ViewChanged events that don't represent real user motion.</summary>
    private const double OffsetEpsilonPx = 1;

    /// <summary>Debounce window for size-driven snap-to-bottom. If the user
    /// initiates a scroll within this window, the snap is cancelled and they
    /// keep their position. Long enough to catch a wheel scroll that's
    /// already in flight when content growth fires SizeChanged.</summary>
    private const int SnapDebounceMs = 60;

    private bool _stickToBottom = true;
    private bool _suppressViewChanged;
    private bool _viewTrackingInitialized;
    private double _lastViewedOffset;
    private double _lastViewedScrollable;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _snapTimer;

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
    /// Updates <see cref="_stickToBottom"/> based on the *delta* between the
    /// previous and current scroll position. This is the only place we look
    /// at scroll position to decide stickiness.
    ///
    /// <para>Why deltas, not absolute position: when MarkdownPresenter
    /// rebuilds its visual tree mid-stream, the ScrollViewer's ScrollableHeight
    /// briefly shrinks and the VerticalOffset gets clamped under us. Old code
    /// checked "is the clamped offset near the bottom?" and would falsely
    /// re-stick on every render tick. The delta approach can distinguish
    /// user-initiated scroll (offset changes, scrollable doesn't) from
    /// layout-driven clamping (both change).</para>
    /// </summary>
    private void TranscriptScroll_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_suppressViewChanged) return;
        var sv = TranscriptScroll;
        var newOffset = sv.VerticalOffset;
        var newScrollable = sv.ScrollableHeight;

        if (!_viewTrackingInitialized)
        {
            _viewTrackingInitialized = true;
            _lastViewedOffset = newOffset;
            _lastViewedScrollable = newScrollable;
            return;
        }

        var offsetDelta = newOffset - _lastViewedOffset;
        var scrollableDelta = newScrollable - _lastViewedScrollable;
        _lastViewedOffset = newOffset;
        _lastViewedScrollable = newScrollable;

        // Scrollable extent shrank → offset may have been clamped by the
        // layout system, not the user. Skip the stickiness update; the next
        // size-change snap will keep us in the right place.
        if (scrollableDelta < -OffsetEpsilonPx) return;

        if (offsetDelta < -OffsetEpsilonPx)
        {
            // Offset went UP while scrollable didn't shrink → user scrolled up
            // (wheel, keyboard, scrollbar drag, touch pan). Stop chasing the
            // bottom and cancel any in-flight snap.
            _stickToBottom = false;
            _snapTimer?.Stop();
        }
        else if (offsetDelta > OffsetEpsilonPx)
        {
            // Offset went DOWN → user scrolled down (or our own programmatic
            // snap, but those are suppressed via _suppressViewChanged). Only
            // re-engage auto-follow when they're at the very bottom.
            if (newOffset >= newScrollable - ReStickSlackPx)
            {
                _stickToBottom = true;
            }
        }
    }

    /// <summary>
    /// Content size grew (new message, more tokens, expander toggled). Schedule
    /// a debounced snap-to-bottom — the delay gives an in-flight wheel scroll
    /// a chance to fire its <see cref="TranscriptScroll_ViewChanged"/> first
    /// and cancel the snap, so the user doesn't fight us when they wheel up
    /// while a token is streaming in.
    /// </summary>
    private void TranscriptContent_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_stickToBottom) return;

        if (_snapTimer is null)
        {
            _snapTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            _snapTimer.IsRepeating = false;
            _snapTimer.Interval = TimeSpan.FromMilliseconds(SnapDebounceMs);
            _snapTimer.Tick += SnapTimer_Tick;
        }
        _snapTimer.Stop();
        _snapTimer.Start();
    }

    private void SnapTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        // Re-check stickiness — the user may have wheeled up between
        // SizeChanged and now, in which case ViewChanged set _stickToBottom
        // to false. We also cancel the timer there, but be defensive.
        if (!_stickToBottom) return;

        var sv = TranscriptScroll;
        _suppressViewChanged = true;
        sv.ChangeView(null, sv.ScrollableHeight, null, disableAnimation: true);
        _suppressViewChanged = false;
        // Resync the trackers so the next user-initiated ViewChanged computes
        // its delta from the post-snap position, not the pre-snap one.
        _lastViewedOffset = sv.VerticalOffset;
        _lastViewedScrollable = sv.ScrollableHeight;
    }
}

