using ExileCore.PoEMemory;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Interfaces;

namespace PoE1Access;

/// <summary>
/// Reads UI element strings at the offsets the text probe found on the current game version
/// (18 Nov 2026). ExileApi's own Element.Text returns leftover memory for elements that have
/// no text, so these reads check that the result is clean text and otherwise return "".
///
///   +0x358  the text the element shows ("Tarkleigh", "Full Passive Skill Reset")
///   +0x1B0  the element's internal name ("title_label", "action_button", "root")
///   +0x2C8  a panel's title ("Inventory", "Passives", "Stash")
///
/// If a game patch moves these, run the F3 text probe again and update the numbers here.
/// </summary>
internal static class UiText
{
    // How far a game patch moved these fields (bytes). The patch of 18 Nov 2026 (10 PM) added
    // 8 bytes early in the element, moving the name to +0x1B8 and the text to +0x360. Every
    // offset below follows Shift; it is learned from the UI root, whose name is always "root".
    public static int Shift;
    private static readonly int[] Shifts = { 0, 8, 16, -8, 24 };

    /// <summary>True if the element is named "root" (at any known shift; the shift is learned).</summary>
    public static bool IsRoot(IMemory mem, Element e)
    {
        if (Read(mem, e, 0x1B0 + Shift) == "root") return true;
        foreach (int s in Shifts)
        {
            if (s == Shift || Read(mem, e, 0x1B0 + s) != "root") continue;
            Shift = s;
            PluginLog.Write("UI element layout: fields moved by " + s + " bytes (learned from the UI root)");
            return true;
        }
        return false;
    }

    // ExileApi's own visibility flag is misread after the patch of 18 Nov 2026. The element's
    // fields run type (+0x1D0), parent (+0x1D8), flags (+0x1E0) before the patch, each 8 bytes
    // later after it; "shown" is bit 11 of the flags (set on the UI root and on-screen HUD,
    // clear on every closed panel, checked live).
    private const int FlagsOffset = 0x1E0, ShownBit = 11;

    /// <summary>The element's own "shown" flag (not its parents').</summary>
    public static bool VisibleLocal(IMemory mem, Element e)
    {
        if (Shift == 0) { try { return e.IsVisibleLocal; } catch { return false; } }
        try { return (mem.Read<uint>(e.Address + FlagsOffset + Shift) >> ShownBit & 1) == 1; }
        catch { return false; }
    }

    /// <summary>The element's parent. ExileApi's own Parent is misread after the patch of
    /// 18 Nov 2026; the parent pointer sits at +0x1D8 before it, +0x1E0 after (checked on
    /// 1,915 parent/child pairs).</summary>
    public static Element ParentOf(IMemory mem, Element e)
    {
        if (e == null || e.Address == 0) return null;
        if (Shift == 0) { try { return e.Parent; } catch { return null; } }
        try
        {
            long p = mem.Read<long>(e.Address + 0x1D8 + Shift);
            if (p < 0x10000 || p > 0x7FFF_FFFF_FFFF) return null;
            return e.GetObject<Element>(p);
        }
        catch { return null; }
    }

    /// <summary>The element's type. ExileApi's own is misread after the patch of 18 Nov 2026;
    /// the type (a 16-bit number) sits at +0x1D0 before it, +0x1D8 after.</summary>
    public static ElementType TypeOf(IMemory mem, Element e)
    {
        if (Shift == 0) return e.Type;
        return (ElementType)mem.Read<ushort>(e.Address + 0x1D0 + Shift);
    }

    /// <summary>Shown, with every parent shown (up to the root).</summary>
    public static bool Visible(IMemory mem, Element e)
    {
        if (Shift == 0) { try { return e.IsVisible; } catch { return false; } }
        Element cur = e;
        for (int up = 0; up < 40 && cur != null && cur.Address != 0; up++)
        {
            if (!VisibleLocal(mem, cur)) return false;
            if (IsRoot(mem, cur)) return true;
            try { cur = ParentOf(mem, cur); } catch { return false; }
        }
        return true;
    }

    public static int TextOffset => 0x358 + Shift;
    public static int NameOffset => 0x1B0 + Shift;
    public static int TitleOffset => 0x2C8 + Shift;

    // Byte that is 1 on the menu control selected with the controller or keys, 0 otherwise.
    // Only the low byte: the bytes after it hold other data (0x90001, 0x1D0001 seen).
    public static int FocusFlagOffset => 0x3F4 + Shift;

    // On a settings section ("Display Settings"): the selected row's index.
    // On a tab button: 0x100 when it is the selected tab, 0 otherwise.
    public static int SelectionOffset => 0x368 + Shift;

    // One read covering both fields above, starting at SelectionBlockStart.
    public static int SelectionBlockStart => 0x360 + Shift;
    public static int SelectionBlockSize => FocusFlagOffset - SelectionBlockStart + 4;

