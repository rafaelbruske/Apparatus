namespace Apparatus.Core.Capture;

/// <summary>A pointer picture: square, 32 bits per pixel in BGRA order, rows from top to bottom.</summary>
/// <param name="HotspotX">The pixel that "is" the pointer position; the same for <paramref name="HotspotY"/>.</param>
public sealed record CursorImage(int Size, int HotspotX, int HotspotY, byte[] Bgra);

/// <summary>
/// The capture's own pointer: a white cross with a black edge, so one of the two always stands out. The Windows crosshair inverts the
/// colors under it, which over the middle gray of a dimmed white area gives almost the same gray, and it disappears.
/// </summary>
public static class Crosshair
{
    /// <summary>The side of an ordinary Windows pointer at 100%.</summary>
    public const int NormalSize = 32;

    public const int MaxSize = 256;

    /// <summary>
    /// The side in pixels on a monitor: the pointer size chosen in Windows (accessibility; 32 by default) times the monitor's scale
    /// (1.5 = 150%).
    /// </summary>
    public static int SizeFor(double monitorScale, int pointerSize)
    {
        if (pointerSize <= 0) pointerSize = NormalSize;
        if (!(monitorScale > 0)) monitorScale = 1;
        return (int)Math.Clamp(Math.Round(pointerSize * monitorScale), NormalSize, MaxSize);
    }

    public static CursorImage Draw(int size)
    {
        size = Math.Clamp(size, NormalSize, MaxSize);
        int center = size / 2;
        int core = size / 64;                  // half of the white line, without its middle pixel: 1 px at 32, 3 px at 64...
        int edge = Math.Max(1, size / 32);     // width of the black edge
        int arm = size * 3 / 8;                // from the middle to the tip of the white line

        // The cross made of two bars, grown by "grow" pixels on every side.
        bool Inside(int dx, int dy, int grow) =>
            (dx <= core + grow && dy <= arm + grow) || (dy <= core + grow && dx <= arm + grow);

        var pixels = new byte[size * size * 4];   // all transparent
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int dx = Math.Abs(x - center), dy = Math.Abs(y - center);
                if (!Inside(dx, dy, edge)) continue;
                byte tone = Inside(dx, dy, 0) ? (byte)0xFF : (byte)0x00;
                int i = (y * size + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = tone;
                pixels[i + 3] = 0xFF;
            }
        }
        return new CursorImage(size, center, center, pixels);
    }
}
