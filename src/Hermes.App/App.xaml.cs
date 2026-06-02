using System;
using Hermes.ApiClient;
using Microsoft.Extensions.DependencyInjection;
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
    private Window? _window;

    /// <summary>
    /// DI root. Exposed statically because WinUI 3 instantiates pages
    /// via the parameterless ctor that <c>Frame.Navigate</c> requires —
    /// pages pull dependencies through this in their own constructors.
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

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

        return services.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
