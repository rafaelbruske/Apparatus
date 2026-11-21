using System.Runtime.InteropServices;
using Apparatus.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Memory;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Apparatus.Native;

/// <summary>
/// Plain-text clipboard access for background threads, with the markers that keep our temporary
/// clipboard writes out of Windows clipboard history and cloud sync (snippets may hold passwords).
/// </summary>
public sealed unsafe class ClipboardAccess : IDisposable
{
    private const uint CF_TEXT = 1, CF_OEMTEXT = 7, CF_UNICODETEXT = 13, CF_LOCALE = 16;

    private readonly HWND _owner;
    private readonly uint _excludeFromMonitor, _canIncludeInHistory, _canUploadToCloud;

    public ClipboardAccess()
    {
        // SetClipboardData requires a real owner window; a hidden message-only window is enough.
        var hInstance = PInvoke.GetModuleHandle((string?)null);
        fixed (char* cls = "Apparatus.ClipboardOwner")
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &OwnerProc,
                hInstance = (HINSTANCE)hInstance.DangerousGetHandle(),
                lpszClassName = cls,
            };
            PInvoke.RegisterClassEx(in wc);
        }
        _owner = PInvoke.CreateWindowEx(0, "Apparatus.ClipboardOwner", "", 0, 0, 0, 0, 0, HWND.HWND_MESSAGE, null, null, null);
        _excludeFromMonitor = PInvoke.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
        _canIncludeInHistory = PInvoke.RegisterClipboardFormat("CanIncludeInClipboardHistory");
        _canUploadToCloud = PInvoke.RegisterClipboardFormat("CanUploadToCloudClipboard");
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static LRESULT OwnerProc(HWND h, uint msg, WPARAM w, LPARAM l) => PInvoke.DefWindowProc(h, msg, w, l);

    public uint SequenceNumber => PInvoke.GetClipboardSequenceNumber();

    private bool Open()
    {
        for (int i = 0; i < 20; i++)
        {
            if (PInvoke.OpenClipboard(_owner)) return true;
            Thread.Sleep(10);   // another process (clipboard manager, remote session) briefly holds it
        }
        return false;
    }

    /// <summary>The current text, or null when the clipboard holds no text (or could not be opened).</summary>
    public string? GetText()
    {
        if (!Open()) return null;
        try
        {
            var handle = PInvoke.GetClipboardData(CF_UNICODETEXT);
            if (handle == HANDLE.Null) return null;
            var hMem = new HGLOBAL((void*)handle.Value);
            var p = PInvoke.GlobalLock(hMem);
            if (p is null) return null;
            try { return new string((char*)p); }
            finally { PInvoke.GlobalUnlock(hMem); }
        }
        finally { PInvoke.CloseClipboard(); }
    }

    /// <summary>
    /// Reads what was just copied, for the history. Returns null when it should not be recorded: no text, marked
    /// by the source as private (password managers do this), or copied in an excluded program.
    /// </summary>
    public ClipboardCapture? ReadForHistory(IReadOnlyCollection<string> excludedProcesses)
    {
        // Ask for the owner first: it is only meaningful while the clipboard still holds this copy.
        string source = OwnerProcessName();
        if (source.Length > 0 && excludedProcesses.Contains(source, StringComparer.OrdinalIgnoreCase)) return null;
        if (!PInvoke.IsClipboardFormatAvailable(CF_UNICODETEXT)) return null;
        if (PInvoke.IsClipboardFormatAvailable(_excludeFromMonitor)) return null;
        if (PInvoke.IsClipboardFormatAvailable(_canIncludeInHistory) && ReadDword(_canIncludeInHistory) == 0) return null;

        string? text = GetText();
        return string.IsNullOrWhiteSpace(text) ? null : new ClipboardCapture(text, source);
    }

    private string OwnerProcessName()
    {
        var owner = PInvoke.GetClipboardOwner();
        if (owner == HWND.Null) return "";
        uint pid;
        PInvoke.GetWindowThreadProcessId(owner, &pid);
        try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); return p.ProcessName; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return ""; }
    }

    private uint? ReadDword(uint format)
    {
        if (!Open()) return null;
        try
        {
            var handle = PInvoke.GetClipboardData(format);
            if (handle == HANDLE.Null) return null;
            var hMem = new HGLOBAL((void*)handle.Value);
            if (PInvoke.GlobalSize(hMem) < sizeof(uint)) return null;
            var p = PInvoke.GlobalLock(hMem);
            if (p is null) return null;
            try { return *(uint*)p; }
            finally { PInvoke.GlobalUnlock(hMem); }
        }
        finally { PInvoke.CloseClipboard(); }
    }

    /// <summary>
    /// True when the clipboard is empty or holds only text, i.e. it can be replaced and restored without
    /// losing anything (an image or a copied file would be lost).
    /// </summary>
    public bool IsSafeToReplace()
    {
        if (!Open()) return false;
        try
        {
            for (uint f = PInvoke.EnumClipboardFormats(0); f != 0; f = PInvoke.EnumClipboardFormats(f))
            {
                if (f is not (CF_TEXT or CF_OEMTEXT or CF_UNICODETEXT or CF_LOCALE)) return false;
            }
            return true;
        }
        finally { PInvoke.CloseClipboard(); }
    }

    /// <summary>Replaces the clipboard with <paramref name="text"/> (or empties it when null), hidden from history/cloud sync.</summary>
    public bool SetText(string? text)
    {
        if (!Open()) return false;
        try
        {
            PInvoke.EmptyClipboard();
            if (text is null) return true;

            int bytes = (text.Length + 1) * sizeof(char);
            var hMem = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, (nuint)bytes);
            if (hMem == HGLOBAL.Null) return false;
            var p = PInvoke.GlobalLock(hMem);
            if (p is null) { PInvoke.GlobalFree(hMem); return false; }
            text.AsSpan().CopyTo(new Span<char>(p, text.Length));
            ((char*)p)[text.Length] = '\0';
            PInvoke.GlobalUnlock(hMem);

            if (PInvoke.SetClipboardData(CF_UNICODETEXT, new HANDLE((nint)hMem.Value)) == HANDLE.Null)
            {
                PInvoke.GlobalFree(hMem);   // the system only takes ownership when the call succeeds
                return false;
            }
            SetDword(_excludeFromMonitor, 1);
            SetDword(_canIncludeInHistory, 0);
            SetDword(_canUploadToCloud, 0);
            return true;
        }
        finally { PInvoke.CloseClipboard(); }
    }

    private static void SetDword(uint format, uint value)
    {
        var hMem = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, sizeof(uint));
        if (hMem == HGLOBAL.Null) return;
        var p = PInvoke.GlobalLock(hMem);
        if (p is null) { PInvoke.GlobalFree(hMem); return; }
        *(uint*)p = value;
        PInvoke.GlobalUnlock(hMem);
        if (PInvoke.SetClipboardData(format, new HANDLE((nint)hMem.Value)) == HANDLE.Null) PInvoke.GlobalFree(hMem);
    }

    public void Dispose() => PInvoke.DestroyWindow(_owner);
}

