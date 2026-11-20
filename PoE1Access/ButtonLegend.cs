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
    private long _bindingLastSeenMs;
    private string _bindingSlot = "";
    private string _bindingDetailsSig = "";

    private void PollButtonLegend(long now, bool uiBusy)
    {
        if (now < _nextLegendPollMs) return;
        _nextLegendPollMs = now + (_bindingMode ? 100 : 250);   // a slot's mark lasts a third of a second

        var entries = LegendEntries(now);
        _legendNow = entries;
        // Skill binding mode is the legend offering to navigate and select skills.
        bool binding = entries.Any(e => e.EndsWith("Navigate Skills") || e.EndsWith("Select Skill"));
        var details = binding ? SkillBindingDetails() : new List<string>();

        // The prompts change for a moment as you move in the mode, so it counts as closed only
        // after a second without them.
        if (binding) _bindingLastSeenMs = now;
        if (binding && !_bindingMode)
        {
            _bindingMode = true;
            _bindingDetailsSig = "";
            Say("Skill binding" + (entries.Count > 0 ? ". " + string.Join(". ", entries) : ""), true);
            _legendSig = string.Join("|", entries);
            return;
        }
        if (!binding && _bindingMode && now - _bindingLastSeenMs > 1200)
        {
            _bindingMode = false;
            _bindingSlot = "";
            Say("Skill binding closed", true);
            _legendSig = string.Join("|", entries);
            return;
        }
        binding = _bindingMode;
        if (binding)
        {
            if (_skillsBar == null) SkillBindingDetails();   // finds the skill bar
            Try(() => { PollBindingSlots(now); return 0; }, 0);
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
            // The picture is held twice in an entry (its frame and its image): each button once.
            buttons = buttons.Where(b => b.Length > 0).Distinct().ToList();
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

    // ---- Binding mode: the slot you move to ----
    //
    // The skill bar's slots are named skillA, skillB, skillX, skillY, skillDpadU/R/D/L. Moving
    // onto one sets a byte at +0x128 (+0x130 after the patch's 8-byte shift) to 1 for about a
    // third of a second (found 20 Nov 2026); each slot's icon (its "icon1" child, or "icon4" on
    // the D-pad) holds the picture of the skill bound there ("Art/2DArt/SkillIcons/
    // ghostlythrow.dds"), which the skill's own data names too.
    private string _lastSlotSaid = "";
    private long _slotSaidMs;

    private void PollBindingSlots(long now)
    {
        var mem = Mem;
        Element bar = _skillsBar;
        if (bar == null) return;
        Element container = ChildNamed(bar, "skills_container", 3);
        var slots = Try<IList<Element>>(() => container?.Children, null);
        if (slots == null) return;
        foreach (Element slot in slots)
        {
            string name = UiText.Name(mem, slot);
            if (!name.StartsWith("skill") || name == "skills_container") continue;
            if (Try(() => mem.Read<byte>(slot.Address + 0x128 + UiText.Shift), (byte)0) != 1) continue;
            if (name == _lastSlotSaid && now - _slotSaidMs < 1500) continue;
            _lastSlotSaid = name;
            _slotSaidMs = now;
            string skill = SlotSkill(slot);
            Say(SlotButton(name) + ": " + (skill.Length > 0 ? skill : "empty"), true);
            return;
        }
    }

    private static string SlotButton(string slot) => slot switch
    {
        "skillA" => "A", "skillB" => "B", "skillX" => "X", "skillY" => "Y",
        "skillDpadU" => "D-pad up", "skillDpadR" => "D-pad right", "skillDpadD" => "D-pad down", "skillDpadL" => "D-pad left",
        _ => slot,
    };

    // The skill on a slot, named from its icon: the skill on your bar with that icon, or the
    // icon's own file name in words.
    private string SlotSkill(Element slot)
    {
        var mem = Mem;
        var kids = Try<IList<Element>>(() => slot.Children, null);
        if (kids == null) return "";
        foreach (Element k in kids)
        {
            string n = UiText.Name(mem, k);
            if (!n.StartsWith("icon")) continue;
            if (!Try(() => UiText.VisibleLocal(mem, k), false)) continue;
            string icon = "";
            try
            {
                long p = mem.Read<long>(k.Address + 0x230 + UiText.Shift);
                long s = p > 0x10000 ? mem.Read<long>(p + 8) : 0;
                if (s > 0x10000) icon = ReadAsciiOrWide(s);
            }
            catch { }
            int at = icon.IndexOf("SkillIcons/", StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            string file = icon.Substring(at + 11);
            int cut = file.IndexOfAny(new[] { '.', '|' });
            if (cut > 0) file = file.Substring(0, cut);
            return SkillNameForIcon(file);
        }
        return "";
    }

    private string SkillNameForIcon(string iconFile)
    {
        var actor = GetComp<ExileCore.PoEMemory.Components.Actor>(GameController.Player);
        var skills = actor != null ? Try(() => actor.ActorSkills, null) : null;
        if (skills != null)
            foreach (var s in skills)
            {
                var active = Try(() => s.EffectsPerLevel?.SkillGemWrapper?.ActiveSkill, null);
                string icon = Try(() => active?.Icon, "") ?? "";
                if (icon.Length > 0 && icon.IndexOf(iconFile, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string name = CleanText(Try(() => active.DisplayName, "") ?? "");
                    if (name.Length > 0) return name;
                }
            }
        // Words from the file name: "ghostlythrow" stays as it is, "iconbasicattack" -> "basic attack".
        string w = iconFile.StartsWith("icon", StringComparison.OrdinalIgnoreCase) ? iconFile.Substring(4) : iconFile;
        w = System.Text.RegularExpressions.Regex.Replace(w, "([a-z])([A-Z])", "$1 $2");
        if (w.Equals("basicattack", StringComparison.OrdinalIgnoreCase)) return "Default Attack";
        return w.Length > 0 ? w : iconFile;
    }
}
