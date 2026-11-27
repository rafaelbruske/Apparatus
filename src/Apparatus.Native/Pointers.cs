using System.Runtime.InteropServices;
using Apparatus.Core.Capture;
using Microsoft.Win32;

namespace Apparatus.Native;

/// <summary>Mouse pointers drawn by Apparatus, and what Windows says about the size the user wants them.</summary>
public static class Pointers
{
    private static readonly object Gate = new();
    private static readonly Dictionary<int, nint> Crosshairs = new();   // by side in pixels; kept while the program runs

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;
        public uint HotspotX, HotspotY;
        public nint Mask, Color;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern nint CreateIconIndirect(in IconInfo info);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, in BitmapInfoHeader info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint CreateBitmap(int width, int height, uint planes, uint bitCount, byte[] bits);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);

    /// <summary>The pointer size chosen in Windows' accessibility settings, in pixels at 100% (32 unless it was enlarged).</summary>
    public static int ChosenSize()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Cursors");
        return key?.GetValue("CursorBaseSize") is int size and > 0 ? size : Crosshair.NormalSize;
    }

    /// <summary>The Windows handle (HCURSOR) of the capture's crosshair with this side. Zero when Windows refuses to create it.</summary>
    public static nint CaptureCrosshair(int size)
    {
        lock (Gate)
        {
            if (Crosshairs.TryGetValue(size, out nint known)) return known;
            nint created = Create(Crosshair.Draw(size));
            if (created != 0) Crosshairs[size] = created;
            return created;
        }
    }

    private static unsafe nint Create(CursorImage image)
    {
        int size = image.Size;
        var header = new BitmapInfoHeader { Size = (uint)sizeof(BitmapInfoHeader), Width = size, Height = -size, Planes = 1, BitCount = 32 };
        nint color = 0, mask = 0;
        try
        {
            color = CreateDIBSection(0, in header, 0, out nint bits, 0, 0);
            if (color == 0 || bits == 0) return 0;
            Marshal.Copy(image.Bgra, 0, bits, image.Bgra.Length);

            // The one-bit mask: set where nothing is drawn. Each row takes a whole number of 16-bit words.
            int stride = (size + 15) / 16 * 2;
            var maskBits = new byte[stride * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    if (image.Bgra[(y * size + x) * 4 + 3] == 0) maskBits[y * stride + x / 8] |= (byte)(0x80 >> (x % 8));
            mask = CreateBitmap(size, size, 1, 1, maskBits);
            if (mask == 0) return 0;

            var info = new IconInfo { IsIcon = 0, HotspotX = (uint)image.HotspotX, HotspotY = (uint)image.HotspotY, Mask = mask, Color = color };
            return CreateIconIndirect(in info);   // keeps its own copy of both bitmaps
        }
        finally
        {
            if (color != 0) DeleteObject(color);
            if (mask != 0) DeleteObject(mask);
        }
    }
}
