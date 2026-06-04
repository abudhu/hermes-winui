using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Hermes.App.Pages.Chat;
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
    /// <summary>True while we're subscribed to ViewModel events. Tracks
    /// attach/detach across OnNavigatedTo / OnNavigatedFrom so we don't
    /// double-subscribe (the VM is a DI singleton; the page is recreated
    /// on every navigation and would otherwise pile on handlers).</summary>
    private bool _attached;

    /// <summary>Monotonically incremented every time a scroll-on-Add is
    /// scheduled. Each scheduled callback captures the value at the time
    /// it was queued and bails if a later one has superseded it. This
    /// coalesces the back-to-back User+Assistant Adds of a send flow
    /// into a single executed scroll — without this, BringIntoView fires
    /// twice and the first call (before layout settles) snaps to a
    /// slightly-off position which then jumps when the second call
    /// corrects it. Visible as a small flicker.</summary>
    private int _scrollSeq;

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
        Greeting = GreetingProvider.BuildInitialGreeting();

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

        // Ctrl+/ → focus composer. The VirtualKey enum doesn't expose
        // Oem2 (the slash key, VK 0xBF=191) so we can't wire this from
        // XAML; the accelerators are built here at runtime instead. Same
        // wiring covers the numpad Divide key. Page-scoped — only fires
        // while ChatPage is the loaded content.
        //
        // IMPORTANT: must defer to Loaded. Calling KeyboardAccelerators.Add
        // from the page ctor — before the page is hosted in a Frame and
        // therefore before it has a XamlRoot — crashes WinUI 3 with a
        // stowed combase exception (HRESULT 0x802b000a, FACILITY_XAML) at
        // app startup with no managed stack. We learned this the hard way
        // (see commit history); do not move these back into the ctor.
        Loaded += ChatPage_AttachAccelerators;
    }

    private bool _acceleratorsAttached;

    private void ChatPage_AttachAccelerators(object sender, RoutedEventArgs e)
    {
        if (_acceleratorsAttached) return;
        _acceleratorsAttached = true;

        var slashAccel = new KeyboardAccelerator
        {
            Key = (VirtualKey)0xBF,  // VK_OEM_2 — the `/?` key on US layouts
            Modifiers = VirtualKeyModifiers.Control,
        };
        slashAccel.Invoked += FocusComposer_Invoked;
        KeyboardAccelerators.Add(slashAccel);

        var divideAccel = new KeyboardAccelerator
        {
            Key = VirtualKey.Divide,
            Modifiers = VirtualKeyModifiers.Control,
        };
        divideAccel.Invoked += FocusComposer_Invoked;
        KeyboardAccelerators.Add(divideAccel);
    }

    /// <summary>
    /// Awaits the WinRT/Win32 first-name probe and, if a real name comes
    /// back, hops to the UI thread to swap in the upgraded greeting. The
    /// greeting string itself is rebuilt <em>inside</em> the dispatcher
    /// callback so the time-of-day evaluation happens when the UI update
    /// actually runs (matters if the probe takes long enough to cross a
    /// boundary like 5/12/17/21 o'clock).
    /// </summary>
    private async System.Threading.Tasks.Task RefreshGreetingAsync()
    {
        var firstName = await GreetingProvider.GetFirstNameAsync();
        if (string.IsNullOrEmpty(firstName)) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            Greeting = GreetingProvider.BuildGreeting(firstName);
            if (GreetingText is not null)
            {
                GreetingText.Text = Greeting;
            }
        });
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

        // Kick off the model-list fetch the first time the chat page lands.
        // Coalesces internally — subsequent navigations don't re-fetch unless
        // the prior load failed. Discard the task; LoadModelsAsync catches
        // exceptions so this can't surface as an unobserved-task crash.
        _ = ViewModel.EnsureModelsLoadedAsync();
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

    private void ViewModel_MessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateSessionLine();

        // We care about two flavors of Add:
        //
        //   1. Send flow — two Adds land back-to-back: the User message
        //      and the Assistant placeholder (State == Streaming).
        //      We want to pin the User's message to the TOP of the
        //      viewport so the user can read both their prompt AND
        //      watch the assistant stream in underneath. This is the
        //      ChatGPT / Claude pattern and it's more robust than
        //      snap-to-bottom because the bottom target moves out from
        //      under us as the assistant grows.
        //
        //   2. Anything else (resume hydration, lone system messages,
        //      etc.) — snap to bottom so the most recent content is
        //      visible. Acceptable to fire N times during hydration:
        //      each ChangeView is cheap and the last one wins.
        //
        // Both branches use a Low-priority dispatcher hop so layout
        // can settle (especially the user bubble's MarkdownPresenter
        // measure pass) before we ask for positions — otherwise the
        // user's bubble height is still the placeholder size and the
        // scroll lands a hundred-odd pixels short of where we want.
        //
        // NOT triggered by streaming content updates — those bump
        // PropertyChanged on the existing MessageVm, not the
        // collection. The user keeps full control of the viewport
        // mid-stream and can scroll wherever they want without being
        // yanked back.
        if (e.Action != NotifyCollectionChangedAction.Add) return;

        // Bump the sequence so any in-flight callback from a prior Add
        // (e.g. the User Add of a User+Assistant send pair) no-ops out
        // and only the latest one actually scrolls.
        var seq = ++_scrollSeq;

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (seq != _scrollSeq) return; // superseded by a later Add

            var msgs = ViewModel.Messages;
            if (msgs.Count == 0) return;

            // Late-check the send-flow pattern: by the time this
            // callback runs both Adds have landed, so msgs[last] is
            // the assistant placeholder and msgs[last-1] is the user
            // message.
            if (msgs.Count >= 2)
            {
                var last = msgs[msgs.Count - 1];
                var prev = msgs[msgs.Count - 2];
                if (last.Role == MessageRole.Assistant
                    && last.State == MessageState.Streaming
                    && prev.Role == MessageRole.User)
                {
                    var userElement = MessagesRepeater
                        .TryGetElement(msgs.Count - 2) as FrameworkElement;
                    userElement?.StartBringIntoView(new BringIntoViewOptions
                    {
                        AnimationDesired = false,
                        VerticalAlignmentRatio = 0,
                        // Tiny breathing room so the user bubble's
                        // role/timestamp header isn't flush with the
                        // ScrollViewer's top padding.
                        VerticalOffset = -8,
                    });
                    return;
                }
            }

            // Default: snap to bottom.
            TranscriptScroll.ChangeView(
                null, TranscriptScroll.ScrollableHeight, null,
                disableAnimation: true);
        });
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.SessionId) ||
            e.PropertyName == nameof(ChatViewModel.SessionTitle))
        {
            UpdateSessionLine();
        }
    }

    /// <summary>
    /// Keep the BottomSpacer sized to roughly the viewport so the "scroll
    /// latest user message to top of viewport" logic in
    /// <see cref="ViewModel_MessagesChanged"/> always has enough scroll-room
    /// to land cleanly at the top. Without this spacer, a fresh assistant
    /// placeholder (~30 px "thinking" label) doesn't add enough content to
    /// make the target offset reachable, and the user's message ends up
    /// somewhere in the middle of the viewport instead of pinned to the top.
    /// </summary>
    private void TranscriptScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (BottomSpacer is null) return;
        var vh = TranscriptScroll.ViewportHeight;
        // Subtract a small breathing margin so there's always a bit of
        // empty space below the last message rather than the latest bubble
        // sitting flush with the bottom of the spacer area.
        var target = Math.Max(200, vh - 80);
        if (Math.Abs(BottomSpacer.Height - target) > 0.5)
        {
            BottomSpacer.Height = target;
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
    /// Ctrl+/ accelerator handler. Focuses the composer text box and puts
    /// the caret at the end so the user can start typing without an extra
    /// keystroke. Page-scoped — only fires while ChatPage is loaded, so
    /// Ctrl+/ on other pages naturally no-ops.
    /// </summary>
    private void FocusComposer_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        FocusComposer();
        args.Handled = true;
    }

    /// <summary>Public hook so MainWindow can forward focus requests
    /// (e.g. from a palette command) without needing to know which
    /// element on the page is the composer.</summary>
    public void FocusComposer()
    {
        if (Composer is null) return;
        Composer.Focus(FocusState.Programmatic);
        Composer.SelectionStart = Composer.Text?.Length ?? 0;
        Composer.SelectionLength = 0;
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
}

