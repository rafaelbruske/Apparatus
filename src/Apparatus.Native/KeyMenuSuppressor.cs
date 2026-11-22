using System.Runtime.InteropServices;

namespace Apparatus.Native;

/// <summary>
/// Keeps Alt+Space from opening a window's system menu (Restore, Move, Size, Minimize...) while a hotkey is being recorded in it.
/// Windows turns Alt+Space into that menu by itself, after the application has seen the key, so handling the key in the UI is not
/// enough: the request has to be dropped in the window procedure. Recording "Alt+Space" as the launcher hotkey used to leave the
/// settings window with its system menu open.
/// </summary>
public sealed class KeyMenuSuppressor : IDisposable
{
    private const uint WM_SYSCHAR = 0x0106, WM_SYSCOMMAND = 0x0112;
    private const nint SC_KEYMENU = 0xF100;

    private delegate nint SubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data);

    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);

    private const nuint SubclassId = 0xA99A5101;

    private readonly nint _hwnd;
    private readonly Func<bool> _suppress;
    private readonly SubclassProc _proc;   // kept in a field: the native side holds only a pointer to it
    private bool _installed;

    /// <param name="suppress">Asked for every Alt+menu request; true means "drop it".</param>
    public KeyMenuSuppressor(nint hwnd, Func<bool> suppress)
    {
        _hwnd = hwnd;
        _suppress = suppress;
        _proc = Proc;
        _installed = SetWindowSubclass(hwnd, _proc, SubclassId, 0);
    }

    public bool IsInstalled => _installed;

    private nint Proc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        bool menuRequest = message == WM_SYSCOMMAND && (wParam & 0xFFF0) == SC_KEYMENU
                           || message == WM_SYSCHAR && wParam == ' ';
        if (menuRequest && _suppress()) return 0;
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (!_installed) return;
        RemoveWindowSubclass(_hwnd, _proc, SubclassId);
        _installed = false;
    }
}
