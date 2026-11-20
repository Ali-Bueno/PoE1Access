using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore.PoEMemory;
using static PoE1Access.Loc;

namespace PoE1Access;

// What the controls do in each window.
//
//  - The game's own hints: texts in an open window that name a controller button
//    ("Press <<xbox_button_a>> to buy and <<xbox_button_y>> to sell", "confirm <<xbox_button_rb>>"),
//    said each time the window opens and whenever they change.
//  - The mod's own controls for that window, said the first time it opens in a session.
//  - Holding L3 in a window (when no item details are there to read) says both again.
public partial class PoE1AccessCore
{
    private string _hintsSig = "";
    private long _hintsDueMs = -1, _nextHintsPollMs;
    private List<(string Title, Element E, string Path)> _visiblePanels;
    // The character sheet and skills panel say their controls themselves each time they open.
    private readonly HashSet<string> _modHelpSaid = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Character", "Skills" };
    private List<string> _lastHints = new List<string>();
    private string _lastHintsPanels = "";

    // The mod's own controls, by window title in English (PanelKey finds it in any language).
    // The keys are English; the texts are made when spoken, in the mod's language.
    private static readonly Dictionary<string, Func<string>> ModHelp = new Dictionary<string, Func<string>>(StringComparer.OrdinalIgnoreCase)
    {
        ["Inventory"] = () => T("Tap L3: next line of the selected item. Double tap: previous line. Hold L3: the whole item. Hold L3 longer: the whole bag, or every equipment slot",
            "Pulsa L3: siguiente línea del objeto seleccionado. Doble pulsación: línea anterior. Mantén L3: el objeto completo. Mantén L3 más tiempo: toda la bolsa, o todas las ranuras de equipamiento"),
        ["Stash"] = () => T("Tap L3: next line of the selected item. Double tap: previous line. Hold L3: the whole item",
            "Pulsa L3: siguiente línea del objeto seleccionado. Doble pulsación: línea anterior. Mantén L3: el objeto completo"),
        ["Passives"] = () => T("Each node says its links. Tap L3: next line. Double tap: previous line. Hold L3: everything about the node",
            "Cada nodo dice sus enlaces. Pulsa L3: siguiente línea. Doble pulsación: línea anterior. Mantén L3: todo sobre el nodo"),
        ["Character"] = () => T("Hold L3 and move the right stick: up and down for each stat, left and right for each section",
            "Mantén L3 y mueve el stick derecho: arriba y abajo para cada estadística, izquierda y derecha para cada sección"),
        ["Skills"] = () => T("Hold L3 and move the right stick: left and right for each skill, up and down for its details",
            "Mantén L3 y mueve el stick derecho: izquierda y derecha para cada habilidad, arriba y abajo para sus detalles"),
        ["World"] = () => T("Move to hear each place. Tap L3 for its description",
            "Muévete para oír cada lugar. Pulsa L3 para su descripción"),
    };

    // A window's English title, from its title in the client's language ("Inventario" is
    // "Inventory"); any other title as it is.
    private string PanelKey(string title)
    {
        foreach (GameWord w in new[] { InventoryTitle, StashTitle, PassivesTitle, CharacterTitle, SkillsTitle, WorldTitle })
            if (IsGameWord(title, w)) return w.English;
        return title;
    }

    private void PollControlHints(long now)
    {
        if (now < _nextHintsPollMs) return;
        _nextHintsPollMs = now + 400;

        var panels = _visiblePanels ?? new List<(string Title, Element E, string Path)>();
        string panelsSig = string.Join("|", panels.Select(p => p.Title));
        if (panelsSig != _lastHintsPanels)
        {
            _lastHintsPanels = panelsSig;
            _hintsDueMs = now + 700;   // let the window finish appearing
            _hintsSig = "";
        }
        if (panels.Count == 0) { _lastHints.Clear(); return; }

        var hints = GameHints(panels);
        string sig = string.Join("|", hints);
        if (_hintsDueMs >= 0 && now >= _hintsDueMs)
        {
            _hintsDueMs = -1;
            _hintsSig = sig;
            _lastHints = hints;
            var say = new List<string>();
            if (hints.Count > 0) { string hintList = string.Join(". ", hints); say.Add(T($"Controls: {hintList}", $"Controles: {hintList}")); }
            foreach (var (title, _, _) in panels)
                if (ModHelp.TryGetValue(PanelKey(title), out Func<string> help) && _modHelpSaid.Add(PanelKey(title)))
                    say.Add(title + ": " + help());
            if (say.Count > 0) Say(JoinSpeech(say), false);
            return;
        }
        // Hints that change while the window is open (another tab, a new selection).
        if (_hintsDueMs < 0 && sig != _hintsSig)
        {
            var added = hints.Except(_lastHints).ToList();
            _hintsSig = sig;
            _lastHints = hints;
            if (added.Count > 0) { string addedList = string.Join(". ", added); Say(T($"Controls: {addedList}", $"Controles: {addedList}"), false); }
        }
    }

