namespace Apparatus.Core.Capture;

/// <summary>A rectangle in whatever unit the caller works in (screen pixels for frames, DIPs inside an overlay window).</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;

    /// <summary>The smallest rectangle that holds all of them (the whole virtual desktop, for the monitors).</summary>
    public static RectD Union(IEnumerable<RectD> rects)
    {
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
        bool any = false;
        foreach (var rect in rects)
        {
            any = true;
            l = Math.Min(l, rect.X); t = Math.Min(t, rect.Y);
            r = Math.Max(r, rect.Right); b = Math.Max(b, rect.Bottom);
        }
        return any ? new RectD(l, t, r - l, b - t) : default;
    }

    public static RectD FromCorners(double x1, double y1, double x2, double y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));

    /// <summary>The part of this rectangle that lies inside <paramref name="bounds"/> (empty when they do not meet).</summary>
    public RectD ClampTo(RectD bounds)
    {
        double l = Math.Max(X, bounds.X), t = Math.Max(Y, bounds.Y);
        double r = Math.Min(Right, bounds.Right), b = Math.Min(Bottom, bounds.Bottom);
        return r <= l || b <= t ? new RectD(l, t, 0, 0) : new RectD(l, t, r - l, b - t);
    }
}

/// <summary>
/// What is captured. A screenshot grabs one frame of a target; the screen recording planned for later grabs a stream of frames of the
/// same kinds of target, so both go through <see cref="IFrameSource"/>.
/// </summary>
public abstract record CaptureTarget;

/// <summary>A rectangle of the virtual desktop, in physical pixels.</summary>
public sealed record RegionTarget(RectD Area) : CaptureTarget;

/// <summary>One monitor, by the position of its top-left corner on the virtual desktop.</summary>
public sealed record MonitorTarget(int Index) : CaptureTarget;

/// <summary>The visible area of one window (its frame, without the invisible resize borders).</summary>
public sealed record WindowTarget(nint Handle) : CaptureTarget;

/// <summary>One grabbed image: top-down BGRA, 4 bytes per pixel, <see cref="Width"/> x <see cref="Height"/>.</summary>
public sealed record CapturedFrame(RectD Area, int Width, int Height, byte[] Bgra);

/// <summary>A monitor as the overlay needs it: where it is on the desktop (physical pixels) and its scale.</summary>
public sealed record MonitorInfoData(int Index, RectD Bounds, double Scale, bool IsPrimary);

public interface IFrameSource
{
    IReadOnlyList<MonitorInfoData> Monitors();

    /// <summary>Grabs the target once. Throws when the screen cannot be read (for example a locked desktop).</summary>
    CapturedFrame Grab(CaptureTarget target);
}

/// <summary>The eight handles of a selection plus "inside" (move) and "outside".</summary>
public enum SelectionHandle { None, Inside, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left }

/// <summary>Pure geometry of the selection rectangle: hit testing, moving and resizing, always kept inside the monitor.</summary>
public static class SelectionGeometry
{
    public const double MinSize = 8;

    /// <summary>Which handle (or the inside) is under the point. <paramref name="grab"/> is the reach of a handle around its position.</summary>
    public static SelectionHandle HitTest(RectD sel, double x, double y, double grab)
    {
        if (sel.IsEmpty) return SelectionHandle.None;
        bool left = Math.Abs(x - sel.X) <= grab, right = Math.Abs(x - sel.Right) <= grab;
        bool top = Math.Abs(y - sel.Y) <= grab, bottom = Math.Abs(y - sel.Bottom) <= grab;
        bool withinX = x >= sel.X - grab && x <= sel.Right + grab, withinY = y >= sel.Y - grab && y <= sel.Bottom + grab;

        if (left && top) return SelectionHandle.TopLeft;
        if (right && top) return SelectionHandle.TopRight;
        if (right && bottom) return SelectionHandle.BottomRight;
        if (left && bottom) return SelectionHandle.BottomLeft;
        if (top && withinX) return SelectionHandle.Top;
        if (bottom && withinX) return SelectionHandle.Bottom;
        if (left && withinY) return SelectionHandle.Left;
        if (right && withinY) return SelectionHandle.Right;
        return sel.Contains(x, y) ? SelectionHandle.Inside : SelectionHandle.None;
    }

