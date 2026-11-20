using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ExileCore.PoEMemory;

namespace PoE1Access;

// The game's button legend: the row of controller prompts it shows for the current screen
// ("Navigate Skills", "Select Skill", "Back", "Use Skill" in skill binding mode). Each entry is a
// button picture and a label. The pictures are boxes cut from one sheet of controller icons
// (Gamepad/XboxOne/1.dds|left,top,right,bottom); the boxes were matched to buttons on
// 20 Nov 2026 from the legend's own entries: "Pick Up" is A and "Back" is B, the two-button
// entries pair the four face buttons with each modifier, and the sheet keeps the face buttons
// in the order A, B, X, Y; "Move" is the left stick. Boxes not matched yet are read by label only.
//
// Also skill binding mode (R3 in play, as the game does it on controller): its start and end
// are said, and the details box of the selected skill is read when it changes.
public partial class PoE1AccessCore
{
    private static readonly Dictionary<string, string> LegendButtons = new Dictionary<string, string>
    {
        ["0,39"] = "A",
        ["61,39"] = "B",
        ["c2,39"] = "X",
        ["14e,0"] = "Y",
        ["123,c0"] = "Left stick",
    };

    private Element _legend;
    private long _nextLegendSearchMs, _nextLegendPollMs;
    private string _legendSig = "";
    private List<string> _legendNow = new List<string>();
    private bool _bindingMode;
    private string _bindingDetailsSig = "";

    private void PollButtonLegend(long now, bool uiBusy)
    {
        if (now < _nextLegendPollMs) return;
        _nextLegendPollMs = now + 250;

        var entries = LegendEntries(now);
        _legendNow = entries;
        // Skill binding mode is the legend offering to navigate and select skills.
        bool binding = entries.Any(e => e.EndsWith("Navigate Skills") || e.EndsWith("Select Skill"));
        var details = binding ? SkillBindingDetails() : new List<string>();

        if (binding != _bindingMode)
        {
            _bindingMode = binding;
            _bindingDetailsSig = "";
            Say(binding ? "Skill binding" + (entries.Count > 0 ? ". " + string.Join(". ", entries) : "") : "Skill binding closed", true);
            _legendSig = string.Join("|", entries);
            return;
        }
        if (binding)
        {
            string dsig = string.Join("|", details);
            if (dsig != _bindingDetailsSig && details.Count > 0)
            {
                _bindingDetailsSig = dsig;
                Say(JoinSpeech(details), false);
            }
        }

        // The legend is said as it changes in windows and menus (in play it changes with
        // whatever you stand by, which would be chatter; hold L3 hears it there).
        string sig = string.Join("|", entries);
        if (sig == _legendSig) return;
        string before = _legendSig;
        _legendSig = sig;
        if ((uiBusy || binding) && entries.Count > 0 && before.Length > 0)
            Say("Controls: " + string.Join(". ", entries), false);
    }

    /// <summary>The legend as "X: Select Skill", one entry each, in screen order.</summary>
    private List<string> LegendEntries(long now)
    {
        var mem = Mem;
        var result = new List<string>();
        if (_legend == null || _legend.Address == 0)
        {
            if (now < _nextLegendSearchMs) return result;
            _nextLegendSearchMs = now + 3000;
            _legend = FindLegend();
            if (_legend == null) return result;
        }
        if (!Try(() => UiText.Visible(mem, _legend), false)) return result;
        var kids = Try<IList<Element>>(() => _legend.Children, null);
        if (kids == null) { _legend = null; return result; }
        foreach (Element entry in kids)
        {
            if (!Try(() => UiText.VisibleLocal(mem, entry), false)) continue;
            var parts = Try<IList<Element>>(() => entry.Children, null);
            if (parts == null || parts.Count < 2) continue;
            var labels = new List<string>();
            CollectText(entry, 3, labels);
            string label = string.Join(" ", labels.Where(l => l != "+"));
            if (label.Length == 0) continue;
            var buttons = new List<string>();
            foreach (Element p in parts)
            {
                if (!Try(() => UiText.VisibleLocal(mem, p), false)) continue;
                string box = IconBox(p);
                if (box.Length > 0) buttons.Add(LegendButtons.TryGetValue(box, out string b) ? b : "");
            }
            buttons = buttons.Where(b => b.Length > 0).ToList();
            result.Add(buttons.Count > 0 ? string.Join(" and ", buttons) + ": " + label : label);
        }
        return result;
    }

