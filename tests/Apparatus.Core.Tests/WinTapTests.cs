using Apparatus.Core.Input;

namespace Apparatus.Core.Tests;

public class WinTapDetectorTests
{
    private const uint L = WinTapDetector.VkLWin, R = WinTapDetector.VkRWin, E = 0x45, Shift = 0x10, Ctrl = 0x11;

    [Fact]
    public void LoneTap_IsDetected_AndAsksForTheMask()
    {
        var d = new WinTapDetector();
        Assert.Equal(WinTapOutcome.None, d.Feed(L, true));
        var up = d.Feed(L, false);
        Assert.True(up.Tapped);
        Assert.True(up.MaskWindowsKey);
        Assert.False(d.IsWinDown);
    }

    [Fact]
    public void RightWinKey_Works_Too()
    {
        var d = new WinTapDetector();
        d.Feed(R, true);
        Assert.True(d.Feed(R, false).Tapped);
    }

    [Fact]
    public void AutoRepeat_DoesNotBreakTheTap()
    {
        var d = new WinTapDetector();
        d.Feed(L, true);
        for (int i = 0; i < 20; i++) Assert.False(d.Feed(L, true).Tapped);
        Assert.True(d.Feed(L, false).Tapped);
    }

    [Theory]
    [InlineData(0x45u)]   // Win+E
    [InlineData(0x44u)]   // Win+D
    [InlineData(0x10u)]   // Win+Shift...
    [InlineData(0x11u)]   // Win+Ctrl...
    [InlineData(0x25u)]   // Win+Left
    public void AnyKeyBetween_CancelsTheTap(uint other)
    {
        var d = new WinTapDetector();
        d.Feed(L, true);
        d.Feed(other, true);
        d.Feed(other, false);
        Assert.Equal(WinTapOutcome.None, d.Feed(L, false));
    }

    [Fact]
    public void ReleasingTheOtherKeyAfterWin_StillCancels()
    {
        var d = new WinTapDetector();
        d.Feed(L, true);
        d.Feed(E, true);
        d.Feed(L, false);
        d.Feed(E, false);
        // A fresh tap afterwards works again.
        d.Feed(L, true);
        Assert.True(d.Feed(L, false).Tapped);
    }

    [Fact]
    public void KeyHeldBeforeWin_AndReleasedDuringIt_DoesNotCancel()
    {
        // Only key presses (downs) count; releasing something pressed earlier is not a combination.
        var d = new WinTapDetector();
        d.Feed(Shift, true);
        d.Feed(L, true);
        d.Feed(Shift, false);
        Assert.True(d.Feed(L, false).Tapped);
    }

    [Fact]
    public void ModifierPressedWhileWinHeld_Cancels()
    {
        var d = new WinTapDetector();
        d.Feed(L, true);
        d.Feed(Ctrl, true);
        d.Feed(Ctrl, false);
        Assert.False(d.Feed(L, false).Tapped);
    }

    [Fact]
    public void MouseClickWhileWinHeld_Cancels()
    {
        var d = new WinTapDetector();
        d.Feed(L, true);
        d.MouseButtonPressed();
        Assert.False(d.Feed(L, false).Tapped);
    }

    [Fact]
    public void MouseClickWithoutWin_IsIgnored()
    {
        var d = new WinTapDetector();
        d.MouseButtonPressed();
        d.Feed(L, true);
        Assert.True(d.Feed(L, false).Tapped);
    }

    [Fact]
    public void ReleaseWithoutPress_IsNotATap()
    {
        // e.g. the hook was installed while Win was already held
        var d = new WinTapDetector();
        Assert.Equal(WinTapOutcome.None, d.Feed(L, false));
    }

    [Fact]
    public void TwoWinKeysAtOnce_TapsOnceOnLastRelease_AtMostTwice()
    {
        var d = new WinTapDetector();
        d.Feed(L, true);
        d.Feed(R, true);          // not a repeat: still down, treated as the same hold
        var first = d.Feed(L, false);
        var second = d.Feed(R, false);
        Assert.True(first.Tapped);
        Assert.False(second.Tapped, "the second release has no matching press");
    }

    [Fact]
    public void Reset_ForgetsAHeldKey()
    {
        var d = new WinTapDetector();
        d.Feed(L, true);
        d.Reset();
        Assert.False(d.IsWinDown);
        Assert.Equal(WinTapOutcome.None, d.Feed(L, false));
    }

    [Fact]
    public void MaskKey_IsNotAKeyTheUserCanPress()
    {
        // 0xE8 is documented as "unassigned": nothing on a keyboard produces it and nothing is bound to it.
        Assert.Equal(0xE8u, WinTapDetector.MaskKey);
        Assert.False(WinTapDetector.IsWin(WinTapDetector.MaskKey));
    }
}
