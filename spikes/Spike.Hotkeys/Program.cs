// Spike 1: RegisterHotKey (Alt+Space) latency + WH_KEYBOARD_LL suppression of the lone Win key.
// Input is injected with SendInput so the whole test is automatic.
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

internal static unsafe class Program
{
    const nuint OurMarker = 0xA99A;   // dwExtraInfo on events injected to mask the Start menu
    static long s_hotkeyTicks;
    static int s_winTaps;
    static readonly List<string> s_found = new();
    static bool s_winUsed;

    static int Main(string[] args)
    {
        if (args.Contains("--interactive"))
        {
            // Manual check, no injected input: tap the Win key a few times during 20 s.
            // Expected: Start menu does NOT open and each lone tap is counted; Win+E etc. still work.
            var h = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, &HookProc, null, 0);
            Console.WriteLine("Hook active for 20 s. Tap the Win key (alone) and try Win+E...");
            var t = Stopwatch.StartNew(); int last = 0;
            while (t.ElapsedMilliseconds < 20000)
            {
                PumpMessages(); Thread.Sleep(10);
                int now = Volatile.Read(ref s_winTaps);
                if (now != last) { Console.WriteLine($"  lone Win tap #{now} detected"); last = now; }
            }
            h.Dispose();
            Console.WriteLine($"Total lone taps: {s_winTaps}");
            return 0;
        }

