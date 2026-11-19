using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore.PoEMemory;

namespace PoE1Access;

// The Character panel (attributes, life and defences, resistances, and its Misc / Charges /
// Defence pages) and the Skills panel (the selected skill's name, cost, time, description and
// its stat table). Both keep their numbers in name / value table rows: row children whose first
// child holds the text at UiText's text offset (found 18 Nov 2026).
//
// Opening a panel (and, in the Skills panel, moving to another skill) speaks its summary; then
// tap, double tap and hold step through every row, as with item tooltips.
public partial class PoE1AccessCore
{
    private Element _characterPanel, _skillsPanel;
    private string _characterSig = "", _skillsSig = "";
    private long _nextStatPanelMs;

    private void PollStatPanels(long now)
    {
        if (now < _nextStatPanelMs) return;
        _nextStatPanelMs = now + 300;

        if (_characterPanel != null && _characterPanel.Address != 0 && Try(() => UiText.Visible(Mem, _characterPanel), false))
        {
            var (summary, lines) = ReadCharacterPanel(_characterPanel);
            string sig = string.Join("|", lines.Select(l => l.Text));
            if (sig != _characterSig && lines.Count > 0)
            {
                bool first = _characterSig.Length == 0;
                _characterSig = sig;
                _charLines = lines;   // kept apart from the tooltip lines: the inventory beside it uses those
                if (first)
                {
                    _charIndex = -1;
                    Say(summary + ". Hold L3 and move the right stick: up and down for each stat, left and right for each section", false);
                }
                else _charIndex = Math.Min(_charIndex, lines.Count - 1);
            }
        }
        else
        {
            _characterSig = "";
            _charLines = new List<(string, string)>();
        }

        if (_skillsPanel != null && _skillsPanel.Address != 0 && Try(() => UiText.Visible(Mem, _skillsPanel), false))
        {
            var lines = ReadSkillsPanel(_skillsPanel);
            string sig = lines.Count > 0 ? lines[0] + "|" + (lines.Count > 1 ? lines[^1] : "") : "";
            if (sig != _skillsSig && lines.Count > 0)
            {
                _skillsSig = sig;
                _tipLines = lines;
                _tipIndex = 0;
                _tipItemAddr = 0;
                // The skill's name, then the lines above its stat table (cost, time, description).
                Say(JoinSpeech(lines.Take(_skillsHeaderCount).ToList()), false);
            }
        }
        else _skillsSig = "";
    }

    private int _skillsHeaderCount = 1;

    // The character sheet's stats, each with its section, stepped through with L3 + right stick.
    private List<(string Section, string Text)> _charLines = new List<(string, string)>();
    private int _charIndex = -1;

    private void CharacterStep(int stat, int section)
    {
        if (_charLines.Count == 0) return;
        int i = _charIndex;
        if (stat != 0)
        {
            i = Math.Max(0, Math.Min(_charLines.Count - 1, (i < 0 ? -1 : i) + stat));
            if (i < 0) i = 0;
        }
        else
        {
            // The first stat of the next / previous section (or of this one, going back).
            string cur = i >= 0 ? _charLines[i].Section : "";
            if (section > 0)
            {
                int j = i + 1;
                while (j < _charLines.Count && _charLines[j].Section == cur) j++;
                if (j >= _charLines.Count) { Say("Last section", true); return; }
                i = j;
            }
            else
            {
                int start = i;
                while (start > 0 && _charLines[start - 1].Section == cur) start--;
                if (start == i && i > 0)
                {
                    string prev = _charLines[i - 1].Section;
                    start = i - 1;
                    while (start > 0 && _charLines[start - 1].Section == prev) start--;
                }
                i = Math.Max(0, start);
            }
        }
        bool newSection = _charIndex < 0 || _charLines[_charIndex].Section != _charLines[i].Section;
        bool edge = i == _charIndex;
        _charIndex = i;
        var (sec, text) = _charLines[i];
        int n = _charLines.Count(l => l.Section == sec), k = _charLines.Take(i + 1).Count(l => l.Section == sec);
        string say = (newSection ? sec + ". " : "") + text + (edge ? (stat > 0 ? ". Last stat" : ". First stat") : "") +
                     (newSection ? ", " + k + " of " + n : "");
        Say(say, true);
    }

