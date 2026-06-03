using System;
using Hermes.App.Pages;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace Hermes.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");
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

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
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
            "home"     => typeof(HomePage),
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

