using System;
using Hermes.ApiClient;
using Hermes.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

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

    /// <summary>
    /// Wired up in <see cref="Program.Main"/> for the primary instance.
    /// Fires every time a second launch attempt is redirected to us — e.g.
    /// the tray's "Open Hermes app" entry or the user double-clicking the
    /// Start tile while the app is already running.
    ///
    /// <para>The event ALWAYS arrives on a thread-pool thread, so we have to
    /// hop to the UI dispatcher before touching the window. <c>AppInstance.RedirectActivationToAsync</c>
    /// transfers foreground privileges to us, so <c>SetForegroundWindow</c>
    /// won't be blocked by the foreground-lock; we still call it explicitly
    /// because <see cref="Window.Activate"/> alone doesn't reliably steal
    /// focus from another foreground process in WinUI 3.</para>
    /// </summary>
    internal static void OnInstanceActivated(object? sender, AppActivationArguments args)
    {
        var window = MainWindow;
        if (window is null) return;

        window.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                // If the window is minimized, lift it out of the taskbar.
                // OverlappedPresenter exposes the WM_SYSCOMMAND restore that
                // would otherwise require P/Invoke.
                if (window.AppWindow?.Presenter is OverlappedPresenter presenter
                    && presenter.State == OverlappedPresenterState.Minimized)
                {
                    presenter.Restore();
                }
                window.AppWindow?.Show();
                window.Activate();

                // Belt-and-braces: explicitly bring the HWND to the front.
                // WinUI 3 Activate() is a no-op if the window is already in
                // the activated state from the OS's perspective but visually
                // buried behind another window — which is exactly the case
                // we're handling here.
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                if (hwnd != IntPtr.Zero)
                {
                    NativeMethods.SetForegroundWindow(hwnd);
                }
            }
            catch
            {
                // Best-effort focus — if any of this throws (e.g. window is
                // mid-close) we don't want to crash the entire process from
                // a background-thread callback.
            }
        });
    }
}

/// <summary>P/Invoke surface limited to what
/// <see cref="App.OnInstanceActivated"/> needs.</summary>
internal static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(
        System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [return: System.Runtime.InteropServices.MarshalAs(
        System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>Returns the dots-per-inch (DPI) value for the specified
    /// window. 96 == 100% scaling. Used by MainWindow to convert a
    /// logical-pixel default size into physical pixels for AppWindow.Resize.</summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(
        System.Runtime.InteropServices.DllImportSearchPath.System32)]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    /// <summary>
    /// Returns the user's display name as Windows knows it (full name
    /// for Microsoft-Account-linked users, "DOMAIN\First Last" or just
    /// "First Last" for AD users). Returns an empty string for pure
    /// local accounts that have no display name configured. P/Invoke
    /// signature follows the EXTENDED_NAME_FORMAT::NameDisplay (=3)
    /// variant of <c>GetUserNameEx</c>.
    /// </summary>
    [System.Runtime.InteropServices.DllImport(
        "secur32.dll",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        SetLastError = true)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(
        System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [return: System.Runtime.InteropServices.MarshalAs(
        System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool GetUserNameExW(
        int nameFormat,
        System.Text.StringBuilder lpNameBuffer,
        ref uint nSize);

    public const int NameDisplay = 3;
}