    /// <summary>The rectangle after dragging <paramref name="handle"/> of <paramref name="start"/> by (dx, dy); never smaller than <see cref="MinSize"/>, never outside <paramref name="bounds"/>.</summary>
    public static RectD Drag(RectD start, SelectionHandle handle, double dx, double dy, RectD bounds)
    {
        if (handle == SelectionHandle.Inside)
        {
            double x = Math.Clamp(start.X + dx, bounds.X, bounds.Right - start.Width);
            double y = Math.Clamp(start.Y + dy, bounds.Y, bounds.Bottom - start.Height);
            return new RectD(x, y, start.Width, start.Height);
        }

        double l = start.X, t = start.Y, r = start.Right, b = start.Bottom;
        if (handle is SelectionHandle.TopLeft or SelectionHandle.Left or SelectionHandle.BottomLeft) l += dx;
        if (handle is SelectionHandle.TopRight or SelectionHandle.Right or SelectionHandle.BottomRight) r += dx;
        if (handle is SelectionHandle.TopLeft or SelectionHandle.Top or SelectionHandle.TopRight) t += dy;
        if (handle is SelectionHandle.BottomLeft or SelectionHandle.Bottom or SelectionHandle.BottomRight) b += dy;

        l = Math.Clamp(l, bounds.X, bounds.Right);
        r = Math.Clamp(r, bounds.X, bounds.Right);
        t = Math.Clamp(t, bounds.Y, bounds.Bottom);
        b = Math.Clamp(b, bounds.Y, bounds.Bottom);

        // Dragging a side past the opposite one flips the rectangle, like every selection tool does.
        var rect = RectD.FromCorners(l, t, r, b);
        double w = Math.Max(rect.Width, MinSize), h = Math.Max(rect.Height, MinSize);
        double nx = Math.Min(rect.X, bounds.Right - w), ny = Math.Min(rect.Y, bounds.Bottom - h);
        return new RectD(Math.Max(nx, bounds.X), Math.Max(ny, bounds.Y), w, h);
    }

    /// <summary>Where the toolbar goes: under the selection, right edge aligned; above it, or inside it, when there is no room below.</summary>
    public static (double X, double Y) ToolbarPosition(RectD sel, double barWidth, double barHeight, RectD bounds, double gap = 8)
    {
        double x = Math.Clamp(sel.Right - barWidth, bounds.X + gap, Math.Max(bounds.X + gap, bounds.Right - barWidth - gap));
        double below = sel.Bottom + gap;
        if (below + barHeight <= bounds.Bottom - gap) return (x, below);
        double above = sel.Y - gap - barHeight;
        if (above >= bounds.Y + gap) return (x, above);
        return (x, Math.Max(bounds.Y + gap, sel.Bottom - barHeight - gap));
    }
}

/// <summary>Drawing helpers for the annotation tools.</summary>
public static class ArrowGeometry
{
    /// <summary>The two end points of the arrow head's barbs for a line from <paramref name="from"/> to <paramref name="to"/>. Both sit at the tip.</summary>
    public static ((double X, double Y) Left, (double X, double Y) Right) Barbs(double fromX, double fromY, double toX, double toY, double thickness)
    {
        double dx = toX - fromX, dy = toY - fromY;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.001) return ((toX, toY), (toX, toY));