        // ---- message-only window (receives WM_HOTKEY) ----
        var hInst = PInvoke.GetModuleHandle((string?)null);
        fixed (char* cls = "ApparatusSpike")
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = (HINSTANCE)hInst.DangerousGetHandle(),
                lpszClassName = cls,
            };
            PInvoke.RegisterClassEx(in wc);
        }
        var hwnd = PInvoke.CreateWindowEx(0, "ApparatusSpike", "spike", 0, 0, 0, 0, 0,
            HWND.HWND_MESSAGE, null, null, null);

        // ---- 1a. RegisterHotKey(Alt+Space) ----
        Console.WriteLine("=== Spike 1a: RegisterHotKey Alt+Space ===");
        bool ok = PInvoke.RegisterHotKey(hwnd, 1, HOT_KEY_MODIFIERS.MOD_ALT | HOT_KEY_MODIFIERS.MOD_NOREPEAT,
            (uint)VIRTUAL_KEY.VK_SPACE);
        VIRTUAL_KEY testKey = VIRTUAL_KEY.VK_SPACE;
        HOT_KEY_MODIFIERS testMods = HOT_KEY_MODIFIERS.MOD_ALT;
        if (!ok)
        {
            Console.WriteLine($"Alt+Space unavailable: Win32 error {Marshal.GetLastWin32Error()} (1409 = already registered by another app, e.g. Raycast)");
            testKey = VIRTUAL_KEY.VK_F12;
            testMods = HOT_KEY_MODIFIERS.MOD_ALT | HOT_KEY_MODIFIERS.MOD_CONTROL;
            ok = PInvoke.RegisterHotKey(hwnd, 1, testMods | HOT_KEY_MODIFIERS.MOD_NOREPEAT, (uint)testKey);
            Console.WriteLine($"Fallback Ctrl+Alt+F12 registered = {ok}");
        }
        if (ok)
        {
            Console.WriteLine("RegisterHotKey OK -> measuring WM_HOTKEY latency");
            var samples = new List<double>();
            for (int i = 0; i < 25; i++)
            {
                Interlocked.Exchange(ref s_hotkeyTicks, 0);
                long t0 = Stopwatch.GetTimestamp();
                if (testMods.HasFlag(HOT_KEY_MODIFIERS.MOD_CONTROL))
                    SendKeys(false, (VIRTUAL_KEY.VK_CONTROL, false), (VIRTUAL_KEY.VK_MENU, false), (testKey, false),
                                    (testKey, true), (VIRTUAL_KEY.VK_MENU, true), (VIRTUAL_KEY.VK_CONTROL, true));
                else
                    SendKeys(false, (VIRTUAL_KEY.VK_MENU, false), (testKey, false),
                                    (testKey, true), (VIRTUAL_KEY.VK_MENU, true));
                var deadline = Stopwatch.StartNew();
                while (Interlocked.Read(ref s_hotkeyTicks) == 0 && deadline.ElapsedMilliseconds < 500)
                    PumpMessages();
                long t1 = Interlocked.Read(ref s_hotkeyTicks);
                if (t1 != 0) samples.Add((t1 - t0) * 1000.0 / Stopwatch.Frequency);
                Thread.Sleep(40);
            }
            samples.Sort();
            double median = samples.Count > 0 ? samples[samples.Count / 2] : 0;
            Console.WriteLine($"WM_HOTKEY received {samples.Count}/25 | latency ms: min {samples.FirstOrDefault():F2}, median {median:F2}, max {samples.LastOrDefault():F2}");
            PInvoke.UnregisterHotKey(hwnd, 1);
        }

        // ---- 1b. Lone Win key: control (no hook), then with hook ----
        Console.WriteLine();
        Console.WriteLine("=== Spike 1b: lone Win key vs Start menu ===");
        Console.WriteLine($"Control (no hook): Start opened = {TapWinAndCheckStart()}");

        var hook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, &HookProc, null, 0);
        if (hook.IsInvalid) { Console.WriteLine("FAIL: SetWindowsHookEx"); return 1; }
        s_winTaps = 0;
        var result = TapWinAndCheckStart();
        Console.WriteLine($"With LL hook:      Start opened = {result} | lone-Win events raised to app = {Volatile.Read(ref s_winTaps)}");

        // Win + (unmapped F13) must NOT be treated as a lone Win tap.
        s_winTaps = 0;
        SendKeys(false, (VIRTUAL_KEY.VK_LWIN, false), (VIRTUAL_KEY.VK_F13, false),
                        (VIRTUAL_KEY.VK_F13, true), (VIRTUAL_KEY.VK_LWIN, true));
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 300) { PumpMessages(); Thread.Sleep(10); }
        Console.WriteLine($"Win+F13 combo through hook: lone-Win events raised (expected 0) = {Volatile.Read(ref s_winTaps)}");

        hook.Dispose();
        PInvoke.DestroyWindow(hwnd);
        Console.WriteLine("Done.");
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    static LRESULT WndProc(HWND h, uint msg, WPARAM w, LPARAM l)
    {
        if (msg == PInvoke.WM_HOTKEY)
        {
            Interlocked.Exchange(ref s_hotkeyTicks, Stopwatch.GetTimestamp());
            return default;
        }
        return PInvoke.DefWindowProc(h, msg, w, l);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    static LRESULT HookProc(int code, WPARAM w, LPARAM l)
    {
        if (code >= 0)
        {
            var k = (KBDLLHOOKSTRUCT*)l.Value;
            if (k->dwExtraInfo != OurMarker)
            {
                bool down = w.Value == PInvoke.WM_KEYDOWN || w.Value == PInvoke.WM_SYSKEYDOWN;
                bool up = w.Value == PInvoke.WM_KEYUP || w.Value == PInvoke.WM_SYSKEYUP;
                bool isWin = k->vkCode == (uint)VIRTUAL_KEY.VK_LWIN || k->vkCode == (uint)VIRTUAL_KEY.VK_RWIN;
                if (isWin && down) s_winUsed = false;
                else if (!isWin && down) s_winUsed = true;
                else if (isWin && up && !s_winUsed)
                {
                    // Lone Win tap: raise our event and mask the Start menu with an unassigned key (VK 0xE8).
                    Interlocked.Increment(ref s_winTaps);
                    SendKeys(true, ((VIRTUAL_KEY)0xE8, false), ((VIRTUAL_KEY)0xE8, true));
                }
            }
        }
        return PInvoke.CallNextHookEx(null, code, w, l);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    static BOOL EnumProc(HWND h, LPARAM _)
    {
        if (!PInvoke.IsWindowVisible(h)) return true;
        int cloaked = 0;
        PInvoke.DwmGetWindowAttribute(h, Windows.Win32.Graphics.Dwm.DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(int));
        if (cloaked != 0) return true;
        uint p; PInvoke.GetWindowThreadProcessId(h, &p);
        try
        {
            var pn = Process.GetProcessById((int)p).ProcessName;
            if (pn is "StartMenuExperienceHost" or "SearchHost") s_found.Add(pn);
        }
        catch { }
        return true;
    }

    static void PumpMessages()
    {
        while (PInvoke.PeekMessage(out var m, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
        {
            PInvoke.TranslateMessage(in m);
            PInvoke.DispatchMessage(in m);
        }
    }

    static void SendKeys(bool marked, params (VIRTUAL_KEY vk, bool up)[] keys)
    {
        var inputs = new INPUT[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            inputs[i].type = INPUT_TYPE.INPUT_KEYBOARD;
            inputs[i].Anonymous.ki.wVk = keys[i].vk;
            if (marked) inputs[i].Anonymous.ki.dwExtraInfo = OurMarker;
            if (keys[i].up) inputs[i].Anonymous.ki.dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;
        }
        PInvoke.SendInput(inputs, sizeof(INPUT));
    }

    static string TapWinAndCheckStart()
    {
        SendKeys(false, (VIRTUAL_KEY.VK_LWIN, false), (VIRTUAL_KEY.VK_LWIN, true));
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 800) { PumpMessages(); Thread.Sleep(10); }
        var fg = PInvoke.GetForegroundWindow();
        uint pid;
        PInvoke.GetWindowThreadProcessId(fg, &pid);
        string name;
        try { name = Process.GetProcessById((int)pid).ProcessName; } catch { name = "?"; }
        s_found.Clear();
        PInvoke.EnumWindows(&EnumProc, 0);
        var found = s_found;
        bool startOpen = found.Count > 0;
        name += found.Count > 0 ? $"; visible Start windows: {string.Join(",", found)}" : "";
        if (startOpen)
        {
            SendKeys(false, (VIRTUAL_KEY.VK_ESCAPE, false), (VIRTUAL_KEY.VK_ESCAPE, true));
            Thread.Sleep(400);
        }
        return $"{startOpen} (foreground: {name})";
    }
}
