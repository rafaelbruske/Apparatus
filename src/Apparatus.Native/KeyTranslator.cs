using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Apparatus.Native;

public enum KeyAction
{
    /// <summary>Not typing (modifier alone, dead key, media key...): leave the typed-text buffer alone.</summary>
    Ignore,
    /// <summary>Produced text; see the out parameter.</summary>
    Text,
    Backspace,
    /// <summary>Anything that moves the caret or is a shortcut: forget what was typed.</summary>
    Reset,
}

/// <summary>Turns hook key events into the text they type, honouring the active layout (ABNT2, AltGr, Caps Lock).</summary>
public static unsafe class KeyTranslator
{
    private const uint VK_PACKET = 0xE7;

    public static KeyAction Classify(KeyEvent e, out string text)
    {
        text = "";
        if (!e.IsDown) return KeyAction.Ignore;

        uint vk = e.VirtualKey;
        if (IsModifierOrLock(vk)) return KeyAction.Ignore;
        if (vk == 0x08) return KeyAction.Backspace;

        // Unicode "packets" (on-screen keyboards, IME, SendInput with KEYEVENTF_UNICODE) carry the char in the scan code.
        if (vk == VK_PACKET)
        {
            text = ((char)e.ScanCode).ToString();
            return KeyAction.Text;
        }

        // Enter, Tab, Esc, Page/Home/End, arrows, Insert, Delete, function keys: the caret moved or something submitted.
        if (vk is 0x0D or 0x09 or 0x1B or >= 0x21 and <= 0x28 or 0x2D or 0x2E or >= 0x70 and <= 0x87) return KeyAction.Reset;

        bool ctrl = Down(0x11), alt = Down(0x12), win = Down(0x5B) || Down(0x5C);
        // Ctrl+Alt is AltGr on many layouts (it types "/" or "?" on ABNT2); Ctrl or Alt alone is a shortcut.
        if (win || (ctrl ^ alt)) return KeyAction.Reset;

        if (vk == 0x20) { text = " "; return KeyAction.Text; }

        Span<byte> state = stackalloc byte[256];
        state.Clear();
        if (Down(0x10)) state[0x10] = 0x80;
        if (ctrl) state[0x11] = 0x80;
        if (alt) state[0x12] = 0x80;
        if ((PInvoke.GetKeyState(0x14) & 1) != 0) state[0x14] = 0x01;   // Caps Lock toggled on

        var layout = PInvoke.GetKeyboardLayout(ForegroundThreadId());
        Span<char> buffer = stackalloc char[8];
        int n;
        fixed (byte* pState = state)
        fixed (char* pBuf = buffer)
        {
            // Flag 0x4 keeps the call from touching the system's pending dead-key state (Windows 10 1607+).
            n = PInvoke.ToUnicodeEx(vk, e.ScanCode, pState, new PWSTR(pBuf), buffer.Length, 0x4, layout);
        }

        if (n > 0)
        {
            text = new string(buffer[..n]);
            return text.Any(char.IsControl) ? KeyAction.Reset : KeyAction.Text;
        }
        return KeyAction.Ignore;   // dead key (n < 0) or no character
    }

    public static bool IsModifierOrLock(uint vk) =>
        vk is 0x10 or 0x11 or 0x12 or >= 0xA0 and <= 0xA5 or 0x5B or 0x5C or 0x14 or 0x90 or 0x91 or 0x5D;

    public static bool Down(int vk) => (PInvoke.GetAsyncKeyState(vk) & 0x8000) != 0;

    private static uint ForegroundThreadId()
    {
        var fg = PInvoke.GetForegroundWindow();
        return fg == HWND.Null ? 0 : PInvoke.GetWindowThreadProcessId(fg, null);
    }

    // ------------------------------------------------------ foreground info

    /// <summary>Process name (no extension) of the window that has the keyboard focus, or "" if unknown.</summary>
    public static string ForegroundProcessName()
    {
        var fg = PInvoke.GetForegroundWindow();
        if (fg == HWND.Null) return "";
        uint pid;
        PInvoke.GetWindowThreadProcessId(fg, &pid);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "";
        }
    }

    public static nint ForegroundWindow() => PInvoke.GetForegroundWindow();

    /// <summary>True when the focused control is a classic Win32 password box (ES_PASSWORD). Browser fields are not detectable here.</summary>
    public static bool IsPasswordFieldFocused()
    {
        const nint ES_PASSWORD = 0x20;
        var info = new GUITHREADINFO { cbSize = (uint)sizeof(GUITHREADINFO) };
        if (!PInvoke.GetGUIThreadInfo(0, ref info) || info.hwndFocus == HWND.Null) return false;
        return (PInvoke.GetWindowLongPtr(info.hwndFocus, WINDOW_LONG_PTR_INDEX.GWL_STYLE) & ES_PASSWORD) != 0;
    }
}
