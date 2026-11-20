using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.MemoryObjects;
using BaseComp = ExileCore.PoEMemory.Components.Base;
using SocketsComp = ExileCore.PoEMemory.Components.Sockets;
using GemComp = ExileCore.PoEMemory.Components.SkillGem;
using static PoE1Access.Loc;

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
                    Say(T($"{summary}. Hold L3 and move the right stick: up and down for each stat, left and right for each section",
                        $"{summary}. Mantén L3 y mueve el stick derecho: arriba y abajo para cada estadística, izquierda y derecha para cada sección"), false);
                }
                else _charIndex = Math.Min(_charIndex, lines.Count - 1);
            }
            _skillsSig = "";
        }
        else if (_skillsPanel != null && _skillsPanel.Address != 0 && Try(() => UiText.Visible(Mem, _skillsPanel), false))
        {
            // The Skills panel opens with the inventory beside it, and the controller moves in the
            // inventory, so the panel's own selection cannot be used to read it. Every skill on
            // your skill bar is read from your character's data instead and stepped through with
            // the same keys as the character sheet: left and right for each skill, up and down
            // for its details.
            _characterSig = "";
            var lines = ReadSkillSheet();
            string sig = string.Join("|", lines.Select(l => l.Section + ":" + l.Text));
            if (sig != _skillsSig && lines.Count > 0)
            {
                bool first = _skillsSig.Length == 0;
                _skillsSig = sig;
                _charLines = lines;
                if (first)
                {
                    _charIndex = -1;
                    var names = lines.Select(l => l.Section).Distinct().ToList();
                    string skillList = string.Join(", ", names);
                    Say(T($"Skills: {skillList}. Hold L3 and move the right stick: left and right for each skill, up and down for its details",
                        $"Habilidades: {skillList}. Mantén L3 y mueve el stick derecho: izquierda y derecha para cada habilidad, arriba y abajo para sus detalles"), false);
                }
                else _charIndex = Math.Min(_charIndex, lines.Count - 1);
            }
        }
        else
        {
            _characterSig = "";
            _skillsSig = "";
            _charLines = new List<(string, string)>();
        }
    }

    // Every skill on the skill bar, in bar order: its description, how fast it is, its cost and
    // cooldown, its damage per second, and the support gems linked to it.
    private List<(string Section, string Text)> ReadSkillSheet()
    {
        var lines = new List<(string, string)>();
        var actor = GetComp<ExileCore.PoEMemory.Components.Actor>(GameController.Player);
        if (actor == null) return lines;
        var skills = Try(() => actor.ActorSkills.Where(s => s.IsOnSkillBar).OrderBy(s => s.SkillSlotIndex).ToList(), null);
        if (skills == null) return lines;

        foreach (var s in skills)
        {
            var active = Try(() => s.EffectsPerLevel?.SkillGemWrapper?.ActiveSkill, null);
            string name = CleanText(Try(() => active?.DisplayName, "") ?? "");
            if (name.Length == 0) name = CleanText(Try(() => s.Name, "") ?? "");
            if (name.Length == 0) continue;
            string section = name;
            if (lines.Any(l => l.Item1 == section)) section += " " + (Try(() => s.SkillSlotIndex, 0) + 1);

            void Add(string t) { if (!string.IsNullOrWhiteSpace(t)) lines.Add((section, t)); }
            Add(CleanText(Try(() => active?.Description, "") ?? ""));

            bool attack = Try(() => s.IsAttack, false);
            int aps100 = Try(() => s.HundredTimesAttacksPerSecond, 0);
            double cast = Try(() => s.CastTime.TotalSeconds, 0d);
            if (attack && aps100 > 0)
            {
                string aps = (aps100 / 100.0).ToString("0.##");
                Add(T($"{aps} attacks per second", $"{aps} ataques por segundo"));
            }
            else if (cast > 0)
            {
                string castText = cast.ToString("0.##");
                Add(T($"Cast time {castText} seconds", $"Tiempo de lanzamiento {castText} segundos"));
            }

            int mana = Try(() => s.Cost, 0), life = Try(() => s.LifeCost, 0), es = Try(() => s.EsCost, 0);
            var cost = new List<string>();
            if (mana > 0) cost.Add(T($"{mana} mana", $"{mana} de maná"));
            if (life > 0) cost.Add(T($"{life} life", $"{life} de vida"));
            if (es > 0) cost.Add(T($"{es} energy shield", $"{es} de escudo de energía"));
            string costList = string.Join(T(" and ", " y "), cost);
            Add(cost.Count > 0 ? T($"Costs {costList}", $"Cuesta {costList}") : T("No cost", "Sin coste"));

            double cd = Try(() => (double)s.Cooldown, 0d);   // seconds
            if (cd > 0)
            {
                string cdText = cd.ToString("0.##");
                Add(T($"Cooldown {cdText} seconds", $"Reutilización {cdText} segundos"));
            }
            float dps = Try(() => s.Dps, 0f);
            if (dps > 0)
            {
                double dpsRounded = Math.Round(dps);
                Add(T($"About {dpsRounded} damage per second", $"Unos {dpsRounded} de daño por segundo"));
            }

            var supports = LinkedSupports(name);
            if (supports.Count > 0)
            {
                string supportList = string.Join(", ", supports);
                Add(T($"Supported by {supportList}", $"Con asistencia de {supportList}"));
            }
        }
        return lines;
    }

    // The support gems linked with the socketed gem called `skill` in your worn gear.
    private List<string> LinkedSupports(string skill)
    {
        var result = new List<string>();
        ServerData server = GetServerData();
        if (server == null) return result;
        foreach (var (slot, _) in EquipmentSlots)
            foreach (var it in Slots(server, slot))
            {
                var sockets = GetComp<SocketsComp>(it.Item);
                if (sockets == null) continue;
                var groups = Try(() => sockets.SocketGroup, null);
                var gems = Try(() => sockets.SocketedGems, null);
                if (groups == null || gems == null) continue;
                var byIndex = new Dictionary<int, Entity>();
                foreach (var g in gems) { var e = Try(() => g.GemEntity, null); if (e != null) byIndex[Try(() => g.SocketIndex, -1)] = e; }
                int start = 0;
                foreach (string grp in groups)
                {
                    var members = Enumerable.Range(start, grp.Length).Where(byIndex.ContainsKey).Select(i => byIndex[i]).ToList();
                    start += grp.Length;
                    var named = members.Select(e => (E: e, Name: CleanText(Try(() => GetComp<BaseComp>(e)?.Name, "") ?? ""))).ToList();
                    if (!named.Any(n => n.Name == skill)) continue;
                    foreach (var n in named)
                        if (n.Name != skill && IsSupportGem(n.E, n.Name)) result.Add(n.Name);
                }
            }
        return result.Distinct().ToList();
    }

    // A support gem by the game's data (its granted effect, or its metadata path), so it works
    // in any client language; the English name ending in "Support" is the last resort.
    private bool IsSupportGem(Entity gem, string name)
    {
        var effect = Try(() => GetComp<GemComp>(gem)?.GemEffect?.GrantedEffect, null);
        if (effect != null) return Try(() => effect.IsSupport, false);
        string path = Try(() => gem.Path, "") ?? "";
        if (path.StartsWith("Metadata/Items/Gems/Support", StringComparison.OrdinalIgnoreCase)) return true;
        return name.EndsWith("Support");
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
                if (j >= _charLines.Count) { Say(T("Last section", "Última sección"), true); return; }
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
        string say = (newSection ? SectionName(sec) + ". " : "") + text +
                     (edge ? (stat > 0 ? T(". Last stat", ". Última estadística") : T(". First stat", ". Primera estadística")) : "") +
                     (newSection ? T($", {k} of {n}", $", {k} de {n}") : "");
        Say(say, true);
    }

    // The sections are keyed in English (compared in code); this is how one is said. A skill's
    // section is its own name, which is the game's text.
    private static string SectionName(string section)
    {
        if (section != null && section.StartsWith("More stats ", StringComparison.Ordinal))
        {
            string n = section.Substring(11);
            return T($"More stats {n}", $"Más estadísticas {n}");
        }
        return section switch
        {
            "Attributes" => T("Attributes", "Atributos"),
            "Stats" => T("Stats", "Estadísticas"),
            "Resistances" => T("Resistances", "Resistencias"),
            "Charges" => T("Charges", "Cargas"),
            "Leech and recovery" => T("Leech and recovery", "Robo y recuperación"),
            "Misc" => T("Misc", "Varios"),
            _ => section,
        };
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
        var attrs = new[] { (T("Strength", "Fuerza"), "label_strength_value"), (T("Dexterity", "Destreza"), "label_dexterity_value"), (T("Intelligence", "Inteligencia"), "label_intelligence_value") }
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
        if (res.Count > 0)
        {
            string resList = string.Join(", ", res);
            summary.Add(T($"Resistances: {resList}", $"Resistencias: {resList}"));
        }
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
            foreach (string row in TableRows(kids[1]))
                if (!row.Equals(skill, StringComparison.OrdinalIgnoreCase) && !BareSkillLabels.Any(w => IsGameWord(row, w))) lines.Add(row);
        }
        _skillsHeaderCount = lines.Count;
        // The stat table (accuracy, attacks per second, critical strike chance...).
        for (int i = 2; i < kids.Count; i++)
        {
            if (UiText.Name(mem, kids[i]) == "main") continue;
            Element k = kids[i];
            if (Try(() => UiText.VisibleLocal(Mem, k), true)) lines.AddRange(TableRows(kids[i]));
        }
        return lines.Where(l => !IsGameWord(l, AltBehaviourWord)).Distinct().ToList();
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
