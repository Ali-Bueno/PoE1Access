using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ExileCore.PoEMemory;

namespace PoE1Access;

// The game's button legend: the row of controller prompts it shows for the current screen
// ("Navigate Skills", "Select Skill", "Back", "Use Skill" in skill binding mode). Each entry is a
// button picture and a label. The pictures are boxes cut from one sheet of controller icons
// (Gamepad/XboxOne/1.dds|left,top,right,bottom), matched to buttons by the game's own
// picture names (see LegendButtons). Boxes not named yet are read by their label only.
//
// Also skill binding mode (R3 in play, as the game does it on controller): its start and end
// are said, and the details box of the selected skill is read when it changes.
public partial class PoE1AccessCore
{
    // Boxes on the controller icon sheet, by the game's own picture names (found 20 Nov 2026 in
    // UI elements that hold both a name such as "Controller/Button4" and its box; the game's
    // button table names A "Button4", B "Button3", X "Button1", Y "Button2"). Boxes not named
    // there are read by their label only, rather than with a guessed button.
    private static readonly Dictionary<string, string> LegendButtons = new Dictionary<string, string>
    {
        ["c2,39"] = "A",
        ["61,39"] = "B",
        ["14e,0"] = "X",
        ["0,39"] = "Y",
        ["c2,99"] = "D-pad up",
        ["184,c0"] = "L3",
        ["c2,f9"] = "R3",
        ["184,120"] = "Right bumper",
        ["0,f9"] = "Start",
        // Not named by the game's pictures; the "Navigate Skills" prompt of skill binding, next
        // to the right stick button's picture on the sheet: the left stick.
        ["61,f9"] = "Left stick",
    };

    private List<Element> _legends = new List<Element>();
    private long _legendRoot;
    private long _nextLegendSearchMs, _nextLegendPollMs;
    private string _legendSig = "";
    private List<string> _legendNow = new List<string>();
    private bool _bindingMode, _legendWasBusy, _bindingHowSaid, _bindingListOpen;
    private string _bindingDetailsPending = "";
    private long _bindingDetailsSinceMs;
    private long _guideCommandMs = -100000, _nextBindSlotPollMs;
    private long _bindingLastSeenMs;
    private string _bindingSlot = "";
    private string _bindingDetailsSig = "";

    private void PollButtonLegend(long now, bool uiBusy)
    {
        // A slot shows it is selected for only a moment, so slots are checked every tick.
        // In the skill list the slots flicker as you move, so their names are left out there.
        if (_bindingMode && !_bindingListOpen && now >= _nextBindSlotPollMs)
        {
            _nextBindSlotPollMs = now + 40;
            Try(() => { PollBindingSlots(now); return 0; }, 0);
        }
        if (now < _nextLegendPollMs) return;
        _nextLegendPollMs = now + (_bindingMode ? 100 : 250);   // a slot's mark lasts a third of a second

        var entries = LegendEntries(now);
        _legendNow = entries;
        // Skill binding mode is the legend offering to navigate and select skills, in play (the
        // skills panel offers "Select Skill" too, which is not the mode).
        bool panelOpen = _openPanels != null && _openPanels.Count > 0;
        bool binding = !panelOpen && entries.Any(e => EndsWithGameWord(e, NavigateSkillsWord) || EndsWithGameWord(e, SelectSkillWord));
        var details = binding ? SkillBindingDetails() : new List<string>();

        // The prompts change for a moment as you move in the mode, so it counts as closed only
        // after a second without them.
        if (binding) _bindingLastSeenMs = now;
        if (binding && !_bindingMode)
        {
            _bindingMode = true;
            _bindingDetailsSig = "";
            string why = "";
            string how = _bindingHowSaid ? "" : ". Move to a slot and its skill is said; A opens the skills you can put there, and the one you move to is read; B goes back";
            _bindingHowSaid = true;
            Say("Skill binding" + why + (entries.Count > 0 ? ". " + string.Join(". ", entries) : "") + how, true);
            _legendSig = string.Join("|", entries.OrderBy(x => x, StringComparer.Ordinal));
            return;
        }
        if (!binding && _bindingMode && now - _bindingLastSeenMs > 1200)
        {
            _bindingMode = false;
            _bindingSlot = "";
            _bindingListOpen = false;
            Say("Skill binding closed", true);
            _legendSig = string.Join("|", entries.OrderBy(x => x, StringComparer.Ordinal));
            return;
        }
        binding = _bindingMode;
        if (binding)
        {
            if (_skillsBar == null) SkillBindingDetails();   // finds the skill bar
            // The skill list is open while its details box shows. Its skill is said once the box
            // has settled (it fills in over a moment), and cuts off whatever was being said.
            _bindingListOpen = details.Count > 0;
            if (!_bindingListOpen) _bindingDetailsSig = "";   // opening the list again says its skill again
            string dsig = string.Join("|", details);
            if (dsig != _bindingDetailsPending) { _bindingDetailsPending = dsig; _bindingDetailsSinceMs = now; }
            else if (dsig != _bindingDetailsSig && details.Count > 0 && now - _bindingDetailsSinceMs >= 150)
            {
                _bindingDetailsSig = dsig;
                var say = new List<string>(details);
                string place = BindingListPlace(details[0]);
                if (place.Length > 0) say.Insert(1, place);
                Say(JoinSpeech(say), true);
            }
        }

        // The legend is said as it changes in windows and menus (in play it changes with
        // whatever you stand by, which would be chatter; hold L3 hears it there).
        string sig = string.Join("|", entries.OrderBy(x => x, StringComparer.Ordinal));
        // A window or menu becoming active says its controls even when they were already on
        // screen (the mod started with the window open, or noticed it late).
        if (uiBusy && !_legendWasBusy) _legendSig = "";
        _legendWasBusy = uiBusy;
        if (sig == _legendSig) return;
        _legendSig = sig;
        // Said when they appear (a window opening, an item selected) and when they change.
        if ((uiBusy || binding) && entries.Count > 0)
            Say("Controls: " + string.Join(". ", entries), false);
    }

