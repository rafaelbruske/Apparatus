using Apparatus.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Accessibility;

namespace Apparatus.Native;

public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    public bool IsEmpty => Right <= Left || Bottom <= Top;
}

/// <summary>
/// Finds the Start button of the Windows 11 taskbar through UI Automation (the XAML taskbar exposes it with the automation id
/// "StartButton"). The answer is cached and refreshed on a worker thread, so the mouse hook can ask "is this point on the
/// Start button?" without ever calling into UI Automation itself.
/// </summary>
public sealed unsafe class StartButtonLocator : IDisposable
{
    private const string StartButtonId = "StartButton";
    private static readonly string[] TrayClasses = { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    private CancellationTokenSource _cts = new();
    private readonly TimeSpan _interval;
    private Thread? _thread;
    private volatile ScreenRect[] _rects = Array.Empty<ScreenRect>();
    private int _missesInARow;

    public StartButtonLocator(TimeSpan? interval = null) => _interval = interval ?? TimeSpan.FromSeconds(1.5);

    /// <summary>The Start buttons last seen (one per taskbar that has one). Empty when none was found.</summary>
    public IReadOnlyList<ScreenRect> Rects => _rects;

    /// <summary>How many refreshes in a row found no Start button (the taskbar being restarted looks like this for a moment).</summary>
    public int MissesInARow => Volatile.Read(ref _missesInARow);

    public event Action? Refreshed;

    public bool IsOnStartButton(int x, int y)
    {
        foreach (var r in _rects) if (r.Contains(x, y)) return true;
        return false;
    }

    public void Start()
    {
        if (_thread is not null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _thread = new Thread(() => Run(token)) { IsBackground = true, Name = "Start button locator" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>Stops looking (and forgets what was found, so nothing can be hit by a stale rectangle).</summary>
    public void Stop()
    {
        if (_thread is null) return;
        _cts.Cancel();
        _thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        _rects = Array.Empty<ScreenRect>();
    }

    private void Run(CancellationToken token)
    {
        PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
        IUIAutomation* uia = null;
        try
        {
            uia = Create();
            while (!token.IsCancellationRequested)
            {
                Refresh(uia);
                token.WaitHandle.WaitOne(_interval);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("The Start button locator stopped", ex);
            _rects = Array.Empty<ScreenRect>();
            Interlocked.Increment(ref _missesInARow);
        }
        finally
        {
            if (uia is not null) uia->Release();
            PInvoke.CoUninitialize();
        }
    }

    /// <summary>One look at the taskbars from the calling thread (COM is initialised here). For tests and diagnostics.</summary>
    public static IReadOnlyList<ScreenRect> FindNow()
    {
        PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
        IUIAutomation* uia = Create();
        try { return Query(uia); }
        finally { uia->Release(); PInvoke.CoUninitialize(); }
    }

    private static IUIAutomation* Create()
    {
        Guid clsid = new("ff48dba4-60ef-4201-aa87-54103eef594e");   // CUIAutomation
        Guid iid = IUIAutomation.IID_Guid;
        void* p;
        PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, &p).ThrowOnFailure();
        return (IUIAutomation*)p;
    }

    private void Refresh(IUIAutomation* uia)
    {
        IReadOnlyList<ScreenRect> found;
        try { found = Query(uia); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            found = Array.Empty<ScreenRect>();   // Explorer restarting: the old element is gone
        }

        _rects = found.ToArray();
        if (found.Count == 0) Interlocked.Increment(ref _missesInARow);
        else Volatile.Write(ref _missesInARow, 0);
        Refreshed?.Invoke();
    }

    private static IReadOnlyList<ScreenRect> Query(IUIAutomation* uia)
    {
        var list = new List<ScreenRect>();
        foreach (var cls in TrayClasses)
        {
            var hwnd = PInvoke.FindWindow(cls, null);
            if (hwnd == HWND.Null) continue;

            IUIAutomationElement* root = null;
            IUIAutomationCondition* all = null;
            IUIAutomationElementArray* items = null;
            try
            {
                // The "friendly" overloads return the interface pointer and throw when the call fails.
                root = uia->ElementFromHandle(hwnd);
                all = uia->CreateTrueCondition();
                items = root->FindAll(TreeScope.TreeScope_Descendants, all);
                int count = items->Length;
                for (int i = 0; i < count; i++)
                {
                    IUIAutomationElement* e = items->GetElement(i);
                    try
                    {
                        BSTR id = e->CurrentAutomationId;
                        string text = id.ToString();
                        System.Runtime.InteropServices.Marshal.FreeBSTR((nint)id.Value);
                        if (text != StartButtonId) continue;

                        RECT r = e->CurrentBoundingRectangle;
                        if (r.right > r.left && r.bottom > r.top) list.Add(new ScreenRect(r.left, r.top, r.right, r.bottom));
                    }
                    finally
                    {
                        e->Release();
                    }
                }
            }
            finally
            {
                if (items is not null) items->Release();
                if (all is not null) all->Release();
                if (root is not null) root->Release();
            }
        }
        return list;
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }
}
