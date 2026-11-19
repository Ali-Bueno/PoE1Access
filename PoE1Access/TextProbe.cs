using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ExileCore.PoEMemory;
using ExileCore.Shared.Interfaces;

namespace PoE1Access;

/// <summary>
/// Finds where the game really keeps UI element text, because ExileApi's own Element.Text
/// reads garbage on the current game version.
///
/// For each element it reads the first bytes of the element and tries every 8-byte-aligned
/// offset as a game string (MSVC std::wstring layout: 16-byte buffer-or-pointer, then length,
/// then capacity; short strings live inline). It also follows each pointer one level and scans
/// the first bytes of what it points to. A candidate counts only if it decodes to clean,
/// mostly-Latin text. The summary ranks offsets by how many elements gave clean text there.
///
/// Read-only: it only reads memory through ExileApi.
/// </summary>
internal static class TextProbe
{
    private const int ElementScanBytes = 0x400;
    private const int PointerScanBytes = 0x80;
    private const int MaxElements = 150;
    private const int MaxChars = 2000;

    private sealed class Hit
    {
        public string Key = "";
        public string Text = "";
        public string Where = "";
    }

    public static string Run(IMemory mem, List<(string Label, Element Root)> roots, string expected, out int candidateCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine("PoE1Access text probe " + DateTime.Now);
        sb.AppendLine("Expected text (from item data, if an item was hovered): " + (expected.Length > 0 ? expected : "(none)"));
        sb.AppendLine();

        // Gather elements: a few parents of each root, the root, then its descendants.
        var elements = new List<(string Where, Element E)>();
        var seen = new HashSet<long>();
        foreach (var (label, root) in roots)
        {
            if (root == null || root.Address == 0) continue;

            Element p = SafeParent(root);
            for (int i = 1; i <= 3 && p != null && p.Address != 0; i++)
            {
                if (seen.Add(p.Address)) elements.Add((label + " parent " + i, p));
                p = SafeParent(p);
            }

            var queue = new Queue<(Element E, string Path, int Depth)>();
            queue.Enqueue((root, label, 0));
            while (queue.Count > 0 && elements.Count < MaxElements)
            {
                var (e, path, depth) = queue.Dequeue();
                if (e == null || e.Address == 0 || !seen.Add(e.Address)) continue;
                elements.Add((path, e));
                if (depth >= 5) continue;

                IList<Element> kids = null;
                try { kids = e.Children; } catch { }
                if (kids == null) continue;
                int i = 0;
                foreach (Element k in kids) queue.Enqueue((k, path + "/" + i++, depth + 1));
            }
        }

        var hitsByKey = new Dictionary<string, List<Hit>>();
        var perElement = new StringBuilder();

        foreach (var (where, e) in elements)
        {
            byte[] bytes = SafeRead(mem, e.Address, ElementScanBytes);
            if (bytes == null) continue;

            var hits = new List<Hit>();

            for (int o = 0; o + 32 <= bytes.Length; o += 8)
            {
                string t = TryString(mem, bytes, o);
                if (t != null) hits.Add(new Hit { Key = "direct +0x" + o.ToString("X3"), Text = t, Where = where });
            }

            for (int o = 0; o + 8 <= bytes.Length; o += 8)
            {
                long ptr = BitConverter.ToInt64(bytes, o);
                if (!Plausible(ptr) || ptr == e.Address) continue;
                byte[] sub = SafeRead(mem, ptr, PointerScanBytes);
                if (sub == null) continue;
                for (int q = 0; q + 32 <= sub.Length; q += 8)
                {
                    string t = TryString(mem, sub, q);
                    if (t != null)
                        hits.Add(new Hit { Key = "ptr +0x" + o.ToString("X3") + " -> +0x" + q.ToString("X2"), Text = t, Where = where });
                }
            }

            string apiText = "";
            try { apiText = e.Text ?? ""; } catch { }

            perElement.AppendLine("[" + where + "] addr=0x" + e.Address.ToString("X") + " exileApiText=\"" + Short(apiText, 80) + "\"");
            foreach (Hit h in hits)
            {
                perElement.AppendLine("    " + h.Key + "  \"" + Short(h.Text, 200) + "\"" +
                                      (expected.Length > 0 && h.Text.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0 ? "   <== EXPECTED" : ""));
                if (!hitsByKey.TryGetValue(h.Key, out var list)) hitsByKey[h.Key] = list = new List<Hit>();
                list.Add(h);
            }
        }

        candidateCount = hitsByKey.Count;

        sb.AppendLine("=== summary: offsets ranked by how many elements gave clean text there ===");
        sb.AppendLine("Elements scanned: " + elements.Count);
        foreach (var kv in hitsByKey.OrderByDescending(k => k.Value.Select(h => h.Where).Distinct().Count()).Take(40))
        {
            int count = kv.Value.Select(h => h.Where).Distinct().Count();
            bool hasExpected = expected.Length > 0 &&
                               kv.Value.Any(h => h.Text.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0);
            sb.AppendLine(kv.Key.PadRight(24) + " elements=" + count + (hasExpected ? "  CONTAINS EXPECTED" : ""));
            foreach (Hit h in kv.Value.Take(4))
                sb.AppendLine("      e.g. [" + h.Where + "] \"" + Short(h.Text, 120) + "\"");
        }

        sb.AppendLine();
        sb.AppendLine("=== per element ===");
        sb.Append(perElement);
        return sb.ToString();
    }

