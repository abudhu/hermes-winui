using System;
using Hermes.App.Pages;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    private async System.Threading.Tasks.Task<bool> PromptDiscardSettingsAsync()
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
}

