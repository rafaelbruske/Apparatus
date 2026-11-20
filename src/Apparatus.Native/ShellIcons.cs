using System.Runtime.InteropServices;

namespace Apparatus.Native;

public sealed record IconBitmap(int Width, int Height, byte[] Bgra);

/// <summary>
/// Real icons for apps and files via IShellItemImageFactory, which handles packaged apps (AUMIDs),
/// shortcuts, executables, folders and file types uniformly. Returns premultiplied BGRA pixels.
/// </summary>
public static class ShellIcons
{
    private const int SIIGBF_ICONONLY = 0x04;

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public short bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern int GetObjectW(IntPtr h, int c, out BITMAP bmp);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[]? bits, ref BITMAPINFOHEADER bmi, uint usage);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    private static readonly Guid FactoryIid = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    /// <summary>
    /// <paramref name="parsingName"/> is a file path or "shell:AppsFolder\AUMID". Must run on an STA/UI-safe thread;
    /// a pool thread works because the factory is free-threaded for icons.
    /// </summary>
    public static IconBitmap? TryGet(string parsingName, int size)
    {
        IShellItemImageFactory? factory = null;
        IntPtr hbitmap = IntPtr.Zero;
        try
        {
            var iid = FactoryIid;
            SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out factory);
            if (factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_ICONONLY, out hbitmap) != 0 || hbitmap == IntPtr.Zero)
                return null;

            if (GetObjectW(hbitmap, Marshal.SizeOf<BITMAP>(), out var bmp) == 0 || bmp.bmWidth <= 0 || bmp.bmHeight <= 0)
                return null;

            var header = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = bmp.bmWidth,
                biHeight = -bmp.bmHeight,   // negative: top-down rows
                biPlanes = 1,
                biBitCount = 32,
            };
            var pixels = new byte[bmp.bmWidth * bmp.bmHeight * 4];
            IntPtr hdc = GetDC(IntPtr.Zero);
            try
            {
                if (GetDIBits(hdc, hbitmap, 0, (uint)bmp.bmHeight, pixels, ref header, 0) == 0) return null;
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, hdc);
            }

            // Some sources return no alpha at all; treat that as opaque so the icon is not invisible.
            bool anyAlpha = false;
            for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { anyAlpha = true; break; }
            if (!anyAlpha) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 0xFF;

            return new IconBitmap(bmp.bmWidth, bmp.bmHeight, pixels);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or FileNotFoundException)
        {
            return null;
        }
        finally
        {
            if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
            if (factory is not null) Marshal.ReleaseComObject(factory);
        }
    }
}
