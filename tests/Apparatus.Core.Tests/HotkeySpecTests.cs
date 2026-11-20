using Apparatus.Core.Input;

namespace Apparatus.Core.Tests;

public class HotkeySpecTests
{
    [Fact]
    public void Default_IsCtrlAltSpace()
    {
        Assert.Equal("Ctrl+Alt+Space", HotkeySpec.Default.ToString());
        Assert.True(HotkeySpec.Default.IsValid);
    }

    [Theory]
    [InlineData("Ctrl+Alt+Space", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x20u)]
    [InlineData("alt+space", HotkeyModifiers.Alt, 0x20u)]
    [InlineData("Win+Shift+S", HotkeyModifiers.Win | HotkeyModifiers.Shift, 0x53u)]
    [InlineData("Ctrl+F12", HotkeyModifiers.Control, 0x7Bu)]
    [InlineData("F9", HotkeyModifiers.None, 0x78u)]
    [InlineData("Ctrl+;", HotkeyModifiers.Control, 0xBAu)]
    [InlineData("Control + 5", HotkeyModifiers.Control, 0x35u)]
    public void Parses(string text, HotkeyModifiers mods, uint vk)
    {
        Assert.True(HotkeySpec.TryParse(text, out var spec));
        Assert.Equal(mods, spec.Modifiers);
        Assert.Equal(vk, spec.VirtualKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+Alt")]
    [InlineData("A")]            // a plain letter would hijack typing
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+Banana")]
    [InlineData("Ctrl++")]
    public void Rejects_Invalid(string text) => Assert.False(HotkeySpec.TryParse(text, out _));

    [Theory]
    [InlineData("Ctrl+Alt+Space")]
    [InlineData("Alt+Space")]
    [InlineData("Ctrl+Shift+F5")]
    [InlineData("Alt+Win+\\")]
    public void RoundTrips(string text)
    {
        Assert.True(HotkeySpec.TryParse(text, out var spec));
        Assert.Equal(text, spec.ToString());
    }

    [Fact]
    public void ModifierValues_MatchWin32() =>
        Assert.Equal(1 | 2 | 4 | 8, (int)(HotkeyModifiers.Alt | HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Win));
}
