using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ExileCore.PoEMemory;
using ExileCore.Shared.Interfaces;

namespace PoE1Access;

/// <summary>
/// Developer tool (F4 on/off): while on, compares the first bytes of every visible UI element
/// about three times a second and writes what changed into PoE1Access_changes.txt, with the
/// element's place in the tree, its label, the changed offsets and any strings that changed.
/// Used to find how checkboxes, sliders and dropdowns store their value and selection.
///
/// Bytes that keep changing on their own (animations, timers) are learned in the first second
/// and whenever they change often, and are then left out. Read-only.
/// </summary>
internal sealed class ChangeLogger
{
    private int Bytes = 0x400;
    private long _rootAddr;
    private const int MaxElements = 8000;
    private const int LearnPolls = 4;
    private const int MaxLinesPerPoll = 40;

    private Dictionary<long, (string Path, byte[] Data)> _prev;
    private readonly Dictionary<(long, int), int> _changeCount = new Dictionary<(long, int), int>();
    private readonly HashSet<(long, int)> _noisy = new HashSet<(long, int)>();
    private int _polls;
    private readonly StringBuilder _out = new StringBuilder();

    public string FilePath => System.IO.Path.Combine(PluginLog.Folder, "PoE1Access_changes.txt");

    public void Start()
    {
        _prev = null;
        _changeCount.Clear();
        _noisy.Clear();
        _polls = 0;
        Append("===== change logging started " + DateTime.Now.ToString("HH:mm:ss.fff") + " (learning noise for ~1 second) =====");
    }

    public void Stop()
    {
        Append("===== change logging stopped " + DateTime.Now.ToString("HH:mm:ss.fff") + " =====");
        Flush();
    }

