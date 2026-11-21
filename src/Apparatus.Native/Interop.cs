using System.Runtime.InteropServices;

namespace Apparatus.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct Rect
{
    public int Left, Top, Right, Bottom;

    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MonitorInfo
{
    public int Size;
    public Rect Monitor;
    public Rect Work;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
internal struct PhysicalMonitor
{
    public nint Handle;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string Description;
}

/// <summary>Plain P/Invoke declarations for the window, monitor and power APIs the action steps need.</summary>
internal static unsafe class Interop
{
    public const int SW_MAXIMIZE = 3, SW_MINIMIZE = 6, SW_RESTORE = 9;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOPMOST = 0x8;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const uint WM_CLOSE = 0x0010, WM_SYSCOMMAND = 0x0112;
    public static readonly nint HWND_TOPMOST = -1, HWND_NOTOPMOST = -2, HWND_BROADCAST = 0xFFFF;

    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern bool IsZoomed(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] public static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] public static extern bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] public static extern bool LockWorkStation();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(nint hwnd, char* buffer, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(nint hwnd, char* buffer, int max);

    public delegate bool MonitorEnumProc(nint monitor, nint hdc, ref Rect rect, nint data);

    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);

    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect value, int size);

    [DllImport("powrprof.dll")] public static extern bool SetSuspendState(bool hibernate, bool force, bool disableWakeEvents);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHEmptyRecycleBinW(nint hwnd, string? rootPath, uint flags);

    [DllImport("dxva2.dll")] public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(nint monitor, out uint count);
    [DllImport("dxva2.dll")] public static extern bool GetPhysicalMonitorsFromHMONITOR(nint monitor, uint count, [Out] PhysicalMonitor[] monitors);
    [DllImport("dxva2.dll")] public static extern bool GetMonitorBrightness(nint physical, out uint min, out uint current, out uint max);
    [DllImport("dxva2.dll")] public static extern bool SetMonitorBrightness(nint physical, uint value);
    [DllImport("dxva2.dll")] public static extern bool DestroyPhysicalMonitors(uint count, [In] PhysicalMonitor[] monitors);
}
