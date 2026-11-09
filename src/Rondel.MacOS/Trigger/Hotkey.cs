using Avalonia.Input;

namespace Rondel.MacOS.Trigger;

/// <summary>
/// A global keyboard shortcut such as "Control+Option+R", as settings.json keeps it on macOS: the modifiers by name
/// and the key by its place on the keyboard (Avalonia's <see cref="PhysicalKey"/>), so it stays the same key whatever
/// the keyboard layout. On screen it reads the Mac way, ⌃⌥R.
/// </summary>
internal readonly record struct Hotkey(KeyModifiers Modifiers, PhysicalKey Key)
{
    // Carbon's modifier flags (Events.h).
    const uint CmdKey = 0x100, ShiftKey = 0x200, OptionKey = 0x800, ControlKey = 0x1000;

    /// <summary>The virtual key codes of the keys a shortcut may use (Carbon's kVK_ANSI_* and friends, by place).</summary>
    static readonly Dictionary<PhysicalKey, uint> KeyCodes = new()
    {
        [PhysicalKey.A] = 0x00, [PhysicalKey.S] = 0x01, [PhysicalKey.D] = 0x02, [PhysicalKey.F] = 0x03, [PhysicalKey.H] = 0x04,
        [PhysicalKey.G] = 0x05, [PhysicalKey.Z] = 0x06, [PhysicalKey.X] = 0x07, [PhysicalKey.C] = 0x08, [PhysicalKey.V] = 0x09,
        [PhysicalKey.B] = 0x0B, [PhysicalKey.Q] = 0x0C, [PhysicalKey.W] = 0x0D, [PhysicalKey.E] = 0x0E, [PhysicalKey.R] = 0x0F,
        [PhysicalKey.Y] = 0x10, [PhysicalKey.T] = 0x11, [PhysicalKey.O] = 0x1F, [PhysicalKey.U] = 0x20, [PhysicalKey.I] = 0x22,
        [PhysicalKey.P] = 0x23, [PhysicalKey.L] = 0x25, [PhysicalKey.J] = 0x26, [PhysicalKey.K] = 0x28, [PhysicalKey.N] = 0x2D,
        [PhysicalKey.M] = 0x2E,
        [PhysicalKey.Digit1] = 0x12, [PhysicalKey.Digit2] = 0x13, [PhysicalKey.Digit3] = 0x14, [PhysicalKey.Digit4] = 0x15,
        [PhysicalKey.Digit5] = 0x17, [PhysicalKey.Digit6] = 0x16, [PhysicalKey.Digit7] = 0x1A, [PhysicalKey.Digit8] = 0x1C,
        [PhysicalKey.Digit9] = 0x19, [PhysicalKey.Digit0] = 0x1D,
        [PhysicalKey.Space] = 0x31, [PhysicalKey.Enter] = 0x24, [PhysicalKey.Tab] = 0x30,
        [PhysicalKey.ArrowLeft] = 0x7B, [PhysicalKey.ArrowRight] = 0x7C, [PhysicalKey.ArrowDown] = 0x7D, [PhysicalKey.ArrowUp] = 0x7E,
        [PhysicalKey.F1] = 0x7A, [PhysicalKey.F2] = 0x78, [PhysicalKey.F3] = 0x63, [PhysicalKey.F4] = 0x76, [PhysicalKey.F5] = 0x60,
        [PhysicalKey.F6] = 0x61, [PhysicalKey.F7] = 0x62, [PhysicalKey.F8] = 0x64, [PhysicalKey.F9] = 0x65, [PhysicalKey.F10] = 0x6D,
        [PhysicalKey.F11] = 0x67, [PhysicalKey.F12] = 0x6F, [PhysicalKey.F13] = 0x69, [PhysicalKey.F14] = 0x6B, [PhysicalKey.F15] = 0x71,
        [PhysicalKey.F16] = 0x6A, [PhysicalKey.F17] = 0x40, [PhysicalKey.F18] = 0x4F, [PhysicalKey.F19] = 0x50, [PhysicalKey.F20] = 0x5A,
    };

    /// <summary>
    /// Shortcuts macOS keeps for itself (as <see cref="ToString"/> writes them): one of them never reaches Rondel, or takes
    /// away what everyone uses.
    /// </summary>
    static readonly HashSet<string> Reserved =
    [
        "Command+Space", "Control+Space", "Control+Option+Space", "Control+Command+Space", "Option+Command+Space",
        "Shift+Command+Digit3", "Shift+Command+Digit4", "Shift+Command+Digit5", "Shift+Command+Digit6",
        "Command+Tab", "Shift+Command+Tab", "Command+Q", "Command+W", "Command+H", "Command+M",
        "Control+ArrowLeft", "Control+ArrowRight", "Control+ArrowUp", "Control+ArrowDown", "Control+Command+Q", "Option+Command+D",
    ];

    /// <summary>A plain key, or Shift and a key, would steal ordinary typing; F-keys are fine on their own.</summary>
    public bool IsUsable =>
        (Modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0 || IsFunctionKey(Key);

    static bool IsFunctionKey(PhysicalKey key) => key.ToString() is ['F', _, ..] name && name[1..].All(char.IsAsciiDigit);

    /// <summary>macOS uses it itself (Spotlight, input sources, screenshots, switching apps…).</summary>
    public bool IsReserved => Reserved.Contains(ToString());

    public uint CarbonModifiers =>
        (Modifiers.HasFlag(KeyModifiers.Control) ? ControlKey : 0) | (Modifiers.HasFlag(KeyModifiers.Alt) ? OptionKey : 0)
        | (Modifiers.HasFlag(KeyModifiers.Shift) ? ShiftKey : 0) | (Modifiers.HasFlag(KeyModifiers.Meta) ? CmdKey : 0);

    public uint KeyCode => KeyCodes[Key];

    /// <summary>Whether a key may end a shortcut (not a modifier, and one Rondel knows the code of).</summary>
    public static bool IsShortcutKey(PhysicalKey key) => KeyCodes.ContainsKey(key);

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var modifiers = KeyModifiers.None;
        PhysicalKey? key = null;
        foreach (string part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToUpperInvariant())
            {
                case "CONTROL" or "CTRL": modifiers |= KeyModifiers.Control; break;
                case "OPTION" or "ALT": modifiers |= KeyModifiers.Alt; break;
                case "SHIFT": modifiers |= KeyModifiers.Shift; break;
                case "COMMAND" or "CMD": modifiers |= KeyModifiers.Meta; break;
                default:
                    if (key is not null || ParseKey(part) is not { } parsed)
                        return false;
                    key = parsed;
                    break;
            }
        }
        if (key is null)
            return false;
        hotkey = new Hotkey(modifiers, key.Value);
        return true;
    }

    /// <summary>
    /// A key by its name ("R", "Digit7", "F5"), or a bare digit as Windows writes it ("7"). Other numbers are refused:
    /// Enum.TryParse would read "7" as the key whose value is 7, and "R,A" as two keys at once.
    /// </summary>
    static PhysicalKey? ParseKey(string part)
    {
        if (part is [var digit] && char.IsAsciiDigit(digit))
            part = "Digit" + digit;
        else if (!part.All(char.IsAsciiLetterOrDigit) || !char.IsAsciiLetter(part[0]))
            return null;
        return Enum.TryParse(part, ignoreCase: true, out PhysicalKey key) && KeyCodes.ContainsKey(key) ? key : null;
    }

    /// <summary>As settings.json keeps it: "Control+Option+R", modifiers in Apple's order.</summary>
    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(KeyModifiers.Control))
            parts.Add("Control");
        if (Modifiers.HasFlag(KeyModifiers.Alt))
            parts.Add("Option");
        if (Modifiers.HasFlag(KeyModifiers.Shift))
            parts.Add("Shift");
        if (Modifiers.HasFlag(KeyModifiers.Meta))
            parts.Add("Command");
        parts.Add(Key.ToString());
        return string.Join('+', parts);
    }

    /// <summary>As macOS shows it: ⌃⌥⇧⌘ and the key.</summary>
    public string Display()
    {
        string symbols = Symbols(Modifiers);
        string key = Key switch
        {
            PhysicalKey.Space => "Space",
            PhysicalKey.Enter => "↩",
            PhysicalKey.Tab => "⇥",
            PhysicalKey.ArrowLeft => "←",
            PhysicalKey.ArrowRight => "→",
            PhysicalKey.ArrowUp => "↑",
            PhysicalKey.ArrowDown => "↓",
            _ when Key.ToString().StartsWith("Digit", StringComparison.Ordinal) => Key.ToString()[^1..],
            _ => Key.ToString(),
        };
        return symbols + key;
    }

    /// <summary>Modifiers as macOS shows them, in Apple's order: ⌃⌥⇧⌘.</summary>
    public static string Symbols(KeyModifiers modifiers) =>
        (modifiers.HasFlag(KeyModifiers.Control) ? "⌃" : "") + (modifiers.HasFlag(KeyModifiers.Alt) ? "⌥" : "")
        + (modifiers.HasFlag(KeyModifiers.Shift) ? "⇧" : "") + (modifiers.HasFlag(KeyModifiers.Meta) ? "⌘" : "");

    /// <summary>A stored shortcut as macOS shows it; the text itself when it does not parse.</summary>
    public static string Display(string text) => TryParse(text, out var hotkey) ? hotkey.Display() : text;
}