    /// <summary>
    /// Walks the whole UI tree (visible or not) and tries every 8-byte offset of every element
    /// as an inline game string (no pointer following, to stay fast). Reports which offsets give
    /// clean text in many elements, every element whose text contains the expected string, and
    /// which offsets of the hovered element point at those elements or their parents (that is
    /// the real tooltip link).
    /// </summary>
    public static string RunGlobal(IMemory mem, Element root, Element hover, string expected, int maxElements)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("############ WHOLE-UI SCAN ############");
        if (root == null || root.Address == 0) { sb.AppendLine("(no UI root)"); return sb.ToString(); }

        var byOffset = new Dictionary<int, List<(string Path, string Text, bool Visible)>>();
        var matches = new List<(string Path, long Addr, int Offset, string Text, bool Visible, List<long> Chain)>();
        var seen = new HashSet<long>();
        int scanned = 0;

        var stack = new Stack<(Element E, string Path, List<long> Chain)>();
        stack.Push((root, "root", new List<long>()));
        while (stack.Count > 0 && scanned < maxElements)
        {
            var (e, path, chain) = stack.Pop();
            if (e == null || e.Address == 0 || !seen.Add(e.Address)) continue;
            scanned++;

            bool visible = false;
            try { visible = e.IsVisibleLocal; } catch { }
            // Hidden subtrees filled the element limit before; skip them so the scan reaches
            // what is actually on screen (such as the item tooltip).
            if (!visible && path != "root") continue;

            byte[] bytes = SafeRead(mem, e.Address, ElementScanBytes);
            if (bytes != null)
            {
                for (int o = 0; o + 32 <= bytes.Length; o += 8)
                {
                    string t = TryString(mem, bytes, o);
                    if (t == null) continue;
                    if (!byOffset.TryGetValue(o, out var list)) byOffset[o] = list = new List<(string, string, bool)>();
                    list.Add((path, t, visible));
                    if (expected.Length > 0 && t.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0)
                        matches.Add((path, e.Address, o, t, visible, new List<long>(chain) { e.Address }));
                }
            }

            IList<Element> kids = null;
            try { kids = e.Children; } catch { }
            if (kids == null || kids.Count > 3000) continue;
            var childChain = new List<long>(chain) { e.Address };
            if (childChain.Count > 12) childChain.RemoveAt(0);
            for (int i = kids.Count - 1; i >= 0; i--) stack.Push((kids[i], path + "/" + i, childChain));
        }

        sb.AppendLine("Elements scanned: " + scanned + (scanned >= maxElements ? " (limit reached)" : ""));
        sb.AppendLine();
        sb.AppendLine("=== inline-string offsets, ranked by element count (visible count in brackets) ===");
        foreach (var kv in byOffset.OrderByDescending(k => k.Value.Count).Take(25))
        {
            int vis = kv.Value.Count(x => x.Visible);
            int distinct = kv.Value.Select(x => x.Text).Distinct().Count();
            sb.AppendLine("+0x" + kv.Key.ToString("X3") + "  elements=" + kv.Value.Count + " (visible " + vis + ")  distinct texts=" + distinct);
            foreach (var x in kv.Value.Where(x => x.Visible).Concat(kv.Value.Where(x => !x.Visible))
                         .GroupBy(x => x.Text).Select(g => g.First()).Take(8))
                sb.AppendLine("      " + (x.Visible ? "V " : "  ") + "[" + x.Path + "] \"" + Short(x.Text, 110) + "\"");
        }