    /// <summary>The legend as "X: Select Skill", one entry each, in screen order. There are
    /// several legends (play, the inventory's, ...); every shown entry of each counts.</summary>
    private List<string> LegendEntries(long now)
    {
        var mem = Mem;
        var result = new List<string>();
        Element root = UiRoot();
        long rootAddr = root?.Address ?? 0;
        if (rootAddr != _legendRoot) { _legends.Clear(); _legendRoot = rootAddr; _nextLegendSearchMs = 0; }
        if (_legends.Count == 0 || now >= _nextLegendSearchMs)
        {
            if (now >= _nextLegendSearchMs)
            {
                _nextLegendSearchMs = now + 5000;
                _legends = FindLegends(root);
            }
            if (_legends.Count == 0) return result;
        }
        foreach (Element legend in _legends)
        {
            if (!Try(() => UiText.Visible(mem, legend), false)) continue;
            var kids = Try<IList<Element>>(() => legend.Children, null);
            if (kids == null) continue;
            foreach (Element entry in kids)
            {
                if (!Try(() => UiText.VisibleLocal(mem, entry), false)) continue;
                var parts = Try<IList<Element>>(() => entry.Children, null);
                if (parts == null || parts.Count < 2) continue;
                var labels = new List<string>();
                CollectText(entry, 3, labels);
                string label = string.Join(" ", labels.Where(l => l != "+"));
                if (label.Length == 0) continue;
                // The button picture. An entry is a picture part and a label part, both reused
                // from a pool: the picture part's main picture (+0x510) is the button, and the
                // label part keeps the same one second (+0x230) while its main picture is left
                // over from an earlier use (found 20 Nov 2026: "Back" showed B on the picture part
                // and +0x230, X as the label part's leftover).
                var buttons = new List<string>();
                var shown = parts.Where(pt => Try(() => UiText.VisibleLocal(mem, pt), false)).ToList();
                Element labelPart = shown.FirstOrDefault(pt => { var t = new List<string>(); CollectTextAll(pt, 3, t); return t.Count > 0; });
                foreach (Element part in shown)
                {
                    string box = part == labelPart ? IconBox(part, 0x230, 0x280) : IconBox(part, 0x510, 0x560);
                    if (box.Length > 0 && LegendButtons.TryGetValue(box, out string b)) buttons.Add(b);
                }
                buttons = buttons.Where(x => x.Length > 0).Distinct().ToList();
                string line = buttons.Count > 0 ? string.Join(" and ", buttons) + ": " + label : label;
                if (!result.Contains(line)) result.Add(line);
            }
        }
        return result;
    }

