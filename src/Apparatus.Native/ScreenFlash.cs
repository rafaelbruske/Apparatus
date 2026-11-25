using System.Runtime.InteropServices;
using Apparatus.Core.Capture;

namespace Apparatus.Native;

/// <summary>
/// A white, see-through window laid over an area of the screen for a moment: the "shutter" flash of the automatic capture.
/// It never takes the focus or the mouse (clicks go through it), and it is created after the screen was read, so it is not in the picture.
/// Create it, call <see cref="SetAlpha"/> a few times to fade it, and dispose it. Use it from a thread that pumps messages (the UI thread).
/// </summary>
public sealed unsafe class FlashWindow : IDisposable
{
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
    private const uint LWA_ALPHA = 0x2;
    private const int SW_SHOWNOACTIVATE = 4;
    private static readonly nint White = 0xFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowClass
    {
        public uint Size, Style;
        public nint WndProc;
        public int ClsExtra, WndExtra;
        public nint Instance, Icon, Cursor, Background, MenuName, ClassName, IconSmall;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern ushort RegisterClassExW(ref WindowClass wc);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, nint className, string? title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint color);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);

    private static nint s_className;
    private nint _hwnd;

    public FlashWindow(RectD area, byte alpha)
    {
        EnsureClass();
        _hwnd = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE, s_className, "", WS_POPUP,
            (int)area.X, (int)area.Y, (int)area.Width, (int)area.Height, 0, 0, GetModuleHandleW(null), 0);
        if (_hwnd == 0) throw new InvalidOperationException("Could not create the flash window.");
        SetLayeredWindowAttributes(_hwnd, 0, alpha, LWA_ALPHA);
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
    }

    /// <summary>0 is fully see-through, 255 fully white.</summary>
    public void SetAlpha(byte alpha)
    {
        if (_hwnd != 0) SetLayeredWindowAttributes(_hwnd, 0, alpha, LWA_ALPHA);
    }

    private static void EnsureClass()
    {
        if (s_className != 0) return;
        var defaultProc = NativeLibrary.GetExport(NativeLibrary.Load("user32.dll"), "DefWindowProcW");
        var name = Marshal.StringToHGlobalUni("Apparatus.Flash");   // lives as long as the class does: the whole run
        var wc = new WindowClass
        {
            Size = (uint)sizeof(WindowClass),
            WndProc = defaultProc,
            Instance = GetModuleHandleW(null),
            Background = CreateSolidBrush(0xFFFFFF),
            ClassName = name,
        };
        if (RegisterClassExW(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410)   // 1410: already registered
            throw new InvalidOperationException("Could not register the flash window class.");
        s_className = name;
    }

    public void Dispose()
    {
        if (_hwnd == 0) return;
        DestroyWindow(_hwnd);
        _hwnd = 0;
    }
}