        sb.AppendLine();
        sb.AppendLine("=== all visible text (+0x" + UiText.TextOffset.ToString("X") + "), in tree order ===");
        if (byOffset.TryGetValue(UiText.TextOffset, out var texts))
            foreach (var x in texts.Where(x => x.Visible).Take(400))
                sb.AppendLine("  [" + x.Path + "] \"" + Short(x.Text, 120) + "\"");

        sb.AppendLine();
        sb.AppendLine("=== elements whose text contains the expected text \"" + expected + "\" ===");
        if (matches.Count == 0) sb.AppendLine("(none found)");
        foreach (var m in matches.Take(30))
            sb.AppendLine((m.Visible ? "V " : "  ") + "[" + m.Path + "] addr=0x" + m.Addr.ToString("X") +
                          " +0x" + m.Offset.ToString("X3") + " \"" + Short(m.Text, 160) + "\"");

        // Which 8-byte slots of the hovered element hold the address of a matching element or
        // one of its ancestors? Distance = how many levels above the matching element.
        if (hover != null && hover.Address != 0 && matches.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("=== pointers in the hovered element that lead to the expected text ===");
            byte[] hb = SafeRead(mem, hover.Address, ElementScanBytes);
            if (hb != null)
            {
                var targets = new Dictionary<long, string>();
                foreach (var m in matches)
                    for (int up = 0; up < m.Chain.Count && up < 8; up++)
                    {
                        long a = m.Chain[m.Chain.Count - 1 - up];
                        if (!targets.ContainsKey(a)) targets[a] = "match [" + m.Path + "] up " + up;
                    }
                int found = 0;
                for (int o = 0; o + 8 <= hb.Length; o += 8)
                {
                    long v = BitConverter.ToInt64(hb, o);
                    if (targets.TryGetValue(v, out string what))
                    {
                        sb.AppendLine("hover +0x" + o.ToString("X3") + " -> 0x" + v.ToString("X") + "  (" + what + ")");
                        found++;
                    }
                }
                if (found == 0) sb.AppendLine("(no direct pointer from the hovered element)");
            }
        }