    // The legends: elements holding prompt entries (dozens of them, mostly hidden, with labels
    // such as "Pick Up", "Move", "Close", "Navigate", in the client's language: LegendWords).
    private List<Element> FindLegends(Element root)
    {
        var found = new List<Element>();
        if (root == null) return found;
        var queue = new Queue<(Element E, int D)>();
        queue.Enqueue((root, 0));
        int visited = 0;
        var known = new HashSet<string>(LegendWords.SelectMany(WordsFor), StringComparer.OrdinalIgnoreCase);
        while (queue.Count > 0 && visited < 8000)
        {
            var (e, d) = queue.Dequeue();
            visited++;
            var kids = Try<IList<Element>>(() => e.Children, null);
            if (kids == null || kids.Count > 500) continue;
            if (kids.Count >= 20)
            {
                int hits = 0;
                foreach (Element k in kids.Skip(Math.Max(0, kids.Count - 16)))
                {
                    var t = new List<string>();
                    CollectTextAll(k, 3, t);
                    if (t.Any(x => known.Contains(x))) hits++;
                }
                if (hits >= 2) { found.Add(e); continue; }
            }
            if (d >= 5) continue;
            foreach (Element k in kids) queue.Enqueue((k, d + 1));
        }
        return found;
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
    private string IconBox(Element e) => IconBox(e, 0x510, 0x560, 0x230, 0x280);

    private string IconBox(Element e, params int[] offsets)
    {
        var mem = Mem;
        foreach (int off in offsets)
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
        // Read afresh each time: ExileApi caches an element's children and refreshes them only
        // when it ticks, and the details box is rebuilt for each skill.
        bar = Fresh(bar);
        var kids = Try<IList<Element>>(() => bar?.Children, null);
        if (kids != null && kids.Count > 1 && Try(() => UiText.Visible(mem, kids[1]), false))
            CollectText(kids[1], 10, details);
        return details;
    }

    private Element _skillsBar;

    // The same element read anew, so none of ExileApi's cached children are reused.
    private Element Fresh(Element e) =>
        e == null || e.Address == 0 ? null : Try<Element>(() => Game.GetObject<Element>(e.Address), null);
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

    // "2 of 3" for the skill shown in the list, from the list's entries (the skill bar's second
    // part, then its second part: an entry per skill, each with the skill's picture). With only
    // one, it says so, since moving then does nothing.
    private string BindingListPlace(string skillName)
    {
        var mem = Mem;
        Element bar = Fresh(_skillsBar);
        var list = Try(() => bar?.Children[1]?.Children[1]?.Children[0], null);
        var entries = Try<IList<Element>>(() => list?.Children, null);
        if (entries == null) return "";
        var names = new List<string>();
        foreach (Element entry in entries)
        {
            string icon = "";
            var stack = new Stack<(Element E, int D)>();
            stack.Push((entry, 0));
            while (stack.Count > 0 && icon.Length == 0)
            {
                var (e, d) = stack.Pop();
                try
                {
                    long p = mem.Read<long>(e.Address + 0x230 + UiText.Shift);
                    long t = p > 0x10000 ? mem.Read<long>(p + 8) : 0;
                    string path = t > 0x10000 ? ReadAsciiOrWide(t) : "";
                    if (path.IndexOf("SkillIcons/", StringComparison.OrdinalIgnoreCase) >= 0) icon = path;
                }
                catch { }
                if (d >= 3) continue;
                var kids = Try<IList<Element>>(() => e.Children, null);
                if (kids != null) foreach (Element k in kids) stack.Push((k, d + 1));
            }
            if (icon.Length == 0) continue;
            string file = icon.Substring(icon.IndexOf("SkillIcons/", StringComparison.OrdinalIgnoreCase) + 11);
            int cut = file.IndexOfAny(new[] { '.', '|' });
            if (cut > 0) file = file.Substring(0, cut);
            names.Add(SkillNameForIcon(file));
        }
        if (names.Count == 0) return "";
        if (names.Count == 1) return "the only skill you can put here; socket skill gems in your gear for more";
        int at = names.FindIndex(n => string.Equals(n, skillName, StringComparison.OrdinalIgnoreCase));
        return at >= 0 ? (at + 1) + " of " + names.Count : names.Count + " skills";
    }

    private void PollBindingSlots(long now)
    {
        var mem = Mem;
        Element bar = Fresh(_skillsBar);
        if (bar == null) return;
        Element main = ChildNamed(bar, "main", 1);
        Element container = ChildNamed(main ?? bar, "skills_container", 2);
        Element indicators = ChildNamed(main ?? bar, "indicator_container", 2);
        var slots = Try<IList<Element>>(() => container?.Children, null);
        if (slots == null) return;
        var marks = Try<IList<Element>>(() => indicators?.Children, null) ?? new List<Element>();
        // The selected slot pulses for a moment (a byte at +0x128 set to 1), on the slot itself,
        // one of its pictures, or its twin in the indicator row.
        bool Pulsing(Element e, int depth)
        {
            if (e == null || e.Address == 0) return false;
            if (Try(() => mem.Read<byte>(e.Address + 0x128 + UiText.Shift), (byte)0) == 1) return true;
            if (depth <= 0) return false;
            var kids = Try<IList<Element>>(() => e.Children, null);
            if (kids == null || kids.Count > 20) return false;
            foreach (Element k in kids) if (Pulsing(k, depth - 1)) return true;
            return false;
        }
        foreach (Element slot in slots)
        {
            string name = UiText.Name(mem, slot);
            if (!name.StartsWith("skill") || name == "skills_container") continue;
            Element twin = marks.FirstOrDefault(m => UiText.Name(mem, m) == name);
            if (!Pulsing(slot, 4) && !Pulsing(twin, 4)) continue;
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
