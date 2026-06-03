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

    /// <summary>Greeting shown on the landing surface when there are no
    /// messages yet. Initialised synchronously in the ctor from the local
    /// time-of-day plus a best-effort username fallback; an async refresh
    /// later in startup upgrades it to the user's real first name when
    /// Windows exposes one. Bound to GreetingText.Text via direct assignment
    /// because the landing area isn't a DataTemplate.</summary>
    public string Greeting { get; private set; }

    public ChatPage()
    {
        // Resolve from the singleton container so the VM's state (active
        // SessionId, in-progress stream, current transcript) survives
        // navigation away and back. This is what makes SessionsPage's
        // "Resume conversation" handoff actually land here visibly.
        ViewModel = App.Services.GetRequiredService<ChatViewModel>();
        Greeting = BuildGreeting(TitleCase(Environment.UserName));

        InitializeComponent();
        GreetingText.Text = Greeting;

        // Fire-and-forget refresh: replace the synchronous fallback name
        // with the user's actual first name once Windows responds.
        _ = RefreshGreetingAsync();

        // First-render hookup. We also attach on OnNavigatedTo, but on the
        // very first construction OnNavigatedTo and the ctor both fire — the
        // _attached guard keeps us from double-subscribing in that case.
        AttachViewModelEvents();
        UpdateSessionLine();
    }

    /// <summary>"Good morning, Amit — let's get something done" — time-of-day
    /// prefix + the supplied name + tagline. Falls back to a generic phrase
    /// if <paramref name="name"/> is empty.</summary>
    private static string BuildGreeting(string name)
    {
        var hour = DateTime.Now.Hour;
        var timeOfDay = hour switch
        {
            < 5 => "Working late",
            < 12 => "Good morning",
            < 17 => "Good afternoon",
            < 21 => "Good evening",
            _ => "Working late",
        };

        return string.IsNullOrWhiteSpace(name)
            ? $"{timeOfDay} — let's get something done"
            : $"{timeOfDay}, {name} — let's get something done";
    }

    private static string TitleCase(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        // Single-pass title-case: capitalize first letter, lowercase rest.
        // Deliberately doesn't try to split "first.last" or "FirstLast"
        // forms — those land cleanly enough as-is for a greeting, and
        // GetFirstNameAsync below will replace this with a real name when
        // Windows exposes one.
        return char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();
    }

    /// <summary>
    /// Looks up the user's actual first name through Windows, replacing the
    /// synchronous username fallback. Three-layer probe with progressively
    /// weaker guarantees:
    ///
    /// <list type="number">
    /// <item><c>Windows.System.User.GetPropertyAsync(KnownUserProperties.FirstName)</c>
    /// — works for users signed in with a Microsoft Account or set up an
    /// account profile; returns the registered first name verbatim.</item>
    /// <item><c>GetUserNameExW(NameDisplay)</c> — Win32 fallback for AD-joined
    /// users; returns "First Last", we split on whitespace.</item>
    /// <item>If both come back empty, leave the title-cased username
    /// fallback in place (don't downgrade what's already on screen).</item>
    /// </list>
    /// </summary>
    private async System.Threading.Tasks.Task RefreshGreetingAsync()
    {
        string? firstName = null;
        try
        {
            // Layer 1: WinRT User API.
            var users = await Windows.System.User.FindAllAsync(
                Windows.System.UserType.LocalUser,
                Windows.System.UserAuthenticationStatus.LocallyAuthenticated);
            foreach (var u in users)
            {
                var value = await u.GetPropertyAsync(Windows.System.KnownUserProperties.FirstName);
                if (value is string s && !string.IsNullOrWhiteSpace(s))
                {
                    firstName = s.Trim();
                    break;
                }
            }
        }
        catch
        {
            // Capability denied / no users found — fall through.
        }

        if (string.IsNullOrEmpty(firstName))
        {
            // Layer 2: Win32 EXTENDED_NAME_FORMAT::NameDisplay.
            firstName = TryGetFirstNameFromWin32();
        }

        if (string.IsNullOrEmpty(firstName)) return;

        var name = firstName!;
        DispatcherQueue.TryEnqueue(() =>
        {
            Greeting = BuildGreeting(name);
            if (GreetingText is not null)
            {
                GreetingText.Text = Greeting;
            }
        });
    }

    /// <summary>
    /// Calls GetUserNameExW with NameDisplay (=3) and returns the part
    /// before the first whitespace. Empty string on any failure — including
    /// the very-common-on-local-accounts case where Windows has no display
    /// name configured and the API returns false with ERROR_NONE_MAPPED.
    /// </summary>
    private static string TryGetFirstNameFromWin32()
    {
        try
        {
            var buffer = new System.Text.StringBuilder(256);
            uint size = (uint)buffer.Capacity;
            if (!NativeMethods.GetUserNameExW(NativeMethods.NameDisplay, buffer, ref size))
            {
                return string.Empty;
            }
            var display = buffer.ToString();
            if (string.IsNullOrWhiteSpace(display)) return string.Empty;
            // Strip any "DOMAIN\" prefix if present, then take everything
            // before the first space. AD users often come back with a
            // domain qualifier; MSA / local profiles don't.
            var slash = display.IndexOf('\\');
            if (slash >= 0) display = display[(slash + 1)..];
            var space = display.IndexOf(' ');
            return space > 0 ? display[..space] : display;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Suggestion-card click: copies the card's Tag prompt into the composer
    /// and focuses the composer for editing. We don't auto-send because most
    /// of the prompts are conversation starters the user typically wants to
    /// finish typing (e.g. "Help me write a Python script that …").
    /// </summary>
    private void Suggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.Tag is not string prompt || prompt.Length == 0) return;

        ViewModel.Composer = prompt;
        // Focus the composer at the end of the inserted text so the user can
        // keep typing immediately. FocusState.Programmatic shows a keyboard
        // caret without the focus-ring chrome that Keyboard would draw.
        Composer.Focus(FocusState.Programmatic);
        Composer.SelectionStart = Composer.Text.Length;
        Composer.SelectionLength = 0;
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
    /// Lifts the TextBox focus signal up to the outer composer pill. The
    /// TextBox's own focus visual (dark fill + thick accent underline) is
    /// suppressed via brush overrides in XAML because it paints over a flat
    /// rectangle that clashes with the pill's rounded corners. Instead we
    /// swap the pill's border to the accent color and bump its thickness so
    /// the user gets a clear, shape-correct focus signal.
    /// </summary>
    private void Composer_GotFocus(object sender, RoutedEventArgs e)
    {
        if (Resources.TryGetValue("AccentFillColorDefaultBrush", out var brush) ||
            Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out brush))
        {
            ComposerPill.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)brush;
        }
        ComposerPill.BorderThickness = new Thickness(2);
    }

    private void Composer_LostFocus(object sender, RoutedEventArgs e)
    {
        if (Resources.TryGetValue("CardStrokeColorDefaultBrush", out var brush) ||
            Application.Current.Resources.TryGetValue("CardStrokeColorDefaultBrush", out brush))
        {
            ComposerPill.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)brush;
        }
        ComposerPill.BorderThickness = new Thickness(1);
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

