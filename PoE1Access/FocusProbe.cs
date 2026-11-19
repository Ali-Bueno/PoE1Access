using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ExileCore.PoEMemory;
using ExileCore.Shared.Interfaces;

namespace PoE1Access;

/// <summary>
/// Finds how the game marks the focused menu item when it is moved with a controller or keys
/// (ExileApi's UIHover stays empty then).
///
/// A snapshot copies the first bytes of every visible UI element. Two snapshots a moment apart
/// with no input show which bytes change by themselves (animations, timers); those are ignored.
/// Comparing a snapshot with the one from the previous key press then shows which elements
/// changed because the focus moved, and at which offsets, plus elements that appeared or
/// disappeared (a highlight frame, for example).
///
/// Read-only.
/// </summary>
internal sealed class FocusProbe
{
    private const int Bytes = 0x400;
    private const int MaxElements = 20000;

    public sealed class Snapshot
    {
        public readonly Dictionary<long, (string Path, byte[] Data)> Elements = new Dictionary<long, (string, byte[])>();
        public readonly List<(string Path, string Name, string Text)> Texts = new List<(string, string, string)>();
        public DateTime Time = DateTime.Now;
    }

    public static Snapshot Take(IMemory mem, Element root)
    {
        var snap = new Snapshot();
        if (root == null || root.Address == 0) return snap;

        var stack = new Stack<(Element E, string Path)>();
        stack.Push((root, "root"));
        while (stack.Count > 0 && snap.Elements.Count < MaxElements)
        {
            var (e, path) = stack.Pop();
            if (e == null || e.Address == 0 || snap.Elements.ContainsKey(e.Address)) continue;

            bool visible = path == "root";
            if (!visible) { try { visible = e.IsVisibleLocal; } catch { } }
            if (!visible) continue;

            byte[] data = null;
            try { data = mem.ReadBytes(e.Address, Bytes); } catch { }
            if (data == null || data.Length != Bytes) continue;
            snap.Elements[e.Address] = (path, data);

            string text = UiText.Text(mem, e);
            if (text.Length > 0) snap.Texts.Add((path, UiText.Name(mem, e), text));

            IList<Element> kids = null;
            try { kids = e.Children; } catch { }
            if (kids == null || kids.Count > 3000) continue;
            for (int i = kids.Count - 1; i >= 0; i--) stack.Push((kids[i], path + "/" + i));
        }
        return snap;
    }

    // Offsets (4-byte aligned) that differ between two snapshots of the same element.
    private static List<int> Diff(byte[] a, byte[] b)
    {
        var list = new List<int>();
        for (int o = 0; o + 4 <= a.Length && o + 4 <= b.Length; o += 4)
            if (BitConverter.ToInt32(a, o) != BitConverter.ToInt32(b, o)) list.Add(o);
        return list;
    }

    // Per element, the offsets that changed between two snapshots taken with no input.
    public static Dictionary<long, HashSet<int>> Noise(Snapshot a, Snapshot b)
    {
        var noise = new Dictionary<long, HashSet<int>>();
        foreach (var kv in b.Elements)
            if (a.Elements.TryGetValue(kv.Key, out var old))
                noise[kv.Key] = new HashSet<int>(Diff(old.Data, kv.Value.Data));
        return noise;
    }

