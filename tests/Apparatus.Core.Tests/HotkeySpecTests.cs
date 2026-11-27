using Apparatus.Core.Input;

namespace Apparatus.Core.Tests;

public class HotkeySpecTests
{
    [Fact]
    public void Default_IsAltSpace()
    {
        Assert.Equal("Alt+Space", HotkeySpec.Default.ToString());
        Assert.True(HotkeySpec.Default.IsValid);
    }

    [Fact]
    public void TheDefaultSetting_AndTheDefaultSpec_AreTheSameKeys()
    {
        // "Padrão" in the settings applies HotkeySpec.Default; a fresh install uses AppSettings.Hotkey: they must not drift apart.
        Assert.True(HotkeySpec.TryParse(new Core.Settings.AppSettings().Hotkey, out var fromSettings));
        Assert.Equal(HotkeySpec.Default, fromSettings);
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

    [Theory]
    [InlineData("Ctrl+Win+Num0", 0x60u)]
    [InlineData("Ctrl+Win+Num4", 0x64u)]
    [InlineData("Ctrl+Win+Num9", 0x69u)]
    [InlineData("Ctrl+Win+NumMultiply", 0x6Au)]
    [InlineData("Ctrl+Win+NumAdd", 0x6Bu)]
    [InlineData("Ctrl+Win+NumSubtract", 0x6Du)]
    [InlineData("Ctrl+Win+NumDecimal", 0x6Eu)]
    [InlineData("Ctrl+Win+NumDivide", 0x6Fu)]
    public void NumericKeypad_IsReadAndWrittenByName(string text, uint vk)
    {
        Assert.True(HotkeySpec.TryParse(text, out var spec));
        Assert.Equal(vk, spec.VirtualKey);
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Win, spec.Modifiers);
        Assert.Equal(text, spec.ToString());
    }

    [Fact]
    public void NumericKeypad_IsNotTheTopRowOfDigits()
    {
        Assert.True(HotkeySpec.TryParse("Ctrl+Num1", out var keypad));
        Assert.True(HotkeySpec.TryParse("Ctrl+1", out var topRow));
        Assert.NotEqual(topRow, keypad);
    }

    [Theory]
    [InlineData("Ctrl+Win+Numpad7", "Ctrl+Win+Num7")]
    [InlineData("ctrl+win+numpadsub", "Ctrl+Win+NumSubtract")]
    [InlineData("Ctrl+NumpadDot", "Ctrl+NumDecimal")]
    public void AutoHotkeySpellings_AreReadAndRewrittenByName(string text, string canonical)
    {
        Assert.True(HotkeySpec.TryParse(text, out var spec));
        Assert.Equal(canonical, spec.ToString());
    }

    [Fact]
    public void EveryKeypadKey_RecordedByAnEditor_IsReadBackFromWhatIsWritten()
    {
        // The editors store spec.ToString() and parse it later: a key that does not survive that trip cannot be used at all.
        // 0x60 to 0x6F are the keypad keys of Windows, except 0x6C, which ordinary keyboards do not have.
        for (uint vk = 0x60; vk <= 0x6F; vk++)
        {
            if (vk == 0x6C) continue;
            var recorded = new HotkeySpec(HotkeyModifiers.Control | HotkeyModifiers.Alt, vk);
            Assert.True(HotkeySpec.TryParse(recorded.ToString(), out var read), $"0x{vk:X2} was written as '{recorded}' and not read back");
            Assert.Equal(recorded, read);
            Assert.True(KeyCombo.TryParse(recorded.ToString(), out var combo), $"\"send keys\" does not read '{recorded}'");
            Assert.Equal(vk, combo.VirtualKey);
        }
    }

    [Fact]
    public void AnActionWithAKeypadHotkey_IsArmed()
    {
        var action = new Core.Actions.ActionDefinition { Id = 1, Name = "x", Hotkey = new HotkeySpec(HotkeyModifiers.Control | HotkeyModifiers.Win, 0x64).ToString() };
        var binding = Core.Actions.HotkeyBinding.From(action);
        Assert.NotNull(binding);
        Assert.Equal(0x64u, binding.Spec.VirtualKey);
    }

    [Theory]
    [InlineData("Ctrl+Num10")]
    [InlineData("Ctrl+Num+")]
    [InlineData("Ctrl+NumEnter")]    // the keypad's Enter is the same key code as Enter
    public void Rejects_UnknownKeypadNames(string text) => Assert.False(HotkeySpec.TryParse(text, out _));

    [Fact]
    public void ModifierValues_MatchWin32() =>
        Assert.Equal(1 | 2 | 4 | 8, (int)(HotkeyModifiers.Alt | HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Win));
}
