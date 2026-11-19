using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore.PoEMemory;

namespace PoE1Access;

// The world map (opened from a waypoint): the act shown, your current quest, and the place the
// controller selects, read from the details box the game shows beside it ("Lioneye's Watch,
// Town"). Layout found 19 Nov 2026 with the map open:
//   panel titled "World" > ... > act_tabs > pages_container > (the shown act's page) > [0] >
//     [2] the quest box (name, description)    [3] the places: [0] the map's markers, and the
//     last child with text, the selected place's details box (name, kind, flavour text).
public partial class PoE1AccessCore
{
    private Element _worldPanel;
    private string _worldPlaceSig = "", _worldActSig = "";
    private long _nextWorldPollMs;

    private void PollWorldMap(long now)
    {
        if (now < _nextWorldPollMs) return;
        _nextWorldPollMs = now + 150;
        var mem = Mem;
        if (_worldPanel == null || _worldPanel.Address == 0 || !Try(() => UiText.Visible(mem, _worldPanel), false))
        {
            _worldPlaceSig = _worldActSig = "";
            return;
        }

        Element tabs = ChildNamed(_worldPanel, "act_tabs", 8);
        if (tabs == null) return;
        Element pages = ChildNamed(tabs, "pages_container", 1);
        var act = ChildNamed(tabs, "buttons_container", 1);
        Element page = Try(() => pages?.Children.FirstOrDefault(p => UiText.VisibleLocal(mem, p)), null);
        Element body = Try(() => page?.Children[0], null);
        var parts = Try<IList<Element>>(() => body?.Children, null);
        if (parts == null || parts.Count < 4) return;

        // The act and the quest, said once when the map opens or the act changes.
        var actText = new List<string>();
        if (act != null) CollectText(act, 6, actText);
        var quest = new List<string>();
        CollectText(parts[2], 4, quest);
        string actSig = string.Join("|", actText) + "#" + string.Join("|", quest);
        bool announceAct = actSig != _worldActSig;
        _worldActSig = actSig;

        // The selected place: the last child of the places list that shows text.
        var placeText = new List<string>();
        var places = Try<IList<Element>>(() => parts[3].Children, null);
        if (places != null)
            for (int i = places.Count - 1; i >= 0; i--)
            {
                Element pl = places[i];
                if (!Try(() => UiText.VisibleLocal(mem, pl), false)) continue;
                var t = new List<string>();
                CollectText(pl, 4, t);
                if (t.Count > 0) { placeText = t; break; }
            }
        string placeSig = string.Join("|", placeText);

        if (announceAct)
        {
            var say = new List<string>();
            if (actText.Count > 0) say.Add(string.Join(", ", actText.Distinct()));
            if (quest.Count > 0) say.Add("Quest: " + string.Join(". ", quest));
            if (placeText.Count > 0) say.Add("Selected: " + string.Join(", ", placeText.Take(2)));
            if (say.Count > 0) Say(JoinSpeech(say), false);
            _worldPlaceSig = placeSig;
            SetWorldTip(placeText);
            return;
        }
        if (placeSig != _worldPlaceSig && placeText.Count > 0)
        {
            _worldPlaceSig = placeSig;
            // Name and kind now ("Lioneye's Watch, Town"); tap for the flavour text.
            Say(string.Join(", ", placeText.Take(2)), true);
            SetWorldTip(placeText);
        }
    }

    private void SetWorldTip(List<string> placeText)
    {
        if (placeText.Count == 0) return;
        _tipLines = new List<string>(placeText);
        _tipIndex = Math.Min(1, placeText.Count - 1);
        _tipItemAddr = 0;
    }

    /// <summary>True when the text is the world map's own content (read by PollWorldMap).</summary>
    private bool IsWorldMapText(string text) =>
        _worldActSig.Length > 0 && (_worldPlaceSig.Length > 0 && _worldPlaceSig.Split('|').Any(t => t.Length > 3 && text.Contains(t)));
}
