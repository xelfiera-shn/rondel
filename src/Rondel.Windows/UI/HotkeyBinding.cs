using System.Windows.Input;
using Rondel.Windows.Interop;

namespace Rondel.Windows.UI;

/// <summary>A global hotkey such as "Ctrl+Alt+Space", parsed with invariant key names.</summary>
internal readonly record struct HotkeyBinding(ModifierKeys Modifiers, Key Key)
{
    public uint NativeModifiers =>
        (Modifiers.HasFlag(ModifierKeys.Control) ? Native.MOD_CONTROL : 0)
        | (Modifiers.HasFlag(ModifierKeys.Alt) ? Native.MOD_ALT : 0)
        | (Modifiers.HasFlag(ModifierKeys.Shift) ? Native.MOD_SHIFT : 0)
        | (Modifiers.HasFlag(ModifierKeys.Windows) ? Native.MOD_WIN : 0);

    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    /// <summary>Plain keys and Shift+key would steal ordinary typing; F-keys are fine on their own.</summary>
    public bool IsUsable =>
        (Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0
        || Key is >= Key.F1 and <= Key.F24;

    public static bool TryParse(string? text, out HotkeyBinding binding)
    {
        binding = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var modifiers = ModifierKeys.None;
        Key? key = null;
        foreach (string raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL": modifiers |= ModifierKeys.Control; break;
                case "ALT": modifiers |= ModifierKeys.Alt; break;
                case "SHIFT": modifiers |= ModifierKeys.Shift; break;
                case "WIN" or "WINDOWS": modifiers |= ModifierKeys.Windows; break;
                default:
                    if (key is not null || !TryParseKey(raw, out var parsed))
                        return false;
                    key = parsed;
                    break;
            }
        }
        if (key is null)
            return false;
        binding = new HotkeyBinding(modifiers, key.Value);
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(Key is >= Key.D0 and <= Key.D9 ? ((char)('0' + (Key - Key.D0))).ToString() : Key.ToString());
        return string.Join("+", parts);
    }

    static bool TryParseKey(string text, out Key key)
    {
        if (text.Length == 1 && char.IsAsciiDigit(text[0]))
        {
            key = Key.D0 + (text[0] - '0');
            return true;
        }
        // Reject numbers: Enum.TryParse would map "5" to the key with value 5.
        if (int.TryParse(text, out _))
        {
            key = Key.None;
            return false;
        }
        return Enum.TryParse(text, ignoreCase: true, out key) && key != Key.None;
    }
}
