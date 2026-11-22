using System;
using System.Collections.Generic;
using System.Linq;

namespace PoE1Access;

// Your current quest objectives, as the game's quest tracker words them ("Travel to the
// Submerged Passage", "Find the three glyphs"): the state text of each quest not done yet
// (ExileApi's IngameUi.GetUncompletedQuests2). Used to say which exit your quest leads to.
public partial class PoE1AccessCore
{
    private string _objectivesLogged = "";

    private List<string> QuestObjectives()
    {
        var list = new List<string>();
        var quests = Try(() => GameController.IngameState.IngameUi.GetUncompletedQuests2, null);
        if (quests != null)
            foreach (var kv in quests)
            {
                string text = Try(() => kv.Value?.QuestStateText, "") ?? "";
                if (text.Length > 0) list.Add(text);
            }
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
