namespace Apparatus.Core.Input;

/// <summary>A key press with modifiers, as sent by a SendKeys step. Unlike a hotkey, no modifier is required.</summary>
public readonly record struct KeyCombo(HotkeyModifiers Modifiers, uint VirtualKey)
{
    /// <summary>Parses one combination: "Ctrl+Shift+T", "Enter", "Alt+Left".</summary>
    public static bool TryParse(string? text, out KeyCombo combo)
    {
        combo = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var modifiers = HotkeyModifiers.None;
        uint vk = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries))
        {
            if (raw.Length == 0) return false;
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= HotkeyModifiers.Control; continue;
                case "alt": modifiers |= HotkeyModifiers.Alt; continue;
                case "shift": modifiers |= HotkeyModifiers.Shift; continue;
                case "win": case "windows": case "meta": modifiers |= HotkeyModifiers.Win; continue;
            }
            if (vk != 0 || !HotkeySpec.TryParseKey(raw, out vk)) return false;
        }
        if (vk == 0) return false;
        combo = new KeyCombo(modifiers, vk);
        return true;
    }

    /// <summary>Parses several combinations separated by spaces: "Ctrl+C Alt+Tab Enter".</summary>
    public static bool TryParseSequence(string? text, out IReadOnlyList<KeyCombo> sequence)
    {
        var list = new List<KeyCombo>();
        sequence = list;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryParse(token, out var combo)) return false;
            list.Add(combo);
        }
        return list.Count > 0;
    }

    public override string ToString() => new HotkeySpec(Modifiers, VirtualKey).ToString();
}