    // The legend: the element holding the prompt entries (dozens of them, mostly hidden, with
    // labels such as "Pick Up", "Move", "Close").
    private Element FindLegend()
    {
        Element root = UiRoot();
        if (root == null) return null;
        var mem = Mem;
        var queue = new Queue<(Element E, int D)>();
        queue.Enqueue((root, 0));
        int visited = 0;
        string[] known = { "Pick Up", "Move", "Close", "Zoom" };
        while (queue.Count > 0 && visited < 6000)
        {
            var (e, d) = queue.Dequeue();
            visited++;
            var kids = Try<IList<Element>>(() => e.Children, null);
            if (kids == null || kids.Count > 500) continue;
            if (kids.Count >= 30)
            {
                int hits = 0;
                foreach (Element k in kids.Skip(Math.Max(0, kids.Count - 20)))
                {
                    var t = new List<string>();
                    CollectTextAll(k, 3, t);
                    if (t.Any(x => known.Contains(x))) hits++;
                }
                if (hits >= 2) return e;
            }
            if (d >= 4) continue;
            foreach (Element k in kids) queue.Enqueue((k, d + 1));
        }
        return null;
    }

    // Text in an element and its children, shown or not (the legend keeps hidden entries).
    private void CollectTextAll(Element e, int depth, List<string> into)
    {
        if (e == null || depth < 0) return;
        string t = CleanText(UiText.Text(Mem, e));
        if (t.Length > 0) into.Add(t);
        var kids = Try<IList<Element>>(() => e.Children, null);
        if (kids == null || kids.Count > 30) return;
        foreach (Element k in kids) CollectTextAll(k, depth - 1, into);
    }

    // "0,39" for a picture cut from the controller icon sheet at 0,39 (its top-left corner).
    private string IconBox(Element e)
    {
        var mem = Mem;
        foreach (int off in new[] { 0x510, 0x560, 0x230, 0x280 })
        {
            try
            {
                long p = mem.Read<long>(e.Address + off + UiText.Shift);
                if (p < 0x10000 || p > 0x7FFF_FFFF_FFFF) continue;
                long s = mem.Read<long>(p + 8);
                if (s < 0x10000 || s > 0x7FFF_FFFF_FFFF) continue;
                string name = ReadAsciiOrWide(s);
                int bar = name.IndexOf('|');
                if (bar < 0 || name.IndexOf("Gamepad", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var nums = name.Substring(bar + 1).Split(',', '|');
                if (nums.Length >= 2) return nums[0] + "," + nums[1];
            }
            catch { }
        }
        return "";
    }

    private string ReadAsciiOrWide(long addr)
    {
        byte[] b = Try(() => Mem.ReadBytes(addr, 240), null);
        if (b == null) return "";
        // Wide text: every second byte zero.
        if (b.Length > 3 && b[1] == 0 && b[3] == 0)
        {
            var sb = new StringBuilder();
            for (int i = 0; i + 1 < b.Length; i += 2) { char c = (char)BitConverter.ToUInt16(b, i); if (c == 0) break; sb.Append(c); }
            return sb.ToString();
        }
        var a = new StringBuilder();
        foreach (byte x in b) { if (x == 0) break; a.Append((char)x); }
        return a.ToString();
    }

    // The selected skill's details box in binding mode: the skill bar ("skills_bar")'s second
    // part ("Default Attack", "DPS 12.9", "Attack Time 0.71 sec", its description, its damage).
    private List<string> SkillBindingDetails()
    {
        var details = new List<string>();
        var mem = Mem;
        Element bar = _skillsBar;
        if (bar == null || bar.Address == 0)
        {
            if (Environment.TickCount64 < _nextSkillsBarSearchMs) return details;
            _nextSkillsBarSearchMs = Environment.TickCount64 + 3000;
            _skillsBar = bar = ChildNamed(UiRoot(), "skills_bar", 4);
            if (bar == null) return details;
        }
        var kids = Try<IList<Element>>(() => bar.Children, null);
        if (kids != null && kids.Count > 1 && Try(() => UiText.Visible(mem, kids[1]), false))
            CollectText(kids[1], 10, details);
        return details;
    }

    private Element _skillsBar;
    private long _nextSkillsBarSearchMs;
}
