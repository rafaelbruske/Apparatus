using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Apparatus.Core;
using Apparatus.Core.Input;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Apparatus.Native;

public sealed record TrayMenuItem(int Id, string Text, bool Checked = false, bool Separator = false)
{
    public static TrayMenuItem Divider { get; } = new(0, "", false, true);
}

public readonly record struct HotkeyRegistration(bool Success, int Win32Error);

/// <summary>
/// Hidden top-level window that owns the global hotkey and the notification-area icon. It must be
/// created on a thread that pumps messages (the WinUI UI thread does), because WM_HOTKEY and tray
/// callbacks are delivered to that thread's queue.
/// </summary>
public sealed unsafe class MessageHost : IDisposable
{
    private const uint TrayCallbackMessage = PInvoke.WM_APP + 1;
    private const uint TrayIconId = 1;
    private const int HotkeyId = 0xA990;
    private const int NIN_SELECT = 0x400;
    private const int NIN_KEYSELECT = 0x401;
    private const string WindowClass = "Apparatus.MessageHost";

    private static MessageHost? s_instance;

    private readonly HWND _hwnd;
    private readonly uint _taskbarCreatedMessage;
    private HICON _icon;
    private IDisposable? _iconOwner;
    private string _tooltip = "Apparatus";
    private bool _trayAdded;
    private bool _hotkeyRegistered;

