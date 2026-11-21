using Apparatus.Core.Actions;
using Apparatus.Core.Input;

namespace Apparatus.Core.Tests;

public class MacroRecorderTests
{
    private static KeyCombo K(string text)
    {
        Assert.True(KeyCombo.TryParse(text, out var combo), text);
        return combo;
    }

    private static string[] Summary(MacroRecorder r) => r.Steps().Select(StepCatalog.Summarize).ToArray();

    [Fact]
    public void TypedCharacters_BecomeOneTextStep()
    {
        var r = new MacroRecorder();
        long t = 1000;
        foreach (char c in "olá mundo") r.AddText(c.ToString(), t += 90);

        Assert.Equal(new[] { "Digitar: olá mundo" }, Summary(r));
    }

    [Fact]
    public void ShortcutsAndSpecialKeys_AreMergedIntoOneSendKeysStep()
    {
        var r = new MacroRecorder();
        r.AddCombo(K("Ctrl+L"), 1000);
        r.AddCombo(K("Ctrl+V"), 1100);
        r.AddCombo(K("Enter"), 1200);

        Assert.Equal(new[] { "Teclas: Ctrl+L Ctrl+V Enter" }, Summary(r));
    }

    [Fact]
    public void TextThenKeysThenText_AlternateSteps()
    {
        var r = new MacroRecorder();
        r.AddText("a", 1000);
        r.AddText("b", 1100);
        r.AddCombo(K("Tab"), 1200);
        r.AddText("c", 1300);

        Assert.Equal(new[] { "Digitar: ab", "Teclas: Tab", "Digitar: c" }, Summary(r));
    }

    [Fact]
    public void ALongPause_BecomesAWaitStep_RoundedTo50()
    {
        var r = new MacroRecorder();
        r.AddCombo(K("Ctrl+C"), 1000);
        r.AddCombo(K("Ctrl+V"), 1000 + 2030);

        Assert.Equal(new[] { "Teclas: Ctrl+C", "Aguardar 2050 ms", "Teclas: Ctrl+V" }, Summary(r));
    }

    [Fact]
    public void ShortGaps_AreNotRecorded()
    {
        var r = new MacroRecorder();
        r.AddCombo(K("Ctrl+C"), 1000);
        r.AddCombo(K("Ctrl+V"), 1000 + MacroRecorder.WaitThresholdMs);   // exactly at the threshold: still no wait

        Assert.Equal(new[] { "Teclas: Ctrl+C Ctrl+V" }, Summary(r));
    }

    [Fact]
    public void ThePauseBeforeTheFirstKey_IsIgnored()
    {
        var r = new MacroRecorder();
        r.AddText("x", 987_654);
        Assert.Equal(new[] { "Digitar: x" }, Summary(r));
    }

    [Fact]
    public void WaitIsCapped()
    {
        var r = new MacroRecorder();
        r.AddText("a", 1000);
        r.AddText("b", 1000 + 10 * 60_000);
        Assert.Equal("Aguardar 30000 ms", Summary(r)[1]);
    }

    [Fact]
    public void ThePauseSplitsTypedText()
    {
        var r = new MacroRecorder();
        r.AddText("a", 1000);
        r.AddText("b", 3000);
        Assert.Equal(new[] { "Digitar: a", "Aguardar 2000 ms", "Digitar: b" }, Summary(r));
    }

    [Fact]
    public void Backspace_ErasesTheLastTypedCharacter()
    {
        var r = new MacroRecorder();
        r.AddText("c", 1000);
        r.AddText("a", 1100);
        r.AddText("x", 1200);
        r.AddBackspace(1300);
        r.AddText("s", 1400);

        Assert.Equal(new[] { "Digitar: cas" }, Summary(r));
    }

    [Fact]
    public void Backspace_EmptyingTheText_RemovesTheStep()
    {
        var r = new MacroRecorder();
        r.AddText("q", 1000);
        r.AddBackspace(1100);
        Assert.True(r.IsEmpty);
    }

    [Fact]
    public void Backspace_WithoutTypedText_IsAKeyPress()
    {
        var r = new MacroRecorder();
        r.AddCombo(K("Ctrl+A"), 1000);
        r.AddBackspace(1100);
        Assert.Equal(new[] { "Teclas: Ctrl+A Backspace" }, Summary(r));
    }

    [Fact]
    public void Backspace_AfterAPause_IsAKeyPress_NotAnEdit()
    {
        var r = new MacroRecorder();
        r.AddText("a", 1000);
        r.AddBackspace(5000);
        Assert.Equal(new[] { "Digitar: a", "Aguardar 4000 ms", "Teclas: Backspace" }, Summary(r));
    }

    [Fact]
    public void Braces_AreEscapedSoTheyTypeLiterally()
    {
        var r = new MacroRecorder();
        foreach (char c in "{date}") r.AddText(c.ToString(), 1000);

        var step = r.Steps().Single();
        Assert.Equal("{{date}}", step.Get("text"));
        // And the template engine turns it back into the literal text.
        Assert.Equal("{date}", Core.Snippets.SnippetTemplate.Parse(step.Get("text")).Render(Core.Snippets.SnippetContext.Create(null), null).Text);
    }

    [Fact]
    public void KeysWithoutAName_AreSkippedAndCounted()
    {
        var r = new MacroRecorder();
        r.AddCombo(new KeyCombo(HotkeyModifiers.None, 0xE5), 1000);   // IME process key: no name
        r.AddCombo(K("Enter"), 1100);

        Assert.Equal(1, r.Skipped);
        Assert.Equal(new[] { "Teclas: Enter" }, Summary(r));
    }

    [Fact]
    public void EveryRecordedStep_PassesValidation()
    {
        var r = new MacroRecorder();
        long t = 1000;
        r.AddCombo(K("Win+R"), t += 50);
        r.AddText("notepad", t += 900);
        r.AddCombo(K("Enter"), t += 80);
        r.AddText("Olá {mundo}", t += 3000);
        r.AddCombo(K("Ctrl+S"), t += 100);

        Assert.All(r.Steps(), s => Assert.Null(StepCatalog.Validate(s)));
        Assert.Equal(7, r.Steps().Count);
    }

    [Fact]
    public void Steps_AreCopies()
    {
        var r = new MacroRecorder();
        r.AddText("a", 1000);
        r.Steps()[0].Args["text"] = "changed";
        Assert.Equal("a", r.Steps()[0].Get("text"));
    }
}
