using System;
using System.Threading.Tasks;
using Hermes.ApiClient;
using Hermes.App.Pages;
using Hermes.App.Services;
using Hermes.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;

namespace Hermes.App;

public sealed partial class MainWindow : Window
{
    /// <summary>Initial window size in logical (DIP) pixels, used only when
    /// no saved state exists from a prior session. Sized to feel at home on
    /// a 1080p laptop without dominating the screen — the chat surface still
    /// gets ~880px wide with the 220px nav rail visible. <see cref="WindowStateManager"/>
    /// converts this to physical pixels using the current window's DPI before
    /// calling <see cref="AppWindow.Resize"/>.</summary>
    private static readonly SizeInt32 DefaultLogicalSize = new(1100, 720);

    /// <summary>True while the palette OR any guarded ContentDialog (e.g.
    /// the Settings discard prompt) is open. App-wide accelerators that
    /// would otherwise stack a second modal check this and bail.</summary>
    private bool _modalOpen;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Restore size/position from the prior session (falls back to
        // DefaultLogicalSize when this is a fresh install).
        WindowStateManager.Apply(this, DefaultLogicalSize);

        // Save on close. Window.Closed fires for normal close paths
        // (X button, Alt+F4, programmatic Close); process kill or crash
        // skips it, which means we lose changes since the last save —
        // acceptable trade-off for not having to debounce on every drag.
        Closed += (_, _) => WindowStateManager.Save(this);

        // Palette wiring. Registry is built once — its delegates close over
        // `this`, so the same registry stays valid for the life of the
        // window. Static command set never changes; dynamic data is fetched
        // fresh per open.
        var api = App.Services.GetRequiredService<HermesApiClient>();
        var chat = App.Services.GetRequiredService<ChatViewModel>();
        var host = new CommandRegistry.PaletteHost
        {
            NavigateToChat = () => TryNavigateAsync("chat"),
            NavigateToSettings = () => TryNavigateAsync("settings"),
            NavigateToJobs = () => TryNavigateAsync("jobs"),
            ResumeSession = async sessionId =>
            {
                // Guard the nav BEFORE mutating ChatViewModel state — if the
                // user has unsaved Settings and chooses "Keep editing", we
                // don't want to silently swap their active chat out from
                // under them.
                if (!await TryNavigateAsync("chat")) return;
                try
                {
                    await chat.ResumeSessionAsync(sessionId);
                }
                catch
                {
                    // ResumeSessionAsync surfaces its own errors via
                    // ChatViewModel.StatusText. Nothing useful to do here.
                }
            },
        };
        Palette.Initialize(new CommandRegistry(api, chat, host));
        Palette.CommandInvoked += Palette_CommandInvoked;

        // Ctrl+, — VirtualKey doesn't expose Oem-Comma (VK 0xBC=188) as a
        // named enum value, so the accelerator is wired at runtime against
        // the numeric code. Lives on the root Grid via the same collection
        // the XAML accelerators use, so scoping behavior is identical.
        var commaAccel = new KeyboardAccelerator
        {
            Key = (Windows.System.VirtualKey)0xBC,  // VK_OEM_COMMA
            Modifiers = Windows.System.VirtualKeyModifiers.Control,
        };
        commaAccel.Invoked += CtrlComma_Invoked;
        RootGrid.KeyboardAccelerators.Add(commaAccel);
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        if (NavFrame.CanGoBack)
        {
            NavFrame.GoBack();
        }
    }

    /// <summary>Switches the side nav to the "Chat" item. The NavView's
    /// own <c>SelectionChanged</c> event then routes the user to ChatPage
    /// through the same code path as a click. Used by SessionsPage's
    /// "Resume conversation" handoff.</summary>
    public void NavigateToChat()
    {
        foreach (var item in NavView.MenuItems)
        {
            if (item is NavigationViewItem nvi && (nvi.Tag as string) == "chat")
            {
                NavView.SelectedItem = nvi;
                return;
            }
        }
    }

    private bool _suppressNavGuard;