        return sb.ToString();
    }

    private static readonly System.Text.RegularExpressions.Regex ValueHint = new System.Text.RegularExpressions.Regex(
        "=\"([^\"]*(1280|720|DirectX|Vulkan|Windowed|Fullscreen|%|[0-9]+\\.?[0-9]*)[^\"]*|On|Off|Yes|No)\"");

    /// <summary>
    /// Lists every element under `top` (visible or not) with every clean string found in it,
    /// directly or one pointer away. Used to find where control values (dropdown choice,
    /// slider number, checkbox state) are stored.
    /// </summary>
    // Reads up to `size` bytes, falling back to smaller sizes if the end of the element runs
    // into unreadable memory.
    internal static byte[] ReadUpTo(IMemory mem, long addr, int size)
    {
        for (int s = size; s >= ElementScanBytes; s /= 2)
        {
            byte[] b = SafeRead(mem, addr, s);
            if (b != null) return b;
        }
        return null;
    }

    public static string RunDeep(IMemory mem, Element top, long focusAddr, int maxElements, int scanBytes = ElementScanBytes, int ptrBytes = PointerScanBytes)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("############ DEEP SCAN AROUND THE SELECTED CONTROL (" + scanBytes + " bytes per element) ############");
        if (top == null || top.Address == 0) { sb.AppendLine("(nothing selected yet: move the selection once, then press F3)"); return sb.ToString(); }

        var hintLines = new List<string>();
        int count = 0;
        var stack = new Stack<(Element E, string Path, int Depth)>();
        stack.Push((top, "top", 0));
        var seen = new HashSet<long>();
        while (stack.Count > 0 && count < maxElements)
        {
            var (e, path, depth) = stack.Pop();
            if (e == null || e.Address == 0 || !seen.Add(e.Address)) continue;
            count++;

            bool vis = false;
            try { vis = e.IsVisibleLocal; } catch { }
            byte[] bytes = ReadUpTo(mem, e.Address, scanBytes);

            var line = new StringBuilder();
            line.Append(new string(' ', Math.Min(depth, 30) * 2)).Append('[').Append(path).Append(']');
            if (e.Address == focusAddr) line.Append(" <== SELECTED");
            line.Append(vis ? " V" : " hidden");
            if (bytes != null)
            {
                line.Append(" bytes=0x").Append(bytes.Length.ToString("X")).Append(" flag3F4=").Append(bytes[0x3F4]).Append(" sel368=").Append(BitConverter.ToInt32(bytes, 0x368).ToString("X"));

                foreach (var kv in DirectStrings(mem, bytes))
                    line.Append(" | +0x").Append(kv.Key.ToString("X3")).Append("=\"").Append(Short(kv.Value, 70)).Append('"');

                for (int o = 0; o + 8 <= bytes.Length; o += 8)
                {
                    long ptr = BitConverter.ToInt64(bytes, o);
                    if (!Plausible(ptr) || ptr == e.Address) continue;
                    byte[] sub = SafeRead(mem, ptr, ptrBytes) ?? SafeRead(mem, ptr, PointerScanBytes);
                    if (sub == null) continue;
                    for (int q = 0; q + 32 <= sub.Length; q += 8)
                    {
                        string t = TryString(mem, sub, q);
                        if (t == null || t.StartsWith("Art/") || t.StartsWith("Audio/") || t.StartsWith("FONT") ||
                            t == "Fontin" || t == "Fontin Smallcaps" || t == "OptimusPrincepsSemiBold") continue;
                        line.Append(" | ptr+0x").Append(o.ToString("X3")).Append("->+0x").Append(q.ToString("X2"))
                            .Append("=\"").Append(Short(t, 70)).Append('"');
                    }
                }
            }
            string l = line.ToString();
            sb.AppendLine(l);
            if (ValueHint.IsMatch(System.Text.RegularExpressions.Regex.Replace(l, "\"(Audio|Art)/[^\"]*\"", "")))
                hintLines.Add(l.Trim());

            IList<Element> kids = null;
            try { kids = e.Children; } catch { }
            if (kids == null || kids.Count > 500) continue;
            for (int i = kids.Count - 1; i >= 0; i--) stack.Push((kids[i], path + "/" + i, depth + 1));
        }

        sb.AppendLine();
        sb.AppendLine("Elements: " + count + (count >= maxElements ? " (limit reached)" : ""));
        sb.AppendLine();
        sb.AppendLine("=== lines that look like values (1280, 720, DirectX, Vulkan, Windowed, Fullscreen, %, On, Off) ===");
        foreach (string h in hintLines.Take(80)) sb.AppendLine(h);
        if (hintLines.Count == 0) sb.AppendLine("(none)");
        return sb.ToString();
    }

    private static bool IsNoise(string t) =>
        t.StartsWith("Art/") || t.StartsWith("Audio/") || t.StartsWith("FONT") || t == "Fontin" ||
        t == "Fontin Smallcaps" || t == "OptimusPrincepsSemiBold" || t == "button" || t == "button_up" ||
        t == "button_down" || t == "button_end" || t == "scrollbar" || t == "textField";

    /// <summary>
    /// Every clean string reachable from a control within two pointer hops (control bytes ->
    /// pointer -> bytes -> pointer -> bytes). Used to find a dropdown's choice names.
    /// </summary>
    public static string RunTwoHop(IMemory mem, Element control, string label)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("############ TWO-HOP STRINGS FROM THE SELECTED CONTROL (" + label + ") ############");
        if (control == null || control.Address == 0) { sb.AppendLine("(no control)"); return sb.ToString(); }

        byte[] root = ReadUpTo(mem, control.Address, 0x800);
        if (root == null) { sb.AppendLine("(unreadable)"); return sb.ToString(); }
        sb.AppendLine("control addr=0x" + control.Address.ToString("X") + " bytes=0x" + root.Length.ToString("X") +
                      " int600=" + (root.Length >= 0x604 ? BitConverter.ToInt32(root, 0x600).ToString() : "?"));

        var found = new Dictionary<string, string>();   // text -> first place found
        var visited = new HashSet<long>();

        void Scan(byte[] b, string where)
        {
            for (int q = 0; q + 32 <= b.Length; q += 8)
            {
                string t = TryString(mem, b, q);
                if (t != null && !IsNoise(t) && !found.ContainsKey(t)) found[t] = where + "+0x" + q.ToString("X");
            }
        }

        Scan(root, "ctl");
        for (int o = 0; o + 8 <= root.Length; o += 8)
        {
            long p1 = BitConverter.ToInt64(root, o);
            if (!Plausible(p1) || !visited.Add(p1)) continue;
            byte[] b1 = SafeRead(mem, p1, 0x200) ?? SafeRead(mem, p1, 0x80);
            if (b1 == null) continue;
            Scan(b1, "ctl+0x" + o.ToString("X") + "->");

            for (int q = 0; q + 8 <= b1.Length; q += 8)
            {
                long p2 = BitConverter.ToInt64(b1, q);
                if (!Plausible(p2) || !visited.Add(p2)) continue;
                byte[] b2 = SafeRead(mem, p2, 0x100) ?? SafeRead(mem, p2, 0x40);
                if (b2 == null) continue;
                Scan(b2, "ctl+0x" + o.ToString("X") + "->+0x" + q.ToString("X") + "->");
            }
        }

        sb.AppendLine("Pointers followed: " + visited.Count + ", strings found: " + found.Count);
        foreach (var kv in found.OrderBy(k => k.Value, StringComparer.Ordinal))
            sb.AppendLine("  " + kv.Value.PadRight(34) + " \"" + Short(kv.Key, 120) + "\"");
        return sb.ToString();
    }

    // All clean inline strings in an element's bytes, keyed by offset.
    internal static Dictionary<int, string> DirectStrings(IMemory mem, byte[] bytes)
    {
        var result = new Dictionary<int, string>();
        if (bytes == null) return result;
        for (int o = 0; o + 32 <= bytes.Length; o += 8)
        {
            string t = TryString(mem, bytes, o);
            if (t != null) result[o] = t;
        }
        return result;
    }

    // Reads an MSVC std::wstring at offset o of the buffer: [0..16) inline chars or pointer,
    // [16..24) length in chars, [24..32) capacity. Returns clean text or null.
    internal static string TryString(IMemory mem, byte[] b, int o, bool numbersOk = false)
    {
        long len = BitConverter.ToInt64(b, o + 16);
        long cap = BitConverter.ToInt64(b, o + 24);
        if (len < 1 || len > MaxChars || cap < len || cap > 1_000_000) return null;

        string s;
        if (cap <= 7)
        {
            if (len * 2 > 16) return null;
            s = Encoding.Unicode.GetString(b, o, (int)len * 2);
        }
        else
        {
            long ptr = BitConverter.ToInt64(b, o);
            if (!Plausible(ptr)) return null;
            byte[] data = SafeRead(mem, ptr, (int)len * 2);
            if (data == null || data.Length < len * 2) return null;
            s = Encoding.Unicode.GetString(data, 0, (int)len * 2);
        }
        return IsClean(s) || numbersOk && IsNumberLike(s) ? s : null;
    }

    // A short value such as "61", "0% (0%)", "+4%", "12.2 (20%)" or "0 / 3".
    private static bool IsNumberLike(string s)
    {
        if (s.Length > 32 || !s.Any(char.IsDigit)) return false;
        return s.All(c => char.IsDigit(c) || char.IsLetter(c) || " %+-.,/()x:".IndexOf(c) >= 0);
    }

    // Clean = almost all characters are Latin-range or whitespace, with at least two letters.
    private static bool IsClean(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        int good = 0, letters = 0;
        foreach (char c in s)
        {
            if (c == '\0') return false;
            if (c < 0x250 && (c >= 0x20 || c == '\n' || c == '\r' || c == '\t')) good++;
            else if (c == 0x2013 || c == 0x2014 || c == 0x2019 || c == 0x2026) good++;
            if (char.IsLetter(c) && c < 0x250) letters++;
        }
        return letters >= 2 && good >= s.Length * 0.97;
    }

    private static bool Plausible(long p) => p > 0x10000 && p < 0x7FFF_FFFF_FFFF && (p & 0x1) == 0;

    private static byte[] SafeRead(IMemory mem, long addr, int size)
    {
        try
        {
            byte[] r = mem.ReadBytes(addr, size);
            return r != null && r.Length == size ? r : null;
        }
        catch
        {
            return null;
        }
    }

    private static Element SafeParent(Element e)
    {
        try { return e.Parent; }
        catch { return null; }
    }

    private static string Short(string s, int max)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " | ");
        return s.Length <= max ? s : s.Substring(0, max) + "...";
    }
}