public sealed record ClipboardCapture(string Text, string Source);

/// <summary>Types and pastes text into whichever window has the focus. Not thread-safe: use from one worker thread.</summary>
public sealed unsafe class TextInjector : IDisposable
{
    private const ushort VK_BACK = 0x08, VK_RETURN = 0x0D, VK_LEFT = 0x25, VK_CONTROL = 0x11, VK_V = 0x56;
    private const int TypeChunk = 24;

    private readonly ClipboardAccess _clipboard = new();

    public ClipboardAccess Clipboard => _clipboard;

    /// <summary>Waits until the user has released Shift/Ctrl/Alt/Win, so injected keys are not read as shortcuts.</summary>
    public static void WaitForModifiersReleased(int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (!(KeyTranslator.Down(0x10) || KeyTranslator.Down(0x11) || KeyTranslator.Down(0x12) ||
                  KeyTranslator.Down(0x5B) || KeyTranslator.Down(0x5C))) return;
            Thread.Sleep(10);
        }
    }

    public void Backspaces(int count) => PressRepeatedly(VK_BACK, count);

    public void Left(int count) => PressRepeatedly(VK_LEFT, count, extended: true);

    public void Enter() => PressRepeatedly(VK_RETURN, 1);

    private static void PressRepeatedly(ushort vk, int count, bool extended = false)
    {
        while (count > 0)
        {
            int batch = Math.Min(count, 32);
            var inputs = new INPUT[batch * 2];
            for (int i = 0; i < batch; i++)
            {
                inputs[2 * i] = Key(vk, up: false, extended);
                inputs[2 * i + 1] = Key(vk, up: true, extended);
            }
            Send(inputs);
            count -= batch;
            if (count > 0) Thread.Sleep(2);
        }
    }

    /// <summary>Types text as Unicode key events, so it works on any layout; "\n" becomes Enter.</summary>
    public void Type(string text)
    {
        var inputs = new List<INPUT>(TypeChunk * 2);
        void Flush()
        {
            if (inputs.Count == 0) return;
            Send(inputs.ToArray());
            inputs.Clear();
            Thread.Sleep(2);   // some apps drop characters when flooded
        }

        foreach (char c in text)
        {
            if (c == '\r') continue;
            if (c == '\n')
            {
                inputs.Add(Key(VK_RETURN, up: false));
                inputs.Add(Key(VK_RETURN, up: true));
            }
            else
            {
                inputs.Add(Unicode(c, up: false));
                inputs.Add(Unicode(c, up: true));
            }
            if (inputs.Count >= TypeChunk * 2) Flush();
        }
        Flush();
    }

    /// <summary>
    /// Pastes through the clipboard (multi-line text must not be typed: Enter would "send" in chat apps), then
    /// puts the previous text back. Returns false, without touching anything, if that cannot be done safely.
    /// </summary>
    public bool Paste(string text)
    {
        if (!_clipboard.IsSafeToReplace()) return false;
        string? previous = _clipboard.GetText();

        if (!_clipboard.SetText(text.Replace("\n", "\r\n"))) return false;
        uint ours = _clipboard.SequenceNumber;

        Send(new[]
        {
            Key(VK_CONTROL, up: false), Key(VK_V, up: false), Key(VK_V, up: true), Key(VK_CONTROL, up: true),
        });
        Thread.Sleep(180);   // the target reads the clipboard asynchronously after Ctrl+V

        // Restore only if nobody copied something else in the meantime.
        if (_clipboard.SequenceNumber == ours) _clipboard.SetText(previous);
        return true;
    }

    // --------------------------------------------------------------- inputs

    private static INPUT Key(ushort vk, bool up, bool extended = false)
    {
        var input = new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD };
        input.Anonymous.ki.wVk = (VIRTUAL_KEY)vk;
        input.Anonymous.ki.wScan = (ushort)PInvoke.MapVirtualKey(vk, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC);
        input.Anonymous.ki.dwExtraInfo = KeyboardHook.OwnMarker;
        var flags = (up ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0) | (extended ? KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY : 0);
        input.Anonymous.ki.dwFlags = flags;
        return input;
    }

    private static INPUT Unicode(char c, bool up)
    {
        var input = new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD };
        input.Anonymous.ki.wScan = c;
        input.Anonymous.ki.dwExtraInfo = KeyboardHook.OwnMarker;
        input.Anonymous.ki.dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_UNICODE | (up ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0);
        return input;
    }

    /// <summary>Replays a key the user pressed while an expansion was running, in order.</summary>
    public static void Replay(KeyEvent e)
    {
        const uint VK_PACKET = 0xE7;
        var input = new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD };
        input.Anonymous.ki.dwExtraInfo = KeyboardHook.OwnMarker;
        if (e.VirtualKey == VK_PACKET)
        {
            // A Unicode packet (IME, touch keyboard): the character travels in the scan code.
            input.Anonymous.ki.wScan = (ushort)e.ScanCode;
            input.Anonymous.ki.dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_UNICODE | (e.IsDown ? 0 : KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);
        }
        else
        {
            input.Anonymous.ki.wVk = (VIRTUAL_KEY)e.VirtualKey;
            input.Anonymous.ki.wScan = (ushort)e.ScanCode;
            input.Anonymous.ki.dwFlags = (e.IsDown ? 0 : KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP) |
                                         (e.IsExtended ? KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY : 0);
        }
        Send(new[] { input });
    }

    private static void Send(INPUT[] inputs)
    {
        uint sent = PInvoke.SendInput(inputs, sizeof(INPUT));
        if (sent != inputs.Length)
        {
            AppLog.Warn($"SendInput delivered {sent}/{inputs.Length} events (error {Marshal.GetLastWin32Error()}); " +
                        "the target is probably elevated or on a secure desktop");
        }
    }

    public void Dispose() => _clipboard.Dispose();
}
