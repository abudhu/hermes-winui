using System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Foundation.Collections;
using Windows.Graphics;
using Windows.Storage;

namespace Hermes.App;

/// <summary>
/// Persists the main window's size, position, and maximized state across
/// launches via <see cref="ApplicationData.LocalSettings"/>. Loaded once
/// at startup by <see cref="MainWindow"/>; saved on window Closed.
///
/// <para>Values are stored in PHYSICAL pixels (same units AppWindow uses)
/// rather than logical, so a user on a fixed multi-monitor setup gets the
/// exact same window back. If the user moves to a different-DPI display
/// between sessions the restored size will look off until they resize
/// once — acceptable trade-off for code simplicity.</para>
/// </summary>
internal static class WindowStateManager
{
    // Settings keys are namespaced under "Window." so we never collide
    // with other settings the app might persist later. Changing these
    // keys would silently drop existing users' saved state.
    private const string KeyWidth = "Window.WidthPx";
    private const string KeyHeight = "Window.HeightPx";
    private const string KeyX = "Window.XPx";
    private const string KeyY = "Window.YPx";
    private const string KeyMaximized = "Window.Maximized";

    /// <summary>Anything smaller than this is treated as garbage data and
    /// ignored — guards against zero/negative restoration from a corrupted
    /// or partially-written save. Matches our enforced minimum for the
    /// chat surface to remain usable.</summary>
    private const int MinAcceptableWidthPx = 600;
    private const int MinAcceptableHeightPx = 400;

    /// <summary>
    /// Applies a previously-saved size and position to <paramref name="window"/>.
    /// Falls back to <paramref name="defaultLogicalSize"/> (scaled by the
    /// window's current DPI) when no saved state exists or it's unusable.
    /// </summary>
    public static void Apply(Window window, SizeInt32 defaultLogicalSize)
    {
        try
        {
            var values = ApplicationData.Current.LocalSettings.Values;

            var savedWidth = AsInt(values, KeyWidth);
            var savedHeight = AsInt(values, KeyHeight);
            var savedX = AsInt(values, KeyX);
            var savedY = AsInt(values, KeyY);
            var savedMaximized = values.TryGetValue(KeyMaximized, out var maxObj)
                && maxObj is bool b && b;

            // Decide what size to apply. Saved values win unless they're
            // missing or too small to be a usable window.
            SizeInt32 sizeToApply;
            if (savedWidth >= MinAcceptableWidthPx && savedHeight >= MinAcceptableHeightPx)
            {
                sizeToApply = new SizeInt32(savedWidth, savedHeight);
            }
            else
            {
                sizeToApply = ScaleLogicalToPhysical(window, defaultLogicalSize);
            }

            // Position is more fragile than size — a monitor that was
            // present at save time may be unplugged now, leaving the saved
            // x,y entirely off-screen. Only honour it when a probe point
            // inside the candidate rect lands on a currently-attached
            // display.
            if (savedX != int.MinValue && savedY != int.MinValue
                && IsPositionVisible(savedX, savedY, sizeToApply))
            {
                window.AppWindow.MoveAndResize(new RectInt32(
                    savedX, savedY, sizeToApply.Width, sizeToApply.Height));
            }
            else
            {
                window.AppWindow.Resize(sizeToApply);
            }

            // Maximize last so the presenter remembers our restore size
            // (un-maximizing returns to the size we just set).
            if (savedMaximized && window.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }
        }
        catch
        {
            // Sizing/positioning is cosmetic. Don't crash startup over it.
        }
    }

    /// <summary>
    /// Captures the window's current size, position, and maximized state
    /// to LocalSettings. Wired to <see cref="Window.Closed"/>; safe to call
    /// multiple times (the writes are idempotent).
    /// </summary>
    public static void Save(Window window)
    {
        try
        {
            var aw = window.AppWindow;
            if (aw is null) return;

            var values = ApplicationData.Current.LocalSettings.Values;
            var presenter = aw.Presenter as OverlappedPresenter;
            bool isMaximized = presenter?.State == OverlappedPresenterState.Maximized;
            bool isMinimized = presenter?.State == OverlappedPresenterState.Minimized;

            // When the window is maximized or minimized, AppWindow.Size /
            // .Position report the maximized/minimized values — NOT the
            // restore-target. Writing those would lose the user's preferred
            // restore size. So we update the flag only and leave the size
            // keys alone (which still hold whatever the last unmaximized
            // value was).
            if (isMaximized || isMinimized)
            {
                values[KeyMaximized] = isMaximized;
                return;
            }

            values[KeyWidth] = aw.Size.Width;
            values[KeyHeight] = aw.Size.Height;
            values[KeyX] = aw.Position.X;
            values[KeyY] = aw.Position.Y;
            values[KeyMaximized] = false;
        }
        catch
        {
            // Persistence is best-effort.
        }
    }

    /// <summary>Returns the stored int value or <see cref="int.MinValue"/>
    /// for missing/wrong-type keys. <see cref="int.MinValue"/> is used as
    /// the sentinel instead of a nullable because LocalSettings boxes
    /// every value type and the unbox-via-pattern gets noisy at call sites.</summary>
    private static int AsInt(IPropertySet values, string key)
    {
        if (values.TryGetValue(key, out var obj) && obj is int i) return i;
        return int.MinValue;
    }

    private static SizeInt32 ScaleLogicalToPhysical(Window window, SizeInt32 logical)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        uint dpi = NativeMethods.GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96; // GetDpiForWindow returns 0 on failure
        double scale = dpi / 96.0;
        return new SizeInt32(
            (int)Math.Round(logical.Width * scale),
            (int)Math.Round(logical.Height * scale));
    }

    /// <summary>
    /// True when a probe point inside the candidate window rect lands on a
    /// currently-attached display. Cheaper than checking all four corners
    /// and good enough to detect "monitor unplugged" — if any reasonable
    /// portion of the title bar is visible the user can grab and move the
    /// window, which is the real failure mode we're guarding against.
    /// </summary>
    private static bool IsPositionVisible(int x, int y, SizeInt32 size)
    {
        // Sample 32px inside the top-left so we don't pick a point on
        // the edge that lands in a monitor gap.
        var probe = new PointInt32(
            x + Math.Min(32, size.Width / 2),
            y + Math.Min(32, size.Height / 2));
        var area = DisplayArea.GetFromPoint(probe, DisplayAreaFallback.None);
        return area is not null;
    }
}
