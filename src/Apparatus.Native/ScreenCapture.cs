using System.Runtime.InteropServices;
using Apparatus.Core.Capture;

namespace Apparatus.Native;

/// <summary>
/// Reads the screen with GDI: one BitBlt of the wanted rectangle of the virtual desktop. It is fast, works on every monitor with the
/// program DPI-aware (coordinates are physical pixels) and includes layered windows. Content that Windows draws with exclusive
/// full-screen or protected output comes out black; the screen recording planned for later uses Windows.Graphics.Capture, which
/// handles those, behind the same <see cref="IFrameSource"/>.
/// </summary>
public sealed unsafe class GdiFrameSource : IFrameSource
{
    private const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
    private const int MDT_EFFECTIVE_DPI = 0;
    private const uint MONITORINFOF_PRIMARY = 1;

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

    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, in BitmapInfoHeader info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dest, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

    public IReadOnlyList<MonitorInfoData> Monitors()
    {
        var list = new List<(RectD Bounds, double Scale, bool Primary)>();
        Interop.EnumDisplayMonitors(0, 0, (nint monitor, nint hdc, ref Rect rect, nint data) =>
        {
            var info = new MonitorInfo { Size = sizeof(MonitorInfo) };
            if (!Interop.GetMonitorInfoW(monitor, ref info)) return true;
            double scale = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1.0;
            var m = info.Monitor;
            list.Add((new RectD(m.Left, m.Top, m.Width, m.Height), scale, (info.Flags & MONITORINFOF_PRIMARY) != 0));
            return true;
        }, 0);

        return list.OrderByDescending(m => m.Primary).ThenBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y)
            .Select((m, i) => new MonitorInfoData(i, m.Bounds, m.Scale, m.Primary)).ToList();
    }

    /// <summary>
    /// The centre of the window that has the focus, or null when there is none worth using: no foreground window, a minimized one,
    /// or the desktop and taskbar (which cover monitors without telling which one the user is working on).
    /// </summary>
    public static (int X, int Y)? ForegroundWindowCenter()
    {
        nint hwnd = Interop.GetForegroundWindow();
        if (hwnd == 0 || Interop.IsIconic(hwnd)) return null;

        char* buffer = stackalloc char[64];
        int n = Interop.GetClassNameW(hwnd, buffer, 64);
        string cls = n > 0 ? new string(buffer, 0, n) : "";
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return null;

        if (Interop.DwmGetWindowAttribute(hwnd, Interop.DWMWA_EXTENDED_FRAME_BOUNDS, out var f, sizeof(Rect)) != 0 && !Interop.GetWindowRect(hwnd, out f))
            return null;
        return (f.Left + f.Width / 2, f.Top + f.Height / 2);
    }

    public CapturedFrame Grab(CaptureTarget target)
    {
        var area = Resolve(target);
        int x = (int)Math.Round(area.X), y = (int)Math.Round(area.Y);
        int w = (int)Math.Round(area.Width), h = (int)Math.Round(area.Height);
        if (w <= 0 || h <= 0) throw new InvalidOperationException("A área a capturar está vazia.");

        nint screen = GetDC(0);
        if (screen == 0) throw new InvalidOperationException("Não foi possível ler a tela.");
        nint memory = 0, bitmap = 0, previous = 0;
        try
        {
            memory = CreateCompatibleDC(screen);
            var header = new BitmapInfoHeader { Size = (uint)sizeof(BitmapInfoHeader), Width = w, Height = -h, Planes = 1, BitCount = 32 };
            bitmap = CreateDIBSection(screen, in header, 0, out nint bits, 0, 0);
            if (memory == 0 || bitmap == 0 || bits == 0) throw new InvalidOperationException("Não foi possível criar a imagem da captura.");
            previous = SelectObject(memory, bitmap);
            if (!BitBlt(memory, 0, 0, w, h, screen, x, y, SRCCOPY | CAPTUREBLT))
                throw new InvalidOperationException("O Windows recusou a leitura da tela (a tela pode estar bloqueada).");

            var pixels = new byte[checked(w * h * 4)];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            // GDI leaves the alpha byte at 0; the frame is opaque.
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 0xFF;
            return new CapturedFrame(new RectD(x, y, w, h), w, h, pixels);
        }
        finally
        {
            if (previous != 0) SelectObject(memory, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    private RectD Resolve(CaptureTarget target) => target switch
    {
        RegionTarget r => r.Area,
        MonitorTarget m => Monitors().FirstOrDefault(x => x.Index == m.Index)?.Bounds
            ?? throw new InvalidOperationException("O monitor não existe mais."),
        WindowTarget w => Interop.DwmGetWindowAttribute(w.Handle, Interop.DWMWA_EXTENDED_FRAME_BOUNDS, out var f, sizeof(Rect)) == 0
            ? new RectD(f.Left, f.Top, f.Width, f.Height)
            : throw new InvalidOperationException("Não foi possível medir a janela."),
        _ => throw new NotSupportedException("Alvo de captura desconhecido."),
    };
}