        double barb = Math.Min(length * 0.6, Math.Max(12, thickness * 4.5));
        double angle = Math.Atan2(dy, dx), spread = Math.PI / 7;
        return (
            (toX - barb * Math.Cos(angle - spread), toY - barb * Math.Sin(angle - spread)),
            (toX - barb * Math.Cos(angle + spread), toY - barb * Math.Sin(angle + spread)));
    }
}

/// <summary>Other screen capture programs that commonly own the Print Screen key; named to the user when the key cannot be registered.</summary>
public static class KnownCaptureTools
{
    private static readonly (string Process, string Name)[] Tools =
    {
        ("Lightshot", "Lightshot"), ("ShareX", "ShareX"), ("Greenshot", "Greenshot"), ("Snagit32", "Snagit"), ("SnagitEditor", "Snagit"),
        ("PicPick", "PicPick"), ("Flameshot", "Flameshot"), ("Gyazo", "Gyazo"), ("GyazoCap", "Gyazo"), ("SnippingTool", "Ferramenta de Captura"),
        ("ScreenClippingHost", "Ferramenta de Captura"), ("Monosnap", "Monosnap"), ("FastStone Capture", "FastStone Capture"), ("FSCapture", "FastStone Capture"),
    };

    /// <summary>The display names of the known capture programs among the running process names (no duplicates, in a stable order).</summary>
    public static IReadOnlyList<string> FindRunning(IEnumerable<string> processNames)
    {
        var running = new HashSet<string>(processNames, StringComparer.OrdinalIgnoreCase);
        return Tools.Where(t => running.Contains(t.Process)).Select(t => t.Name).Distinct().ToList();
    }
}

/// <summary>The file formats a capture can be saved in.</summary>
public enum CaptureImageFormat { Png, Jpeg }

public static class CaptureImageFormats
{
    public const int DefaultQuality = 90;

    /// <summary>"png" or "jpeg"/"jpg"; anything else (or nothing) means PNG, the default.</summary>
    public static CaptureImageFormat Parse(string? text) =>
        text?.Trim().ToLowerInvariant() is "jpeg" or "jpg" ? CaptureImageFormat.Jpeg : CaptureImageFormat.Png;

    public static string Id(CaptureImageFormat format) => format == CaptureImageFormat.Jpeg ? "jpeg" : "png";

    public static string Extension(CaptureImageFormat format) => format == CaptureImageFormat.Jpeg ? "jpg" : "png";

    /// <summary>The format a file name asks for (".jpg" and ".jpeg" are JPEG; everything else is PNG).</summary>
    public static CaptureImageFormat FromExtension(string? extension) => Parse(extension?.TrimStart('.'));

    /// <summary>JPEG quality from 1 to 100. PNG is lossless, so it ignores this.</summary>
    public static int ClampQuality(int quality) => Math.Clamp(quality, 1, 100);
}

public static class CaptureNaming
{
    /// <summary>"Screenshot 2026-11-25 14-03-07.png": sortable, and free of characters Windows rejects.</summary>
    public static string FileName(DateTime now, string extension = "png") => $"Screenshot {now:yyyy-MM-dd HH-mm-ss}.{extension.TrimStart('.')}";

    /// <summary>The folder for saving without asking: the configured one, or Pictures\Screenshots when none is set.</summary>
    public static string SaveFolder(string configured, string picturesFolder) =>
        string.IsNullOrWhiteSpace(configured) ? Path.Combine(picturesFolder, "Screenshots") : configured.Trim();

    /// <summary>A path in <paramref name="folder"/> that does not exist yet: "Screenshot ... (2).png" when two captures share a second.</summary>
    public static string UniquePath(string folder, string fileName, Func<string, bool> exists)
    {
        string path = Path.Combine(folder, fileName);
        if (!exists(path)) return path;

        string name = Path.GetFileNameWithoutExtension(fileName), extension = Path.GetExtension(fileName);
        for (int n = 2; ; n++)
        {
            path = Path.Combine(folder, $"{name} ({n}){extension}");
            if (!exists(path)) return path;
        }
    }
}
