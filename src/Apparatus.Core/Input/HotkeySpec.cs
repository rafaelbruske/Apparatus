namespace Apparatus.Core.Input;

/// <summary>Values match the Win32 MOD_* constants used by RegisterHotKey.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>A global hotkey such as "Ctrl+Alt+Space". Parses from / formats to its text form.</summary>
public readonly record struct HotkeySpec(HotkeyModifiers Modifiers, uint VirtualKey)
{
    public static readonly HotkeySpec Default = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x20);

    private static readonly (string Name, uint Vk)[] NamedKeys =
    {
        ("Space", 0x20), ("Enter", 0x0D), ("Tab", 0x09), ("Esc", 0x1B), ("Backspace", 0x08),
        ("Insert", 0x2D), ("Delete", 0x2E), ("Home", 0x24), ("End", 0x23), ("PageUp", 0x21), ("PageDown", 0x22),
        ("Up", 0x26), ("Down", 0x28), ("Left", 0x25), ("Right", 0x27), ("Pause", 0x13),
        (";", 0xBA), ("=", 0xBB), (",", 0xBC), ("-", 0xBD), (".", 0xBE), ("/", 0xBF),
        ("`", 0xC0), ("[", 0xDB), ("\\", 0xDC), ("]", 0xDD), ("'", 0xDE),
    };

    /// <summary>A hotkey needs a modifier, except for function keys.</summary>
    public bool IsValid =>
        VirtualKey != 0 && (Modifiers != HotkeyModifiers.None || IsFunctionKey(VirtualKey));

    public static bool IsFunctionKey(uint vk) => vk is >= 0x70 and <= 0x87;

    public static bool IsModifierKey(uint vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    public static bool TryParse(string? text, out HotkeySpec spec)
    {
        spec = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var modifiers = HotkeyModifiers.None;
        uint vk = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries))
        {
            if (raw.Length == 0)
            {
                // "Ctrl++" means the '+' key is not supported; reject empties.
                return false;
            }
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= HotkeyModifiers.Control; continue;
                case "alt": modifiers |= HotkeyModifiers.Alt; continue;
                case "shift": modifiers |= HotkeyModifiers.Shift; continue;
                case "win": case "windows": case "meta": modifiers |= HotkeyModifiers.Win; continue;
            }
            if (vk != 0 || !TryParseKey(raw, out vk)) return false;
        }

        spec = new HotkeySpec(modifiers, vk);
        return spec.IsValid;
    }

    public static bool TryParseKey(string name, out uint vk)
    {
        vk = 0;
        if (name.Length == 1 && char.IsAsciiLetter(name[0])) { vk = char.ToUpperInvariant(name[0]); return true; }
        if (name.Length == 1 && char.IsAsciiDigit(name[0])) { vk = name[0]; return true; }
        if ((name[0] is 'F' or 'f') && int.TryParse(name.AsSpan(1), out int f) && f is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + f - 1);
            return true;
        }
        foreach (var (n, v) in NamedKeys)
        {
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) { vk = v; return true; }
        }
        return false;
    }

    public static string KeyName(uint vk)
    {
        if (vk is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39) return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return $"F{vk - 0x70 + 1}";
        foreach (var (n, v) in NamedKeys) if (v == vk) return n;
        return $"0x{vk:X2}";
    }

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(KeyName(VirtualKey));
        return string.Join('+', parts);
    }
}