    // "Strength 23, ... Life: 61, ...", and every stat with its section.
    private (string Summary, List<(string Section, string Text)> Lines) ReadCharacterPanel(Element panel)
    {
        var mem = Mem;
        var lines = new List<(string, string)>();
        string Label(string name)
        {
            Element e = ChildNamed(panel, name, 2);
            return e != null ? CleanText(UiText.Text(mem, e)) : "";
        }
        var attrs = new[] { ("Strength", "label_strength_value"), ("Dexterity", "label_dexterity_value"), ("Intelligence", "label_intelligence_value") }
            .Select(a => (a.Item1, Label(a.Item2))).Where(a => a.Item2.Length > 0).Select(a => a.Item1 + ": " + a.Item2).ToList();
        foreach (string a in attrs) lines.Add(("Attributes", a));

        Element summaryFrame = ChildNamed(panel, "frame_character_summary", 2);
        var main = summaryFrame != null ? TableRows(summaryFrame) : new List<string>();
        foreach (string m in main) lines.Add(("Stats", m));
        Element resist = ChildNamed(panel, "frame_resistance_summary", 2);
        var res = resist != null ? TableRows(resist) : new List<string>();
        foreach (string r in res) lines.Add(("Resistances", r));

        // The pages beside the summary (Misc, Charges, Defence...), each its own section.
        Element pages = ChildNamed(panel, "pages_container", 3);
        var pageList = pages != null ? Try<IList<Element>>(() => pages.Children, null) : null;
        if (pageList != null)
        {
            int p = 0;
            foreach (Element page in pageList)
            {
                var rows = TableRows(page);
                if (rows.Count == 0) continue;
                p++;
                string name = rows.Any(r => r.Contains("Charges")) ? "Charges"
                    : rows.Any(r => r.Contains("Leech")) ? "Leech and recovery"
                    : p == 1 ? "Misc" : "More stats " + p;
                foreach (string r in rows) lines.Add((name, r));
            }
        }

        var summary = new List<string>();
        if (attrs.Count > 0) summary.Add(string.Join(", ", attrs));
        if (main.Count > 0) summary.Add(string.Join(", ", main));
        if (res.Count > 0) summary.Add("Resistances: " + string.Join(", ", res));
        return (JoinSpeech(summary), lines);
    }

    // The selected skill: name, the lines under it (cost, time, description), then its stat table.
    private List<string> ReadSkillsPanel(Element panel)
    {
        var mem = Mem;
        var lines = new List<string>();
        Element name = ChildNamed(panel, "active_skill_name", 2);
        string skill = name != null ? CleanText(UiText.Text(mem, name)) : "";
        if (skill.Length == 0) return lines;
        lines.Add(skill);

        var kids = Try<IList<Element>>(() => panel.Children, null) ?? new List<Element>();
        // The block under the name: rows such as "Attack Time: 0.65secs", then the description.
        if (kids.Count > 1)
        {
            // A label left without a value (no cost, no souls) is skipped.
            string[] bare = { "Cost", "Souls", "Reservation", "Cooldown", "Cast Time", "Attack Time" };
            foreach (string row in TableRows(kids[1]))
                if (!row.Equals(skill, StringComparison.OrdinalIgnoreCase) && !bare.Contains(row)) lines.Add(row);
        }
        _skillsHeaderCount = lines.Count;
        // The stat table (accuracy, attacks per second, critical strike chance...).
        for (int i = 2; i < kids.Count; i++)
        {
            if (UiText.Name(mem, kids[i]) == "main") continue;
            Element k = kids[i];
            if (Try(() => UiText.VisibleLocal(Mem, k), true)) lines.AddRange(TableRows(kids[i]));
        }
        return lines.Where(l => !l.Equals("Use Alt Behaviour", StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
    }

    private Element ChildNamed(Element root, string name, int depth)
    {
        var mem = Mem;
        var queue = new Queue<(Element E, int D)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (e, d) = queue.Dequeue();
            if (d > 0 && UiText.Name(mem, e) == name) return e;
            if (d >= depth) continue;
            var kids = Try<IList<Element>>(() => e.Children, null);
            if (kids == null || kids.Count > 300) continue;
            foreach (Element k in kids) queue.Enqueue((k, d + 1));
        }
        return null;
    }

    // Text rows under an element: an element whose children each hold one piece of text (in
    // themselves or their first child) becomes "Name: Value"; a lone text is its own line.
    // Hidden branches (another page of the panel) are skipped.
    private List<string> TableRows(Element root)
    {
        var mem = Mem;
        var rows = new List<string>();
        void Walk(Element e, int depth)
        {
            if (e == null || e.Address == 0 || depth > 12 || rows.Count > 300) return;
            if (depth > 0 && !Try(() => UiText.VisibleLocal(Mem, e), true)) return;
            if (UiText.Name(mem, e).StartsWith("button_")) return;
            var kids = Try<IList<Element>>(() => e.Children, null) ?? new List<Element>();

            // A row: two or more children, each a text cell.
            if (kids.Count >= 2 && kids.Count <= 6)
            {
                var cells = new List<string>();
                bool allCells = true;
                foreach (Element c in kids)
                {
                    long cc = Try(() => c.ChildCount, 0L);
                    if (cc > 1) { allCells = false; break; }
                    string t = CleanText(UiText.Text(mem, c));
                    if (t.Length == 0 && cc == 1) t = CleanText(UiText.Text(mem, Try<Element>(() => c.Children[0], null)));
                    if (t.Length > 0 && t != "margin_l" && t != "margin_r") cells.Add(t);
                }
                if (allCells && cells.Count >= 2)
                {
                    if (!(cells.Count == 2 && cells[0] == "Name" && cells[1] == "Value"))
                        rows.Add(cells[0] + ": " + string.Join(", ", cells.Skip(1)));
                    return;
                }
            }

            string own = CleanText(UiText.Text(mem, e));
            if (own.Length > 0 && own != "margin_l" && own != "margin_r" && own != "Name" && own != "Value") rows.Add(own);
            foreach (Element k in kids) Walk(k, depth + 1);
        }
        Walk(root, 0);
        return rows;
    }
}
