namespace Apparatus.Core.Input;

/// <summary>What the keyboard hook must do about a key, as decided by <see cref="WinTapDetector"/>.</summary>
public readonly record struct WinTapOutcome(bool Tapped, bool MaskWindowsKey)
{
    public static readonly WinTapOutcome None = new(false, false);
}

/// <summary>
/// Recognises "the Windows key pressed and released on its own". Windows opens its Start menu on that release, so
/// the hook has to hide the release from Windows by first injecting a harmless key (<see cref="MaskKey"/>): Windows then
/// sees a Win+key combination and does nothing. Anything pressed while Win is held (Win+E, Ctrl+Win...) cancels the tap.
/// The logic has no Windows dependencies so it can be tested exhaustively.
/// </summary>
public sealed class WinTapDetector
{
    public const uint VkLWin = 0x5B, VkRWin = 0x5C;

    /// <summary>An unassigned virtual key: pressing it changes nothing but it is enough to cancel Windows' Start shortcut.</summary>
    public const uint MaskKey = 0xE8;

    private bool _winDown;
    private bool _combo;

    /// <summary>True while a Win key is held.</summary>
    public bool IsWinDown => _winDown;

    public static bool IsWin(uint vk) => vk is VkLWin or VkRWin;

    public void Reset()
    {
        _winDown = false;
        _combo = false;
    }

    /// <summary>Feed every key event the hook sees (own injected keys excluded). Auto-repeat of Win is ignored.</summary>
    public WinTapOutcome Feed(uint vk, bool isDown)
    {
        if (IsWin(vk))
        {
            if (isDown)
            {
                if (!_winDown)
                {
                    _winDown = true;
                    _combo = false;
                }
                return WinTapOutcome.None;
            }

            bool tap = _winDown && !_combo;
            _winDown = false;
            _combo = false;
            return tap ? new WinTapOutcome(Tapped: true, MaskWindowsKey: true) : WinTapOutcome.None;
        }

        if (isDown && _winDown) _combo = true;
        return WinTapOutcome.None;
    }

    /// <summary>A mouse click while Win is held is also a combination (Win+click is used by some programs).</summary>
    public void MouseButtonPressed()
    {
        if (_winDown) _combo = true;
    }
}