    /// <param name="context">Gives a short label for an element (row label and value).</param>
    /// <param name="bytes">How many bytes of each element to compare (0x1000 for a single row).</param>
    public void Poll(IMemory mem, Element root, Func<Element, string> context, int bytes = 0x400)
    {
        if (root == null || root.Address == 0) return;
        Bytes = bytes;

        // The watched subtree changed (the selection moved to another row): start comparing
        // afresh so the old row's elements are not reported as hidden.
        if (root.Address != _rootAddr)
        {
            if (_prev != null)
                Append(DateTime.Now.ToString("HH:mm:ss.fff") + " ---- now watching [" + root.Address.ToString("X") + "] \"" +
                       SafeContext(context, root) + "\" ----");
            _rootAddr = root.Address;
            _prev = null;
        }

        var cur = new Dictionary<long, (string Path, byte[] Data)>();
        var objects = new Dictionary<long, Element>();
        var stack = new Stack<(Element E, string Path)>();
        stack.Push((root, "root"));
        while (stack.Count > 0 && cur.Count < MaxElements)
        {
            var (e, path) = stack.Pop();
            if (e == null || e.Address == 0 || cur.ContainsKey(e.Address)) continue;
            if (path != "root")
            {
                bool vis = false;
                try { vis = e.IsVisibleLocal; } catch { }
                if (!vis) continue;
            }
            byte[] data = TextProbe.ReadUpTo(mem, e.Address, Bytes);
            if (data == null) continue;
            cur[e.Address] = (path, data);
            objects[e.Address] = e;

            IList<Element> kids = null;
            try { kids = e.Children; } catch { }
            if (kids == null || kids.Count > 3000) continue;
            for (int i = kids.Count - 1; i >= 0; i--) stack.Push((kids[i], path + "/" + i));
        }

        _polls++;
        var time = DateTime.Now.ToString("HH:mm:ss.fff");

        if (_prev != null)
        {
            var lines = new List<string>();
            foreach (var kv in cur.OrderBy(k => k.Value.Path, StringComparer.Ordinal))
            {
                if (!_prev.TryGetValue(kv.Key, out var old)) continue;

                var changed = new List<int>();
                int len = Math.Min(old.Data.Length, kv.Value.Data.Length);
                for (int o = 0; o + 4 <= len; o += 4)
                {
                    if (BitConverter.ToInt32(old.Data, o) == BitConverter.ToInt32(kv.Value.Data, o)) continue;
                    var key = (kv.Key, o);
                    _changeCount.TryGetValue(key, out int n);
                    _changeCount[key] = ++n;
                    if (_polls <= LearnPolls || n >= 12) _noisy.Add(key);
                    if (!_noisy.Contains(key)) changed.Add(o);
                }
                if (changed.Count == 0 || _polls <= LearnPolls) continue;

                var sb = new StringBuilder();
                sb.Append(time).Append(" [").Append(kv.Value.Path).Append(']');
                string name = UiText.Name(mem, objects[kv.Key]);
                if (name.Length > 0) sb.Append(" name=").Append(name);
                string ctx = "";
                try { ctx = context(objects[kv.Key]); } catch { }
                if (ctx.Length > 0) sb.Append(" ctx=\"").Append(ctx).Append('"');
                sb.Append(" |");
                foreach (int o in changed.Take(10))
                    sb.Append(" +0x").Append(o.ToString("X3")).Append(':')
                      .Append(BitConverter.ToInt32(old.Data, o).ToString("X")).Append("->")
                      .Append(BitConverter.ToInt32(kv.Value.Data, o).ToString("X"));
                if (changed.Count > 10) sb.Append(" (+").Append(changed.Count - 10).Append(" more)");

                // Strings that differ between the old and new bytes (a dropdown's value, say).
                var newS = TextProbe.DirectStrings(mem, kv.Value.Data);
                var oldS = TextProbe.DirectStrings(mem, old.Data);
                foreach (var s in newS)
                    if (!oldS.TryGetValue(s.Key, out string was) || was != s.Value)
                        sb.Append(" | str +0x").Append(s.Key.ToString("X3")).Append("=\"").Append(Short(s.Value, 60)).Append('"');

                lines.Add(sb.ToString());
            }

            // Elements that became visible / hidden (a checkmark appearing, for example).
            foreach (var kv in cur.Where(k => !_prev.ContainsKey(k.Key)).Take(15))
            {
                string ctx = "";
                try { ctx = context(objects[kv.Key]); } catch { }
                lines.Add(time + " APPEARED [" + kv.Value.Path + "] name=" + UiText.Name(mem, objects[kv.Key]) +
                          (ctx.Length > 0 ? " ctx=\"" + ctx + "\"" : ""));
            }
            foreach (var kv in _prev.Where(k => !cur.ContainsKey(k.Key)).Take(15))
                lines.Add(time + " HIDDEN [" + kv.Value.Path + "]");

            if (_polls > LearnPolls)
            {
                if (lines.Count > MaxLinesPerPoll)
                {
                    Append(time + " (" + lines.Count + " changes, probably a panel opened; first " + MaxLinesPerPoll + " shown)");
                    lines = lines.Take(MaxLinesPerPoll).ToList();
                }
                foreach (string l in lines) Append(l);
            }
            else if (_polls == LearnPolls)
            {
                Append(time + " learning done, " + _noisy.Count + " self-changing values ignored. Go ahead.");
            }
        }

        _prev = cur;
        if (_out.Length > 20000) Flush();
    }

    // A raw memory block outside the UI tree (a game state's own data): 4-byte values that
    // change are logged as small numbers, which is how a "which class is in view" index shows.
    private byte[] _regionPrev;
    private long _regionAddr;
    private readonly Dictionary<int, int> _regionCount = new Dictionary<int, int>();

    public void PollRegion(IMemory mem, string label, long addr, int size, string note)
    {
        if (addr == 0) return;
        byte[] cur = TextProbe.ReadUpTo(mem, addr, size);
        if (cur == null) return;
        if (addr != _regionAddr || _regionPrev == null || _regionPrev.Length != cur.Length)
        {
            _regionAddr = addr;
            _regionPrev = cur;
            _regionCount.Clear();
            Append(DateTime.Now.ToString("HH:mm:ss.fff") + " ---- watching " + label + " at 0x" + addr.ToString("X") + " (" + cur.Length + " bytes) ----");
            return;
        }

        var sb = new StringBuilder();
        int shown = 0;
        for (int o = 0; o + 4 <= cur.Length; o += 4)
        {
            int a = BitConverter.ToInt32(_regionPrev, o), b = BitConverter.ToInt32(cur, o);
            if (a == b) continue;
            _regionCount.TryGetValue(o, out int n);
            _regionCount[o] = ++n;
            if (n > 400) continue;                                  // keeps changing: noise
            if (Math.Abs((long)a) <= 100000 && Math.Abs((long)b) <= 100000)
            {
                if (n <= 40 && shown++ < 12) sb.Append(" +0x").Append(o.ToString("X3")).Append(':').Append(a).Append("->").Append(b);
            }
            else if (LooksLikePixel(_regionPrev, cur, o, out float fa, out float fb) && shown++ < 20)
                sb.Append(" +0x").Append(o.ToString("X3")).Append(":f").Append(fa.ToString("0.#")).Append("->").Append(fb.ToString("0.#"));
        }
        if (shown > 0)
            Append(DateTime.Now.ToString("HH:mm:ss.fff") + " STATE " + label + sb + (note.Length > 0 ? " | " + note : ""));
        _regionPrev = cur;
    }

