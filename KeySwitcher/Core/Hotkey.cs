using Avalonia.Input;

namespace KeySwitcher.Core;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0x0000,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
    NoRepeat = 0x4000,
}

public readonly record struct Hotkey(HotkeyModifiers Modifiers, int VirtualKey)
{
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLwin = 0x5B;
    private const int VkRwin = 0x5C;

    public static Hotkey Default => new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x20);

    public HotkeyModifiers BaseModifiers => Modifiers & ~HotkeyModifiers.NoRepeat;

    public bool IsValid => VirtualKey != 0 && BaseModifiers != HotkeyModifiers.None;

    public bool TryGetNativeRegistration(out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;

        if (!IsValid)
        {
            return false;
        }

        if (TryGetModifierTrigger(VirtualKey, out var triggerModifier))
        {
            var remainingModifiers = BaseModifiers & ~triggerModifier;
            if (remainingModifiers == HotkeyModifiers.None)
            {
                return false;
            }

            modifiers = (uint)(remainingModifiers | HotkeyModifiers.NoRepeat);
            virtualKey = (uint)VirtualKey;
            return true;
        }

        modifiers = (uint)(BaseModifiers | HotkeyModifiers.NoRepeat);
        virtualKey = (uint)VirtualKey;
        return true;
    }

    public override string ToString()
    {
        if (!IsValid)
        {
            return string.Empty;
        }

        if (TryGetModifierTrigger(VirtualKey, out var triggerModifier))
        {
            var allModifiers = BaseModifiers | triggerModifier;
            return FormatModifiers(allModifiers);
        }

        var modifiersText = FormatModifiers(BaseModifiers);
        var keyText = FormatVirtualKey(VirtualKey);

        if (string.IsNullOrEmpty(modifiersText))
        {
            return keyText;
        }

        return $"{modifiersText}+{keyText}";
    }

    public static bool TryFromAvaloniaKey(Key key, out int virtualKey)
    {
        virtualKey = key switch
        {
            Key.Back => 0x08,
            Key.Tab => 0x09,
            Key.Enter => 0x0D,
            Key.Escape => 0x1B,
            Key.Space => 0x20,
            Key.PageUp => 0x21,
            Key.PageDown => 0x22,
            Key.End => 0x23,
            Key.Home => 0x24,
            Key.Left => 0x25,
            Key.Up => 0x26,
            Key.Right => 0x27,
            Key.Down => 0x28,
            Key.Insert => 0x2D,
            Key.Delete => 0x2E,
            Key.LeftShift or Key.RightShift => VkShift,
            Key.LeftCtrl or Key.RightCtrl => VkControl,
            Key.LeftAlt or Key.RightAlt => VkMenu,
            Key.LWin => VkLwin,
            Key.RWin => VkRwin,
            Key.OemSemicolon => 0xBA,
            Key.OemPlus => 0xBB,
            Key.OemComma => 0xBC,
            Key.OemMinus => 0xBD,
            Key.OemPeriod => 0xBE,
            Key.OemQuestion => 0xBF,
            Key.OemTilde => 0xC0,
            Key.OemOpenBrackets => 0xDB,
            Key.OemPipe => 0xDC,
            Key.OemCloseBrackets => 0xDD,
            Key.OemQuotes => 0xDE,
            Key.Oem8 => 0xDF,
            Key.Oem102 => 0xE2,
            _ => 0,
        };

        if (virtualKey != 0)
        {
            return true;
        }

        var name = key.ToString();

        if (name.Length == 1 && name[0] is >= 'A' and <= 'Z')
        {
            virtualKey = name[0];
            return true;
        }

        if (name.Length == 2 && name[0] == 'D' && name[1] is >= '0' and <= '9')
        {
            virtualKey = 0x30 + (name[1] - '0');
            return true;
        }

        if (
            name.StartsWith("NumPad", StringComparison.Ordinal)
            && int.TryParse(name.AsSpan("NumPad".Length), out var numPad)
            && numPad is >= 0 and <= 9
        )
        {
            virtualKey = 0x60 + numPad;
            return true;
        }

        if (
            name.StartsWith('F')
            && int.TryParse(name.AsSpan(1), out var function)
            && function is >= 1 and <= 24
        )
        {
            virtualKey = 0x70 + function - 1;
            return true;
        }

        return false;
    }

    private static bool TryGetModifierTrigger(int virtualKey, out HotkeyModifiers modifier)
    {
        switch (virtualKey)
        {
            case VkShift:
                modifier = HotkeyModifiers.Shift;
                return true;

            case VkControl:
                modifier = HotkeyModifiers.Control;
                return true;

            case VkMenu:
                modifier = HotkeyModifiers.Alt;
                return true;

            case VkLwin:
            case VkRwin:
                modifier = HotkeyModifiers.Win;
                return true;

            default:
                modifier = HotkeyModifiers.None;
                return false;
        }
    }

    private static string FormatVirtualKey(int virtualKey)
    {
        if (virtualKey is >= 0x41 and <= 0x5A)
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= 0x30 and <= 0x39)
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return $"F{virtualKey - 0x70 + 1}";
        }

        if (virtualKey is >= 0x60 and <= 0x69)
        {
            return $"Num{virtualKey - 0x60}";
        }

        return virtualKey switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x1B => "Esc",
            0x20 => "Space",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2D => "Insert",
            0x2E => "Delete",
            VkShift => "Shift",
            VkControl => "Ctrl",
            VkMenu => "Alt",
            VkLwin or VkRwin => "Win",
            0xBA => ";",
            0xBB => "+",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            0xDF => "OEM8",
            0xE2 => "OEM102",
            _ => $"VK_{virtualKey:X2}",
        };
    }

    private static string FormatModifiers(HotkeyModifiers modifiers)
    {
        var parts = new List<string>(4);

        if (modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Win))
        {
            parts.Add("Win");
        }

        return string.Join("+", parts);
    }
}