    public MessageHost()
    {
        if (s_instance is not null) throw new InvalidOperationException("Only one MessageHost per process");
        s_instance = this;

        var hInstance = PInvoke.GetModuleHandle((string?)null);
        fixed (char* cls = WindowClass)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = (HINSTANCE)hInstance.DangerousGetHandle(),
                lpszClassName = cls,
            };
            PInvoke.RegisterClassEx(in wc);
        }

        _hwnd = PInvoke.CreateWindowEx(0, WindowClass, "Apparatus", 0, 0, 0, 0, 0, HWND.Null, null, null, null);
        if (_hwnd == HWND.Null) throw new InvalidOperationException("Cannot create the message window");
        _taskbarCreatedMessage = PInvoke.RegisterWindowMessage("TaskbarCreated");
    }

    public nint Handle => _hwnd;

    /// <summary>Raised when the global hotkey is pressed.</summary>
    public event Action? HotkeyPressed;

    /// <summary>Raised on a left click on the tray icon.</summary>
    public event Action? TrayClicked;

    /// <summary>Raised with the id of the tray menu entry the user picked.</summary>
    public event Action<int>? TrayCommand;

    /// <summary>Builds the tray context menu each time it opens.</summary>
    public Func<IReadOnlyList<TrayMenuItem>>? TrayMenuProvider { get; set; }

    // ----------------------------------------------------------- hotkey

    /// <summary>Registers (replacing any previous) the global hotkey. Error 1409 means another app owns it.</summary>
    public HotkeyRegistration RegisterHotkey(HotkeySpec spec)
    {
        UnregisterHotkey();
        var mods = (Windows.Win32.UI.Input.KeyboardAndMouse.HOT_KEY_MODIFIERS)(uint)spec.Modifiers
                   | Windows.Win32.UI.Input.KeyboardAndMouse.HOT_KEY_MODIFIERS.MOD_NOREPEAT;
        bool ok = PInvoke.RegisterHotKey(_hwnd, HotkeyId, mods, spec.VirtualKey);
        int error = ok ? 0 : Marshal.GetLastWin32Error();
        _hotkeyRegistered = ok;
        if (!ok) AppLog.Warn($"RegisterHotKey({spec}) failed with error {error}");
        return new HotkeyRegistration(ok, error);
    }

    public void UnregisterHotkey()
    {
        if (!_hotkeyRegistered) return;
        PInvoke.UnregisterHotKey(_hwnd, HotkeyId);
        _hotkeyRegistered = false;
    }

    // ------------------------------------------------------------- tray

    public void ShowTrayIcon(string iconPath, string tooltip)
    {
        _tooltip = tooltip;
        int size = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSMICON);
        var handle = PInvoke.LoadImage((Windows.Win32.FreeLibrarySafeHandle?)null, iconPath,
            GDI_IMAGE_TYPE.IMAGE_ICON, size, size, IMAGE_FLAGS.LR_LOADFROMFILE);
        _iconOwner?.Dispose();
        _iconOwner = handle;   // the safe handle destroys the icon when disposed; keep it alive
        _icon = new HICON(handle.DangerousGetHandle());
        AddOrRefreshTrayIcon();
    }

    private void AddOrRefreshTrayIcon()
    {
        var nid = NewNotifyData();
        nid.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON |
                     NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP;
        nid.uCallbackMessage = TrayCallbackMessage;
        nid.hIcon = _icon;
        CopyTo(nid.szTip.AsSpan(), _tooltip);
        _trayAdded = PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, in nid);
        if (!_trayAdded) AppLog.Warn("Shell_NotifyIcon(NIM_ADD) failed: the tray icon was not created");
        else AppLog.Info("Tray icon added");
        if (_trayAdded)
        {
            nid.Anonymous.uVersion = 4;
            PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_SETVERSION, in nid);
        }
    }

    public void ShowBalloon(string title, string text)
    {
        if (!_trayAdded) return;
        var nid = NewNotifyData();
        nid.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_INFO;
        nid.dwInfoFlags = NOTIFY_ICON_INFOTIP_FLAGS.NIIF_INFO;
        CopyTo(nid.szInfoTitle.AsSpan(), title);
        CopyTo(nid.szInfo.AsSpan(), text);
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, in nid);
    }

    private NOTIFYICONDATAW NewNotifyData() => new()
    {
        cbSize = (uint)sizeof(NOTIFYICONDATAW),
        hWnd = _hwnd,
        uID = TrayIconId,
    };

    private static void CopyTo(Span<char> destination, string text)
    {
        var n = Math.Min(text.Length, destination.Length - 1);
        text.AsSpan(0, n).CopyTo(destination);
        destination[n] = '\0';
    }

    private void ShowTrayMenu()
    {
        var items = TrayMenuProvider?.Invoke();
        if (items is null || items.Count == 0) return;

        var menu = PInvoke.CreatePopupMenu();
        try
        {
            foreach (var item in items)
            {
                if (item.Separator)
                {
                    PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, default(PCWSTR));
                    continue;
                }
                var flags = MENU_ITEM_FLAGS.MF_STRING | (item.Checked ? MENU_ITEM_FLAGS.MF_CHECKED : 0);
                fixed (char* text = item.Text)
                {
                    PInvoke.AppendMenu(menu, flags, (nuint)item.Id, new PCWSTR(text));
                }
            }

            PInvoke.GetCursorPos(out var pt);
            // The menu closes on outside clicks only if our window is foreground when it opens.
            PInvoke.SetForegroundWindow(_hwnd);
            const uint TPM_RIGHTBUTTON = 0x0002, TPM_BOTTOMALIGN = 0x0020, TPM_RETURNCMD = 0x0100;
            var cmd = PInvoke.TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN,
                pt.X, pt.Y, _hwnd, null);
            PInvoke.PostMessage(_hwnd, PInvoke.WM_NULL, 0, 0);
            if (cmd.Value != 0) TrayCommand?.Invoke(cmd.Value);
        }
        finally
        {
            PInvoke.DestroyMenu(menu);
        }
    }

    // ---------------------------------------------------------- wndproc

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        var self = s_instance;
        if (self is not null)
        {
            try
            {
                if (msg == PInvoke.WM_HOTKEY && (int)wParam.Value == HotkeyId)
                {
                    self.HotkeyPressed?.Invoke();
                    return default;
                }
                if (msg == TrayCallbackMessage)
                {
                    int ev = (int)(lParam.Value & 0xFFFF);
                    if (ev is NIN_SELECT or NIN_KEYSELECT) self.TrayClicked?.Invoke();
                    else if (ev == (int)PInvoke.WM_CONTEXTMENU) self.ShowTrayMenu();
                    return default;
                }
                if (msg == self._taskbarCreatedMessage && self._icon != default)
                {
                    self.AddOrRefreshTrayIcon(); // Explorer restarted: the icon must be re-added.
                    return default;
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("MessageHost handler failed", ex);
                return default;
            }
        }
        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        UnregisterHotkey();
        if (_trayAdded)
        {
            var nid = NewNotifyData();
            PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, in nid);
            _trayAdded = false;
        }
        _iconOwner?.Dispose();
        PInvoke.DestroyWindow(_hwnd);
        s_instance = null;
    }
}