    private async void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressNavGuard) return;

        // Dirty-state guard: if we're leaving Settings with unsaved changes
        // in either pane, prompt before discarding. WinUI 3 NavView has no
        // pre-change Cancel hook, so we restore the selection ourselves
        // when the user backs out.
        if (NavFrame.Content is SettingsPage settings && settings.HasUnsavedChanges)
        {
            var leavingSettings = args.IsSettingsSelected
                ? false  // staying on Settings — no prompt
                : true;
            if (leavingSettings)
            {
                var prevSelection = sender.SelectedItem;
                var keepEditing = await PromptDiscardSettingsAsync();
                if (keepEditing)
                {
                    // Restore selection without re-triggering the guard.
                    _suppressNavGuard = true;
                    try
                    {
                        sender.SelectedItem = sender.SettingsItem;
                    }
                    finally
                    {
                        _suppressNavGuard = false;
                    }
                    return;
                }
            }
        }

        if (args.IsSettingsSelected)
        {
            Navigate(typeof(SettingsPage), args.RecommendedNavigationTransitionInfo);
            return;
        }

        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
        {
            return;
        }

        var target = tag switch
        {
            "chat"     => typeof(ChatPage),
            "sessions" => typeof(SessionsPage),
            "memories" => typeof(MemoriesPage),
            "skills"   => typeof(SkillsPage),
            "jobs"     => typeof(JobsPage),
            "bridges"  => typeof(BridgesPage),
            _ => throw new InvalidOperationException($"Unknown navigation tag: {tag}"),
        };

        Navigate(target, args.RecommendedNavigationTransitionInfo);
    }

    /// <summary>Returns true if the user chose to stay on the Settings
    /// page (discarding the navigation), false if they chose to discard
    /// their unsaved edits and proceed.</summary>
    private async Task<bool> PromptDiscardSettingsAsync()
    {
        // Mark a modal as open so Ctrl+K / other accelerators can't stack
        // a second dialog on top of this one.
        _modalOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Discard unsaved settings?",
                Content = "You have unsaved changes on the Settings page. Leaving will discard them.",
                PrimaryButtonText = "Discard changes",
                CloseButtonText = "Keep editing",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = NavView.XamlRoot,
            };
            var result = await dialog.ShowAsync();
            return result != ContentDialogResult.Primary;  // Primary = discard
        }
        finally
        {
            _modalOpen = false;
        }
    }

    private void Navigate(Type pageType, NavigationTransitionInfo transition)
    {
        // Skip if we're already on the requested page — avoids re-creating the
        // page (and its ViewModel) every time the user clicks the same item.
        if (NavFrame.CurrentSourcePageType == pageType)
        {
            return;
        }

        NavFrame.Navigate(pageType, null, transition);
    }

    // ----- Centralized guarded navigation --------------------------------

    /// <summary>
    /// Routes palette / shortcut navigation through the same Settings
    /// unsaved-changes guard that NavView_SelectionChanged uses for
    /// clicks. Without this, Ctrl+, from a dirty Settings page would
    /// just silently no-op (we'd re-navigate to Settings) and Ctrl+N
    /// would skip the discard prompt entirely.
    ///
    /// <para>Returns true if the navigation actually happened, false if
    /// the user backed out at the discard prompt.</para>
    /// </summary>
    private async Task<bool> TryNavigateAsync(string tag)
    {
        if (NavFrame.Content is SettingsPage settings && settings.HasUnsavedChanges
            && tag != "settings")
        {
            var keepEditing = await PromptDiscardSettingsAsync();
            if (keepEditing) return false;
        }

        if (tag == "settings")
        {
            // Use the NavView's SettingsItem when available so its selection
            // visual stays in sync. Fall back to direct Frame nav if the
            // gear was hidden (it isn't today, but defend against future
            // changes to the XAML).
            if (NavView.SettingsItem is NavigationViewItem settingsItem)
            {
                NavView.SelectedItem = settingsItem;
            }
            else
            {
                Navigate(typeof(SettingsPage), new EntranceNavigationTransitionInfo());
            }
            return true;
        }

        foreach (var item in NavView.MenuItems)
        {
            if (item is NavigationViewItem nvi && (nvi.Tag as string) == tag)
            {
                NavView.SelectedItem = nvi;
                return true;
            }
        }
        return false;
    }

    // ----- Keyboard accelerator handlers ---------------------------------

    private void CtrlK_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Don't stack the palette on itself, and don't open it on top of a
        // discard prompt or other modal — that combo isn't allowed in WinUI
        // ("one ContentDialog per XamlRoot" — palette is a Popup but the
        // user typing past a hidden dialog would be confusing).
        if (Palette.IsOpen || _modalOpen)
        {
            args.Handled = true;
            return;
        }

        Palette.Open(RootGrid);
        args.Handled = true;
    }

    private async void CtrlN_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_modalOpen || Palette.IsOpen) { args.Handled = true; return; }
        args.Handled = true;

        var navigated = await TryNavigateAsync("chat");
        if (!navigated) return;

        var chat = App.Services.GetRequiredService<ChatViewModel>();
        if (chat.NewChatCommand.CanExecute(null))
        {
            chat.NewChatCommand.Execute(null);
        }
    }

    private void CtrlW_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_modalOpen || Palette.IsOpen) { args.Handled = true; return; }

        // Spec: "Close current chat (back to no-chat-selected state) — or
        // do nothing if not on a chat page". NewChatCommand clears messages
        // + session id, which IS the "no chat selected" state.
        if (NavFrame.Content is ChatPage)
        {
            var chat = App.Services.GetRequiredService<ChatViewModel>();
            if (chat.NewChatCommand.CanExecute(null))
            {
                chat.NewChatCommand.Execute(null);
            }
            args.Handled = true;
        }
        // Else: deliberately don't mark Handled — let the key bubble in case
        // something else (e.g. a hypothetical future Ctrl+W consumer) wants
        // it. Today nothing does and this is effectively a no-op.
    }

    private async void CtrlComma_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_modalOpen || Palette.IsOpen) { args.Handled = true; return; }
        args.Handled = true;
        await TryNavigateAsync("settings");
    }

    private void Escape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Esc is sensitive — TextBoxes, AutoSuggestBox dropdowns, ComboBox
        // popups, and the palette all want it for "back out of what I'm
        // doing right now". Only consume Esc when:
        //   1. No modal is open (palette dismissal is the Popup's job).
        //   2. The user is on ChatPage AND the stream is actively running.
        // Anything else: leave Esc alone so the default consumer handles it.
        if (_modalOpen || Palette.IsOpen) return;
        if (NavFrame.Content is not ChatPage) return;

        var chat = App.Services.GetRequiredService<ChatViewModel>();
        if (chat.StopCommand.CanExecute(null))
        {
            chat.StopCommand.Execute(null);
            args.Handled = true;
        }
    }

    private void CtrlE_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // TODO: bind to ChatViewModel.CopyAsMarkdownCommand when the
        // parallel export sub-session lands it. Today the command doesn't
        // exist (grep -n on ChatViewModel.cs at HEAD returns no match);
        // shipping with the accelerator wired but no-op keeps the contract
        // visible without breaking the build if the command name ends up
        // different ("SaveAsMarkdownCommand", "ExportMarkdownCommand", …).
        //
        // Once it lands:
        //   if (NavFrame.Content is ChatPage) {
        //       var chat = App.Services.GetRequiredService<ChatViewModel>();
        //       if (chat.CopyAsMarkdownCommand.CanExecute(null))
        //           chat.CopyAsMarkdownCommand.Execute(null);
        //       args.Handled = true;
        //   }
    }

    /// <summary>Called by ChatPage when its Ctrl+/ accelerator fires. Page
    /// is responsible for actually focusing its TextBox — this method is
    /// only here so cross-page Ctrl+/ requests from elsewhere (future
    /// palette command?) have a single entry point.</summary>
    public void FocusComposerIfChat()
    {
        if (NavFrame.Content is ChatPage chatPage)
        {
            chatPage.FocusComposer();
        }
    }

    // ----- Palette invocation --------------------------------------------

    private async void Palette_CommandInvoked(object? sender, PaletteCommand cmd)
    {
        try
        {
            await cmd.Invoke();
        }
        catch
        {
            // Individual command lambdas swallow their own errors; this
            // is a backstop so a misbehaving lambda doesn't crash the
            // dispatcher thread.
        }
    }
}