    public static string Report(IMemory mem, Element root, int number, Snapshot prev, Snapshot cur,
                                Dictionary<long, HashSet<int>> noisePrev, Dictionary<long, HashSet<int>> noiseCur)
    {
        var sb = new StringBuilder();
        sb.AppendLine("PoE1Access focus snapshot " + number + "  " + cur.Time);
        sb.AppendLine("Visible elements: " + cur.Elements.Count);

        if (prev != null)
        {
            sb.AppendLine();
            sb.AppendLine("=== elements that changed since snapshot " + (number - 1) + " (self-changing bytes removed) ===");
            int shown = 0;
            foreach (var kv in cur.Elements.OrderBy(k => k.Value.Path, StringComparer.Ordinal))
            {
                if (!prev.Elements.TryGetValue(kv.Key, out var old)) continue;
                var changed = Diff(old.Data, kv.Value.Data);
                changed.RemoveAll(o => (noisePrev != null && noisePrev.TryGetValue(kv.Key, out var n1) && n1.Contains(o)) ||
                                       (noiseCur != null && noiseCur.TryGetValue(kv.Key, out var n2) && n2.Contains(o)));
                if (changed.Count == 0) continue;

                if (++shown > 250) { sb.AppendLine("... (more not shown)"); break; }
                sb.AppendLine("[" + kv.Value.Path + "] addr=0x" + kv.Key.ToString("X") + Context(mem, kv.Key, root));
                foreach (int o in changed.Take(16))
                    sb.AppendLine("    +0x" + o.ToString("X3") + ": " + BitConverter.ToInt32(old.Data, o).ToString("X8") +
                                  " -> " + BitConverter.ToInt32(kv.Value.Data, o).ToString("X8"));
                if (changed.Count > 16) sb.AppendLine("    ... " + changed.Count + " offsets changed");
            }
            if (shown == 0) sb.AppendLine("(none)");

            sb.AppendLine();
            sb.AppendLine("=== elements that became visible ===");
            int appeared = 0;
            foreach (var kv in cur.Elements.Where(k => !prev.Elements.ContainsKey(k.Key)).OrderBy(k => k.Value.Path, StringComparer.Ordinal).Take(80))
            {
                appeared++;
                sb.AppendLine("[" + kv.Value.Path + "] addr=0x" + kv.Key.ToString("X") + Context(mem, kv.Key, root));
            }
            if (appeared == 0) sb.AppendLine("(none)");

            sb.AppendLine();
            sb.AppendLine("=== elements that became hidden ===");
            int gone = 0;
            foreach (var kv in prev.Elements.Where(k => !cur.Elements.ContainsKey(k.Key)).OrderBy(k => k.Value.Path, StringComparer.Ordinal).Take(80))
            {
                gone++;
                sb.AppendLine("[" + kv.Value.Path + "] addr=0x" + kv.Key.ToString("X"));
            }
            if (gone == 0) sb.AppendLine("(none)");
        }

        sb.AppendLine();
        sb.AppendLine("=== all visible text (+0x358), in tree order ===");
        foreach (var t in cur.Texts.Take(400))
            sb.AppendLine("[" + t.Path + "] " + (t.Name.Length > 0 ? "(" + t.Name + ") " : "") + "\"" + Short(t.Text, 140) + "\"");
        if (cur.Texts.Count == 0) sb.AppendLine("(none)");

        return sb.ToString();
    }

    // Name and text of an element, or else the first text found in its children.
    private static string Context(IMemory mem, long addr, Element root)
    {
        try
        {
            var e = root.GetObject<Element>(addr);
            string name = UiText.Name(mem, e);
            string text = UiText.Text(mem, e);
            if (text.Length == 0) text = FirstChildText(mem, e, 3);
            return (name.Length > 0 ? " name=" + name : "") + (text.Length > 0 ? " text=\"" + Short(text, 100) + "\"" : "");
        }
        catch
        {
            return "";
        }
    }

    private static string FirstChildText(IMemory mem, Element e, int depth)
    {
        if (e == null || depth <= 0) return "";
        IList<Element> kids = null;
        try { kids = e.Children; } catch { }
        if (kids == null || kids.Count > 50) return "";
        foreach (Element k in kids)
        {
            string t = UiText.Text(mem, k);
            if (t.Length > 0) return t;
            t = FirstChildText(mem, k, depth - 1);
            if (t.Length > 0) return t;
        }
        return "";
    }

    private static string Short(string s, int max)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " | ");
        return s.Length <= max ? s : s.Substring(0, max) + "...";
    }
}
