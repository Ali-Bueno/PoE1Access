using System;
using System.Collections.Generic;
using System.Linq;

namespace PoE1Access;

// Your current quest objectives, read from the game's quest tracker as it shows them ("Search
// the Mud Flats for the entrance to the Fetid Pool", "Travel to the Tidal Island"): every text
// under IngameUi.QuestTracker but its own controls ("Collapse Quest Groups", "Page 1/1", "+").
// ExileApi's quest lists (GetUncompletedQuests2, GetQuestStates) read empty since the patch of
// 18 Nov 2026; the tracker's texts were checked live on 22 Nov 2026. Used to say which exit your
// quest leads to.
public partial class PoE1AccessCore
{
    private string _objectivesLogged = "";

    private List<string> QuestObjectives()
    {
        var list = new List<string>();
        var mem = Mem;
        var tracker = Try(() => GameController.IngameState.IngameUi.QuestTracker, null);
        void Walk(ExileCore.PoEMemory.Element e, int depth)
        {
            if (e == null || depth > 12 || list.Count > 60) return;
            string t = Try(() => UiText.Text(mem, e), "") ?? "";
            if (t.Length > 3 && !t.StartsWith("Page ") && t != "Collapse Quest Groups") list.Add(t);
            foreach (var k in Kids(e)) Walk(k, depth + 1);
        }
        if (tracker != null) Walk(tracker, 0);
        // Logged when they change, to check what the game gives.
        string joined = string.Join(" | ", list);
        if (joined != _objectivesLogged)
        {
            _objectivesLogged = joined;
            PluginLog.Write("Quest objectives: " + (joined.Length > 0 ? joined : "(none read)"));
        }
        return list;
    }
}
