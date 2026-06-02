using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Hermes.TrayApp;

/// <summary>
/// Renders the tray icon at runtime as a filled colored circle with an optional
/// "busy" inner dot. Icons are cached per (status, busy) tuple because:
///   1. Creating GDI+ resources every poll tick would be wasteful.
///   2. NotifyIcon does not take ownership of the Icon — replacing it without
///      disposing leaks GDI handles. Caching means we own the lifetime cleanly
///      and dispose them all in <see cref="Dispose"/>.
/// </summary>
public sealed class IconRenderer : IDisposable
{
    public enum Status { Unknown, Healthy, Degraded, Down }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private readonly ConcurrentDictionary<(Status, bool), Icon> _cache = new();
    private readonly int _size;

    public IconRenderer()
    {
        // Match the system's effective small-icon size for crisp tray rendering.
        // SystemInformation.SmallIconSize honors DPI scaling.
        var sz = SystemInformation.SmallIconSize;
        _size = Math.Max(16, Math.Min(sz.Width, sz.Height));
    }

    public Icon Get(Status status, bool busy) =>
        _cache.GetOrAdd((status, busy), key => Render(key.Item1, key.Item2));

    private Icon Render(Status status, bool busy)
    {
        var fill = status switch
        {
            Status.Healthy  => Color.FromArgb(46, 204, 113),  // green
            Status.Degraded => Color.FromArgb(241, 196, 15),  // amber
            Status.Down     => Color.FromArgb(231, 76, 60),   // red
            _               => Color.FromArgb(149, 165, 166), // gray
        };

        // Render at 2x for supersampling, then resize. Tray icons are tiny;
        // any DPI / antialiasing artifact looks bad and noticing it is half
        // the user's interaction with the app.
        var renderSize = _size * 2;
        using var hi = new Bitmap(renderSize, renderSize);
        using (var g = Graphics.FromImage(hi))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;

            var pad = renderSize / 8f;
            var rect = new RectangleF(pad, pad, renderSize - 2 * pad, renderSize - 2 * pad);

            using var brush = new SolidBrush(fill);
            g.FillEllipse(brush, rect);

            // Soft dark outline for contrast on both light + dark taskbars.
            using var pen = new Pen(Color.FromArgb(96, 0, 0, 0), Math.Max(1f, renderSize / 32f));
            g.DrawEllipse(pen, rect);

            if (busy)
            {
                // Inner white dot indicates "agent is doing something right now"
                var inner = renderSize * 0.35f;
                var ix = (renderSize - inner) / 2f;
                var iy = (renderSize - inner) / 2f;
                using var innerBrush = new SolidBrush(Color.FromArgb(240, 255, 255, 255));
                g.FillEllipse(innerBrush, ix, iy, inner, inner);
            }
        }

        using var bmp = new Bitmap(hi, new Size(_size, _size));
        var handle = bmp.GetHicon();
        // Icon.FromHandle does NOT take ownership; we must destroy the icon
        // handle when disposing the cache so we don't leak USER objects.
        return Icon.FromHandle(handle);
    }

    public void Dispose()
    {
        foreach (var icon in _cache.Values)
        {
            var handle = icon.Handle;
            icon.Dispose();
            DestroyIcon(handle);
        }
        _cache.Clear();
    }
}
