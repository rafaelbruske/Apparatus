using Apparatus.Core;

namespace Apparatus.Native;

/// <summary>
/// Opens Apparatus' Start menu when the Start button of the taskbar is clicked, and keeps the click from reaching the taskbar.
/// The button's position comes from <see cref="StartButtonLocator"/> (cached, refreshed in the background); the mouse hook only
/// compares the pointer with it. Only the primary mouse button is involved; touch and pen are left to Windows.
/// </summary>
public sealed class StartClickInterceptor : IDisposable
{
    // The Start button is a small square-ish target; a rectangle much bigger than that is not it.
    private const int MaxSide = 400;

    private readonly MouseHook _hook = new();
    private readonly StartButtonLocator _locator = new();
    private readonly Func<bool> _canIntercept;
    private bool _swallowedDown;   // hook thread only
    private bool _active;

    /// <param name="canIntercept">Asked on every press; when it says no the click goes to Windows.</param>
    public StartClickInterceptor(Func<bool>? canIntercept = null)
    {
        _canIntercept = canIntercept ?? (() => true);
        _hook.Handler = OnMouse;
    }

    /// <summary>Raised on the hook thread when the Start button was clicked. Must return quickly.</summary>
    public event Action? Clicked;

    public bool IsActive => _active;

    /// <summary>The Start buttons currently known (empty when the taskbar's button cannot be found).</summary>
    public IReadOnlyList<ScreenRect> ButtonRects => _locator.Rects;

    public int MissesInARow => _locator.MissesInARow;

    public void SetActive(bool active)
    {
        if (active == _active) return;
        _active = active;
        if (active)
        {
            _locator.Start();
            _hook.Start();
        }
        else
        {
            _hook.Stop();
            _locator.Stop();
            _swallowedDown = false;
        }
    }

    private bool OnMouse(MouseButtonEvent e)
    {
        if (e.IsDown)
        {
            // The cheap test first: the policy may ask the shell, and this runs on every left click anywhere on the screen.
            if (!IsOnButton(e.X, e.Y) || !_canIntercept())
            {
                _swallowedDown = false;
                return false;
            }
            _swallowedDown = true;
            return true;
        }

        if (!_swallowedDown) return false;
        _swallowedDown = false;
        if (IsOnButton(e.X, e.Y)) Clicked?.Invoke();   // released over the button: a click (dragging away cancels it)
        return true;
    }

    private bool IsOnButton(int x, int y)
    {
        foreach (var r in _locator.Rects)
        {
            if (r.Right - r.Left > MaxSide || r.Bottom - r.Top > MaxSide) continue;
            if (r.Contains(x, y)) return true;
        }
        return false;
    }

    public void Dispose()
    {
        SetActive(false);
        _locator.Dispose();
    }
}
