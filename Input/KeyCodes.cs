using System.Text;
using JdAmulet.Native;

namespace JdAmulet.Input;

/// <summary>Клавиша для отправки: скан-код (бит 0x100 = extended) + VK (нужен для PostMessage).</summary>
public readonly record struct KeyStroke(ushort Scan, byte Vk)
{
    public bool IsEmpty => Scan == 0;
    public bool Extended => (Scan & 0x100) != 0;

    public static KeyStroke From(Keys key)
    {
        Keys code = key & Keys.KeyCode;
        if (code == Keys.None) return default;
        ushort sc = KeyCodes.ToScan(code);
        return sc == 0 ? default : new KeyStroke(sc, (byte)code);
    }
}

/// <summary>Биты физически зажатых модификаторов (левый/правый раздельно), как их видит LL-хук.</summary>
public static class ModBits
{
    public const int LCtrl = 1, RCtrl = 2, LShift = 4, RShift = 8, LAlt = 16, RAlt = 32, LWin = 64, RWin = 128;
    public const int AnyAlt = LAlt | RAlt;
    public const int Releasable = LCtrl | RCtrl | LShift | RShift | LAlt | RAlt;

    // Свёрнутая маска для сравнения с комбинациями: Ctrl=1, Shift=2, Alt=4, Win=8
    public const int Ctrl = 1, Shift = 2, Alt = 4, Win = 8;

    public static int FromVk(uint vk) => vk switch
    {
        0xA2 => LCtrl, 0xA3 => RCtrl,
        0xA0 => LShift, 0xA1 => RShift,
        0xA4 => LAlt, 0xA5 => RAlt,
        0x5B => LWin, 0x5C => RWin,
        _ => 0,
    };

    public static int Collapse(int m) =>
        ((m & (LCtrl | RCtrl)) != 0 ? Ctrl : 0) |
        ((m & (LShift | RShift)) != 0 ? Shift : 0) |
        ((m & AnyAlt) != 0 ? Alt : 0) |
        ((m & (LWin | RWin)) != 0 ? Win : 0);

    /// <summary>Скан-код модификатора по биту (только Releasable).</summary>
    public static ushort Scan(int bit) => bit switch
    {
        LCtrl => 0x1D, RCtrl => 0x11D,
        LShift => 0x2A, RShift => 0x36,
        LAlt => 0x38, RAlt => 0x138,
        _ => 0,
    };
}

public static class KeyCodes
{
    public static ushort ToScan(Keys key)
    {
        uint vk = (uint)(key & Keys.KeyCode);
        if (vk == 0) return 0;
        uint r = NativeMethods.MapVirtualKeyW(vk, NativeMethods.MAPVK_VK_TO_VSC_EX);
        if (r == 0) return 0;
        ushort sc = (ushort)(r & 0xFF);
        uint prefix = r & 0xFF00;
        if (prefix == 0xE000 || prefix == 0xE100) sc |= 0x100;
        return sc;
    }

    /// <summary>Модификаторы комбинации из Keys: Ctrl=1, Shift=2, Alt=4.</summary>
    public static int ModsOf(Keys k) =>
        ((k & Keys.Control) != 0 ? ModBits.Ctrl : 0) |
        ((k & Keys.Shift) != 0 ? ModBits.Shift : 0) |
        ((k & Keys.Alt) != 0 ? ModBits.Alt : 0);

    public static string Display(Keys k)
    {
        if ((k & Keys.KeyCode) == Keys.None) return "—";
        var sb = new StringBuilder();
        if ((k & Keys.Control) != 0) sb.Append("Ctrl+");
        if ((k & Keys.Shift) != 0) sb.Append("Shift+");
        if ((k & Keys.Alt) != 0) sb.Append("Alt+");
        sb.Append(KeyName(k & Keys.KeyCode));
        return sb.ToString();
    }

    public static string KeyName(Keys c) => c switch
    {
        >= Keys.D0 and <= Keys.D9 => ((int)c - (int)Keys.D0).ToString(),
        >= Keys.NumPad0 and <= Keys.NumPad9 => "Num" + ((int)c - (int)Keys.NumPad0),
        Keys.Pause => "Pause",
        Keys.Escape => "Esc",
        Keys.Return => "Enter",
        Keys.Back => "Backspace",
        Keys.Prior => "PgUp",
        Keys.Next => "PgDn",
        Keys.Oemtilde => "`",
        Keys.OemMinus => "-",
        Keys.Oemplus => "=",
        Keys.Scroll => "ScrollLock",
        Keys.Capital => "CapsLock",
        _ => c.ToString(),
    };

    /// <summary>Клавиши панели быстрого доступа Jade Dynasty: 1–0 и F1–F8.</summary>
    public static bool IsHotbarKey(Keys k)
    {
        Keys c = k & Keys.KeyCode;
        return (c >= Keys.D0 && c <= Keys.D9) || (c >= Keys.F1 && c <= Keys.F8);
    }
}