    /// <summary>Hold L3 in a window: its controls again. False when there is nothing to say.</summary>
    private bool SayControlsAgain()
    {
        var panels = _visiblePanels ?? new List<(string Title, Element E, string Path)>();
        var say = new List<string>();
        // The game's button legend for the current screen (in play too).
        if (_legendNow.Count > 0) { string legendList = string.Join(". ", _legendNow); say.Add(T($"Controls: {legendList}", $"Controles: {legendList}")); }
        if (panels.Count == 0) { if (say.Count == 0) return false; Say(JoinSpeech(say), true); return true; }
        var hints = GameHints(panels);
        if (hints.Count > 0) { string hintList = string.Join(". ", hints); say.Add(T($"Controls: {hintList}", $"Controles: {hintList}")); }
        foreach (var (title, _, _) in panels)
            if (ModHelp.TryGetValue(PanelKey(title), out Func<string> help)) say.Add(title + ": " + help());
        if (say.Count == 0) return false;
        Say(JoinSpeech(say), true);
        return true;
    }

    // The tab bar over the character windows (Passives, Character, Skills, Inventory, Cosmetics,
    // World), which the game switches with the bumpers; it shows them as pictures of LB and RB
    // ("lb_instruction_image", "rb_instruction_image"), not as text.
    private string TabBarHint()
    {
        var mem = Mem;
        Element bar = ChildNamed(UiRoot(), "panel_selector_container", 5);
        if (bar == null || !Try(() => UiText.Visible(mem, bar), false)) return "";
        Element layout = ChildNamed(bar, "nav_bar_layout", 1);
        var tabs = new List<string>();
        if (layout != null) CollectText(layout, 3, tabs);
        tabs = tabs.Distinct().ToList();
        if (tabs.Count < 2) return "";
        string tabList = string.Join(", ", tabs);
        return T($"Left and right bumper: switch between {tabList}", $"LB y RB: cambia entre {tabList}");
    }

    // Shown texts in the open windows that name a controller button.
    private List<string> GameHints(List<(string Title, Element E, string Path)> panels)
    {
        var mem = Mem;
        var result = new List<string>();
        string tabHint = Try(TabBarHint, "");
        if (tabHint.Length > 0) result.Add(tabHint);
        foreach (var (_, panel, _) in panels)
        {
            if (panel == null || panel.Address == 0) continue;
            var queue = new Queue<(Element E, int D)>();
            queue.Enqueue((panel, 0));
            int visited = 0;
            while (queue.Count > 0 && visited < 4000)
            {
                var (e, d) = queue.Dequeue();
                visited++;
                if (d > 0 && !Try(() => UiText.VisibleLocal(mem, e), false)) continue;
                string raw = UiText.Read(mem, e, UiText.TextOffset);
                if (raw.IndexOf("<<xbox_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    raw.IndexOf("<<ps_", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string t = HintText(raw);
                    if (t.Length > 0 && !result.Contains(t)) result.Add(t);
                }
                if (d >= 20) continue;
                var kids = Try<IList<Element>>(() => e.Children, null);
                if (kids == null || kids.Count > 500) continue;
                foreach (Element k in kids) queue.Enqueue((k, d + 1));
            }
        }
        return result;
    }

    // "buy<<xbox_button_a>>" -> "A: buy"; "Press <<xbox_button_a>> to buy" -> "Press A to buy".
    private static string HintText(string raw)
    {
        string trimmed = raw.Trim();
        var m = System.Text.RegularExpressions.Regex.Match(trimmed, @"^([^<]+?)\s*<<([a-z0-9_]+)>>\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) return PromptName(m.Groups[2].Value) + ": " + CleanText(m.Groups[1].Value);
        m = System.Text.RegularExpressions.Regex.Match(trimmed, @"^\s*<<([a-z0-9_]+)>>\s*([^<]+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) return PromptName(m.Groups[1].Value) + ": " + CleanText(m.Groups[2].Value);
        string t = System.Text.RegularExpressions.Regex.Replace(trimmed, @"<<([a-z0-9_]+)>>", mm => " " + PromptName(mm.Groups[1].Value) + " ",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return CleanText(t);
    }
}
