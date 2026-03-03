namespace KeySwitcher.Core;

internal sealed class AppSettings
{
    public uint HotkeyModifierFlags { get; set; } =
        (uint)(HotkeyModifiers.Control | HotkeyModifiers.Alt);

    public int HotkeyVirtualKey { get; set; } = 0x20;

    public bool StartWithWindows { get; set; }

    public string FirstLayoutId { get; set; } = "00000409";

    public string SecondLayoutId { get; set; } = "00000419";

    public bool? UsePerWindowInputScope { get; set; }

    public Hotkey GetHotkey()
    {
        var hotkey = new Hotkey((HotkeyModifiers)HotkeyModifierFlags, HotkeyVirtualKey);
        return hotkey.IsValid ? hotkey : Hotkey.Default;
    }

    public void SetHotkey(Hotkey hotkey)
    {
        HotkeyModifierFlags = (uint)hotkey.BaseModifiers;
        HotkeyVirtualKey = hotkey.VirtualKey;
    }
}
