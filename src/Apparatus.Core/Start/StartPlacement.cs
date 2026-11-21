namespace Apparatus.Core.Start;

public enum ScreenEdge { Left = 0, Top = 1, Right = 2, Bottom = 3 }

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>Where the Start menu opens: against the taskbar, like the one it replaces.</summary>
public static class StartPlacement
{
    /// <param name="workArea">The monitor's work area (screen minus taskbar), in pixels.</param>
    /// <param name="taskbarEdge">Where the taskbar is docked.</param>
    /// <param name="position">"taskbar" (follows the icons' alignment), "center" or "left".</param>
    /// <param name="iconsCentered">Whether the taskbar icons (and the Start button) are centred.</param>
    /// <param name="size">The menu size in pixels; shrunk if the work area is smaller.</param>
    /// <param name="gap">Space kept between the menu and the screen edges, in pixels.</param>
    public static PixelRect Compute(PixelRect workArea, ScreenEdge taskbarEdge, string position, bool iconsCentered, (int Width, int Height) size, int gap)
    {
        int w = Math.Min(size.Width, Math.Max(1, workArea.Width - 2 * gap));
        int h = Math.Min(size.Height, Math.Max(1, workArea.Height - 2 * gap));

        bool horizontalCenter = position switch
        {
            "center" => true,
            "left" => false,
            _ => iconsCentered,
        };

        int x, y;
        switch (taskbarEdge)
        {
            case ScreenEdge.Left:
                x = workArea.X + gap;
                y = workArea.Y + gap;
                break;
            case ScreenEdge.Right:
                x = workArea.Right - w - gap;
                y = workArea.Y + gap;
                break;
            case ScreenEdge.Top:
                x = HorizontalX(workArea, w, gap, horizontalCenter);
                y = workArea.Y + gap;
                break;
            default:   // bottom
                x = HorizontalX(workArea, w, gap, horizontalCenter);
                y = workArea.Bottom - h - gap;
                break;
        }
        return new PixelRect(x, y, w, h);
    }

    private static int HorizontalX(PixelRect area, int width, int gap, bool center) =>
        center ? area.X + (area.Width - width) / 2 : area.X + gap;
}
