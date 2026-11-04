using System;
using System.Collections.Generic;
using System.Linq;

namespace MicStudio.App;

public enum ShortcutAction { ToggleTransmitMute, ToggleEffects, NextPreset, GainUp, GainDown, StopSending, PushToTalk }

[Flags]
public enum ShortcutModifiers { None = 0, Alt = 1, Control = 2, Shift = 4, Windows = 8 }

public sealed class ShortcutBinding
{
    public ShortcutAction Action { get; set; }
    public int VirtualKey { get; set; }
    public ShortcutModifiers Modifiers { get; set; } = ShortcutModifiers.Control | ShortcutModifiers.Alt;
    public bool Enabled { get; set; } = true;
    public ShortcutBinding Clone() => (ShortcutBinding)MemberwiseClone();
    public string DisplayText => string.Join(" + ", ModifierNames().Append(KeyName(VirtualKey)));
    private IEnumerable<string> ModifierNames()
    {
        if (Modifiers.HasFlag(ShortcutModifiers.Control)) yield return "Ctrl";
        if (Modifiers.HasFlag(ShortcutModifiers.Alt)) yield return "Alt";
        if (Modifiers.HasFlag(ShortcutModifiers.Shift)) yield return "Shift";
        if (Modifiers.HasFlag(ShortcutModifiers.Windows)) yield return "Win";
    }
    public static string KeyName(int key) => key switch
    {
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)key).ToString(),
        >= 0x70 and <= 0x87 => "F" + (key - 0x6F),
        >= 0x60 and <= 0x69 => "Num " + (key - 0x60),
        8 => "Backspace", 9 => "Tab", 13 => "Enter", 27 => "Esc", 32 => "Space",
        33 => "Page Up", 34 => "Page Down", 35 => "End", 36 => "Home",
        37 => "Left", 38 => "Up", 39 => "Right", 40 => "Down", 45 => "Insert", 46 => "Delete",
        0x6A => "Num *", 0x6B => "Num +", 0x6D => "Num -", 0x6E => "Num .", 0x6F => "Num /",
        0xBA => ";", 0xBB => "=", 0xBC => ",", 0xBD => "-", 0xBE => ".", 0xBF => "/", 0xC0 => "`",
        0xDB => "[", 0xDC => "\\", 0xDD => "]", 0xDE => "'", 0xE2 => "OEM 102",
        _ => "VK " + key.ToString("X2")
    };
    public bool TryValidate(out string code)
    {
        code = "";
        if (!Enum.IsDefined(Action)) code = "invalid_action";
        else if (((int)Modifiers & ~15) != 0) code = "invalid_modifiers";
        else if (Modifiers.HasFlag(ShortcutModifiers.Windows) || VirtualKey is 0x7B or 0x2C
            || (Modifiers.HasFlag(ShortcutModifiers.Alt) && VirtualKey is 9 or 0x73)
            || (Modifiers.HasFlag(ShortcutModifiers.Control) && VirtualKey == 27)
            || (Modifiers.HasFlag(ShortcutModifiers.Control | ShortcutModifiers.Alt) && VirtualKey == 46)) code = "reserved_key";
        else if (!IsSupportedKey(VirtualKey)) code = "invalid_key";
        else if (Action != ShortcutAction.PushToTalk && Modifiers == ShortcutModifiers.None && VirtualKey is not (>= 0x70 and <= 0x87)) code = "modifier_required";
        return code.Length == 0;
    }
    private static bool IsSupportedKey(int key) => key is 8 or 9 or 13 or 27 or 32
        or >= 33 and <= 40 or 45 or 46 or >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A
        or >= 0x60 and <= 0x6B or >= 0x6D and <= 0x6F or >= 0x70 and <= 0x87
        or >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDE or 0xE2;
}

public sealed class ShortcutPreferences
{
    public bool Enabled { get; set; }
    public bool PushToTalkEnabled { get; set; }
    public List<ShortcutBinding> Bindings { get; set; } = CreateDefaults();
    public void Normalize()
    {
        if (Bindings == null || Bindings.Count > 32 || Bindings.Any(b => b == null))
            throw new ArgumentException("Invalid shortcut preferences.");
    }
    public ShortcutPreferences Clone() => new() { Enabled = Enabled, PushToTalkEnabled = PushToTalkEnabled, Bindings = Bindings.Select(b => b.Clone()).ToList() };
    public static List<ShortcutBinding> CreateDefaults() =>
    [
        new() { Action = ShortcutAction.ToggleTransmitMute, VirtualKey = 0x4D },
        new() { Action = ShortcutAction.ToggleEffects, VirtualKey = 0x45 },
        new() { Action = ShortcutAction.NextPreset, VirtualKey = 0x50 },
        new() { Action = ShortcutAction.GainUp, VirtualKey = 0x26 },
        new() { Action = ShortcutAction.GainDown, VirtualKey = 0x28 },
        new() { Action = ShortcutAction.StopSending, VirtualKey = 0x53 },
        new() { Action = ShortcutAction.PushToTalk, VirtualKey = 0x20 }
    ];
}

public sealed record ShortcutIssue(ShortcutAction? Action, string Code, int NativeError = 0);
public sealed record ShortcutStatus(bool Enabled, int RegisteredCount, bool PttReady, IReadOnlyList<ShortcutIssue> Issues);

/// <summary>Only a fresh trigger press can open PTT. Releasing any required key closes it.</summary>
internal sealed class PushToTalkLatch
{
    public bool IsDown { get; private set; }
    private bool armed;
    public void Reset() { IsDown = false; armed = false; }
    public void Observe(bool triggerDown, bool modifiersMatch)
    {
        if (!triggerDown) { IsDown = false; armed = true; }
        else if (!modifiersMatch) { IsDown = false; armed = false; }
    }
    public void Press(bool triggerDown, bool modifiersMatch)
    {
        if (armed && triggerDown && modifiersMatch) { IsDown = true; armed = false; }
    }
}