    public static byte[] ReadSelectionBlock(IMemory mem, Element e)
    {
        try
        {
            if (mem == null || e == null || e.Address == 0) return null;
            byte[] b = mem.ReadBytes(e.Address + SelectionBlockStart, SelectionBlockSize);
            return b != null && b.Length == SelectionBlockSize ? b : null;
        }
        catch
        {
            return null;
        }
    }

    // Settings controls (found with the F4 change logger, 18 Nov 2026). The control is the
    // element at row/1/0/0.
    public static int SliderValueOffset => 0x364 + Shift;   // int, one step per press (22 -> 21 -> 20)
    public static int CheckboxStateOffset => 0x602 + Shift; // byte, 1 = checked
    public static int DropdownIndexOffset => 0x600 + Shift; // int, selected choice (0 -> 1 when the 2nd was picked)
    public static int DropdownHighlightOffset => 0x610 + Shift; // int, highlighted choice while the list is open
    public static int DropdownOpenOffset => 0x620 + Shift;      // int, 1 while the list is open

    // Dropdown choices: the control holds a list (begin, end pointers) at +0x770. Each entry
    // is 16 bytes; its first 8 bytes point to an object whose first field is the choice name
    // ("Always Enabled", "When Key Held (A)" for Corpse Targeting).
    public static int DropdownListOffset => 0x770 + Shift;
    public const int DropdownEntrySize = 0x10;

    /// <summary>Name of choice `index` and the number of choices (0 if unknown).</summary>
    public static string DropdownChoice(IMemory mem, long control, int index, out int count)
    {
        count = 0;
        try
        {
            byte[] list = mem.ReadBytes(control + DropdownListOffset, 16);
            if (list == null || list.Length < 16) return "";
            long begin = System.BitConverter.ToInt64(list, 0);
            long end = System.BitConverter.ToInt64(list, 8);
            if (begin <= 0x10000) return "";

            long span = end - begin;
            if (span > 0 && span % DropdownEntrySize == 0 && span / DropdownEntrySize <= 200)
                count = (int)(span / DropdownEntrySize);
            if (count > 0 && index >= count) return "";

            byte[] entry = mem.ReadBytes(begin + (long)index * DropdownEntrySize, 8);
            if (entry == null || entry.Length < 8) return "";
            long item = System.BitConverter.ToInt64(entry, 0);
            if (item <= 0x10000) return "";

            byte[] str = mem.ReadBytes(item, 32);
            if (str == null || str.Length < 32) return "";
            return TextProbe.TryString(mem, str, 0) ?? "";
        }
        catch
        {
            return "";
        }
    }

    // Highlight colour: the low 16 bits are 0xFFFF on a highlighted element (the selected tab,
    // the selected character entry) and lower otherwise (0xFFD8, 0xFF99 seen).
    public static int HighlightOffset => 0x1E4 + Shift;

    public static bool Highlighted(IMemory mem, Element e)
    {
        try
        {
            byte[] b = mem.ReadBytes(e.Address + HighlightOffset, 4);
            return b != null && b.Length == 4 && (System.BitConverter.ToInt32(b, 0) & 0xFFFF) == 0xFFFF;
        }
        catch
        {
            return false;
        }
    }

    public static bool FocusFlag(byte[] block) => block != null && block[FocusFlagOffset - SelectionBlockStart] == 1;

    public static int Selection(byte[] block) =>
        block == null ? 0 : System.BitConverter.ToInt32(block, SelectionOffset - SelectionBlockStart);

    // The text an element shows. Never the text of a password box: an element whose name, or a
    // parent's name up to three levels, contains "password" reads as empty, so a typed password
    // is never spoken nor written to the log.
    public static string Text(IMemory mem, Element e)
    {
        string t = Read(mem, e, TextOffset);
        if (t.Length == 0) return t;
        Element cur = e;
        for (int up = 0; up <= 3 && cur != null && cur.Address != 0; up++)
        {
            if (Name(mem, cur).IndexOf("password", System.StringComparison.OrdinalIgnoreCase) >= 0) return "";
            try { cur = ParentOf(mem, cur); } catch { break; }
        }
        return t;
    }
    public static string Name(IMemory mem, Element e) => Read(mem, e, NameOffset);
    public static string Title(IMemory mem, Element e) => Read(mem, e, TitleOffset);

    public static string Read(IMemory mem, Element e, int offset)
    {
        if (mem == null || e == null) return "";
        try
        {
            long addr = e.Address;
            if (addr == 0) return "";
            byte[] b = mem.ReadBytes(addr + offset, 32);
            if (b == null || b.Length < 32) return "";
            // Shown text may be a bare number ("61", "0% (0%)"); names and titles never are.
            return TextProbe.TryString(mem, b, 0, offset == TextOffset) ?? "";
        }
        catch
        {
            return "";
        }
    }
}
