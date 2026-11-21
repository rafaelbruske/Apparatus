namespace Apparatus.Native;

/// <summary>Moves and resizes the window that has the focus: halves, center, maximize, next monitor...</summary>
public static unsafe class WindowManager
{
    public static readonly IReadOnlyList<string> Commands = new[]
    {
        "maximize", "minimize", "restore", "close", "left-half", "right-half", "top-half", "bottom-half",
        "center", "topmost", "next-monitor",
    };

    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    /// <summary>Applies <paramref name="command"/> to the foreground window, or to <paramref name="hwnd"/> when given.</summary>
    public static void Apply(string command, nint hwnd = 0)
    {
        if (hwnd == 0) hwnd = Interop.GetForegroundWindow();
        if (hwnd == 0 || ClassOf(hwnd) is var cls && ShellClasses.Contains(cls))
            throw new InvalidOperationException("Não há uma janela ativa para gerenciar.");

        switch (command)
        {
            case "maximize": Interop.ShowWindow(hwnd, Interop.SW_MAXIMIZE); return;
            case "minimize": Interop.ShowWindow(hwnd, Interop.SW_MINIMIZE); return;
            case "restore": Interop.ShowWindow(hwnd, Interop.SW_RESTORE); return;
            case "close": Interop.PostMessageW(hwnd, Interop.WM_CLOSE, 0, 0); return;
            case "topmost": ToggleTopmost(hwnd); return;
        }

        if (Interop.IsIconic(hwnd)) Interop.ShowWindow(hwnd, Interop.SW_RESTORE);
        bool wasMaximized = Interop.IsZoomed(hwnd);
        if (wasMaximized && command != "next-monitor") Interop.ShowWindow(hwnd, Interop.SW_RESTORE);

        var work = WorkAreaOf(hwnd);
        switch (command)
        {
            case "left-half": Place(hwnd, work.Left, work.Top, work.Width / 2, work.Height); break;
            case "right-half": Place(hwnd, work.Left + work.Width / 2, work.Top, work.Width - work.Width / 2, work.Height); break;
            case "top-half": Place(hwnd, work.Left, work.Top, work.Width, work.Height / 2); break;
            case "bottom-half": Place(hwnd, work.Left, work.Top + work.Height / 2, work.Width, work.Height - work.Height / 2); break;
            case "center": Center(hwnd, work); break;
            case "next-monitor": MoveToNextMonitor(hwnd, work, wasMaximized); break;
            default: throw new ArgumentException($"Comando de janela desconhecido: {command}");
        }
    }

    // ------------------------------------------------------------ helpers

    /// <summary>
    /// Sets the window's visible rectangle. Windows 10/11 add invisible resize borders around a window, so asking
    /// for the "outer" rectangle would leave gaps; the difference between the two is measured and compensated.
    /// </summary>
    private static void Place(nint hwnd, int x, int y, int width, int height)
    {
        var (l, t, r, b) = InvisibleBorders(hwnd);
        Interop.SetWindowPos(hwnd, 0, x - l, y - t, width + l + r, height + t + b, Interop.SWP_NOZORDER | Interop.SWP_NOACTIVATE);
    }

    private static void Center(nint hwnd, Rect work)
    {
        var v = VisibleRect(hwnd);
        int w = Math.Min(v.Width, work.Width), h = Math.Min(v.Height, work.Height);
        Place(hwnd, work.Left + (work.Width - w) / 2, work.Top + (work.Height - h) / 2, w, h);
    }

    private static void MoveToNextMonitor(nint hwnd, Rect current, bool wasMaximized)
    {
        var monitors = Monitors();
        if (monitors.Count < 2) return;

        int index = monitors.FindIndex(m => m.Left == current.Left && m.Top == current.Top);
        var next = monitors[(Math.Max(index, 0) + 1) % monitors.Count];

        if (wasMaximized) Interop.ShowWindow(hwnd, Interop.SW_RESTORE);
        var v = VisibleRect(hwnd);

        // Keep the same relative position and size on the new screen.
        double fx = (v.Left - current.Left) / (double)current.Width, fy = (v.Top - current.Top) / (double)current.Height;
        double fw = v.Width / (double)current.Width, fh = v.Height / (double)current.Height;
        Place(hwnd, next.Left + (int)(fx * next.Width), next.Top + (int)(fy * next.Height),
            (int)(fw * next.Width), (int)(fh * next.Height));
        if (wasMaximized) Interop.ShowWindow(hwnd, Interop.SW_MAXIMIZE);
    }

    private static void ToggleTopmost(nint hwnd)
    {
        bool topmost = (Interop.GetWindowLongPtr(hwnd, Interop.GWL_EXSTYLE) & Interop.WS_EX_TOPMOST) != 0;
        Interop.SetWindowPos(hwnd, topmost ? Interop.HWND_NOTOPMOST : Interop.HWND_TOPMOST, 0, 0, 0, 0,
            Interop.SWP_NOMOVE | Interop.SWP_NOSIZE | Interop.SWP_NOACTIVATE);
    }

    private static Rect WorkAreaOf(nint hwnd)
    {
        var info = new MonitorInfo { Size = sizeof(MonitorInfo) };
        var monitor = Interop.MonitorFromWindow(hwnd, Interop.MONITOR_DEFAULTTONEAREST);
        if (!Interop.GetMonitorInfoW(monitor, ref info)) throw new InvalidOperationException("Não foi possível obter o monitor da janela.");
        return info.Work;
    }

    private static List<Rect> Monitors()
    {
        var list = new List<Rect>();
        Interop.EnumDisplayMonitors(0, 0, (nint monitor, nint hdc, ref Rect rect, nint data) =>
        {
            var info = new MonitorInfo { Size = sizeof(MonitorInfo) };
            if (Interop.GetMonitorInfoW(monitor, ref info)) list.Add(info.Work);
            return true;
        }, 0);
        return list.OrderBy(r => r.Left).ThenBy(r => r.Top).ToList();
    }

    private static Rect VisibleRect(nint hwnd) =>
        Interop.DwmGetWindowAttribute(hwnd, Interop.DWMWA_EXTENDED_FRAME_BOUNDS, out var r, sizeof(Rect)) == 0
            ? r
            : Outer(hwnd);

    private static Rect Outer(nint hwnd)
    {
        Interop.GetWindowRect(hwnd, out var r);
        return r;
    }

    private static (int Left, int Top, int Right, int Bottom) InvisibleBorders(nint hwnd)
    {
        var outer = Outer(hwnd);
        var visible = VisibleRect(hwnd);
        return (visible.Left - outer.Left, visible.Top - outer.Top, outer.Right - visible.Right, outer.Bottom - visible.Bottom);
    }

    private static string ClassOf(nint hwnd)
    {
        char* buffer = stackalloc char[64];
        int n = Interop.GetClassNameW(hwnd, buffer, 64);
        return n > 0 ? new string(buffer, 0, n) : "";
    }
}