    // One pointer away: each pointer in the state's first bytes leads to a block; small numbers
    // that change in those blocks are logged as "+0xAA->+0xBB: old->new".
    private readonly Dictionary<long, byte[]> _hopPrev = new Dictionary<long, byte[]>();
    private readonly Dictionary<(int, int), int> _hopCount = new Dictionary<(int, int), int>();
    private long _hopStateAddr;

    public void PollRegionHop(IMemory mem, string label, long addr, int scan, int block, string note)
    {
        if (addr == 0) return;
        byte[] state = TextProbe.ReadUpTo(mem, addr, scan);
        if (state == null) return;
        if (addr != _hopStateAddr) { _hopStateAddr = addr; _hopPrev.Clear(); _hopCount.Clear(); }

        var sb = new StringBuilder();
        int shown = 0;
        for (int o = 0; o + 8 <= state.Length; o += 8)
        {
            long p = BitConverter.ToInt64(state, o);
            if (p < 0x10000 || p > 0x7FFF_FFFF_FFFF || p == addr) continue;
            byte[] cur;
            try { cur = mem.ReadBytes(p, block); } catch { continue; }
            if (cur == null || cur.Length != block) continue;

            if (_hopPrev.TryGetValue(o, out byte[] prev) && prev.Length == cur.Length)
            {
                for (int q = 0; q + 4 <= cur.Length; q += 4)
                {
                    int a = BitConverter.ToInt32(prev, q), b = BitConverter.ToInt32(cur, q);
                    if (a == b) continue;
                    var key = (o, q);
                    _hopCount.TryGetValue(key, out int n);
                    _hopCount[key] = ++n;
                    if (n > 400) continue;
                    if (Math.Abs((long)a) <= 100000 && Math.Abs((long)b) <= 100000)
                    {
                        if (n <= 40 && shown++ < 16)
                            sb.Append(" +0x").Append(o.ToString("X3")).Append("->+0x").Append(q.ToString("X3"))
                              .Append(':').Append(a).Append("->").Append(b);
                    }
                    else if (LooksLikePixel(prev, cur, q, out float fa, out float fb) && shown++ < 24)
                    {
                        // Decimal values in screen range: a cursor position, say.
                        sb.Append(" +0x").Append(o.ToString("X3")).Append("->+0x").Append(q.ToString("X3"))
                          .Append(":f").Append(fa.ToString("0.#")).Append("->").Append(fb.ToString("0.#"));
                    }
                }
            }
            _hopPrev[o] = cur;
        }
        if (shown > 0)
            Append(DateTime.Now.ToString("HH:mm:ss.fff") + " HOP " + label + sb + (note.Length > 0 ? " | " + note : ""));
    }

    // Both values are ordinary decimals in screen range (1 to 5000) and differ by over half a pixel.
    private static bool LooksLikePixel(byte[] prev, byte[] cur, int o, out float a, out float b)
    {
        a = BitConverter.ToSingle(prev, o);
        b = BitConverter.ToSingle(cur, o);
        bool ok(float v) => !float.IsNaN(v) && !float.IsInfinity(v) && Math.Abs(v) >= 1f && Math.Abs(v) <= 5000f;
        return ok(a) && ok(b) && Math.Abs(a - b) > 0.5f;
    }

    private void Append(string line) => _out.AppendLine(line);

    private static string SafeContext(Func<Element, string> context, Element e)
    {
        try { return context(e) ?? ""; }
        catch { return ""; }
    }

    public void Flush()
    {
        if (_out.Length == 0) return;
        try
        {
            System.IO.Directory.CreateDirectory(PluginLog.Folder);
            System.IO.File.AppendAllText(FilePath, _out.ToString(), Encoding.UTF8);
        }
        catch { }
        _out.Clear();
    }

    private static string Short(string s, int max)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " | ");
        return s.Length <= max ? s : s.Substring(0, max) + "...";
    }
}
