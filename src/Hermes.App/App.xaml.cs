using System;
using Hermes.ApiClient;
using Hermes.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Hermes.App;

/// <summary>
/// Application entry point. Builds a process-wide service container so
/// pages and view-models can resolve the shared <see cref="HermesApiClient"/>
/// (which owns the bearer-authenticated HttpClient against the local gateway)
/// rather than each constructing its own.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// DI root. Exposed statically because WinUI 3 instantiates pages
    /// via the parameterless ctor that <c>Frame.Navigate</c> requires —
    /// pages pull dependencies through this in their own constructors.
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>The single top-level window. Set in <see cref="OnLaunched"/>.
    /// Other pages reach it for cross-page navigation (e.g. SessionsPage's
    /// "Resume conversation" button switches the NavigationView selection).
    /// Single-window app, so a static handle is fine.</summary>
    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        Services = ConfigureServices();
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // HermesConfig reads ~/.hermes/.env (or $HERMES_HOME). Doing it once
        // at startup means changes to the .env require an app restart, which
        // is fine — the gateway itself only reads the file on launch.
        services.AddSingleton(HermesConfig.Load());

        // HermesApiClient owns an HttpClient with a 6s timeout. Safe to share
        // process-wide for the polling/dashboard endpoints. The chat surface
        // will get its own streaming client in Phase 2b — that one needs
        // Timeout.InfiniteTimeSpan for SSE.
        services.AddSingleton<HermesApiClient>();

        // Streaming client uses a separate HttpClient with infinite timeout —
        // sharing the polling client's 6s timeout would kill SSE streams in
        // 6 seconds flat.
        services.AddSingleton<HermesStreamingClient>();

        // App() ctor runs on the UI thread in WinUI 3 packaged apps, so
        // GetForCurrentThread returns the UI dispatcher. Capture it now and
        // hand it to ChatViewModel — the ChatViewModel singleton needs to
        // marshal streaming-worker callbacks back to UI, and we can't safely
        // call GetForCurrentThread from a worker thread.
        var uiDispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException(
                "App() ctor was not invoked on the UI thread; could not capture DispatcherQueue.");
        services.AddSingleton(uiDispatcher);

        // ChatViewModel is a singleton so its state (active SessionId, current
        // transcript) survives navigation away from ChatPage and back — which
        // is required for SessionsPage's "Resume conversation" handoff to work.
        services.AddSingleton<ChatViewModel>();

        return services.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}

