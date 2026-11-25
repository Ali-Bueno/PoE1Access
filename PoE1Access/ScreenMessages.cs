using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore.PoEMemory;
using static PoE1Access.Loc;

namespace PoE1Access;

// Messages on the screens before the game that are plain text, not something the selection
// lands on: "A character with this name already exists." under the name box when creating a
// character (found 21 Nov 2026 two levels below the creation screen's root, beside its title
// and the name box). Any sentence that newly appears near the top of the screen is said; what
// is there when a screen opens is not (the screen's own lines are read as it opens).
public partial class PoE1AccessCore
{
    private HashSet<string> _screenTexts;
    private long _screenTextsRoot, _nextScreenTextsMs;
    private bool _deleteDialogSaid;
    private long _deleteSinceMs;

    // A visible question anywhere on the screen (the delete confirmation), searched deeper than
    // the screen's own lines.
    private string DeepQuestion(List<Element> tops)
    {
        var mem = Mem;
        foreach (Element top in tops)
        {
            var queue = new Queue<(Element E, int D)>();
            queue.Enqueue((top, 0));
            int visited = 0;
            while (queue.Count > 0 && visited < 4000)
            {
                var (e, d) = queue.Dequeue();
                visited++;
                if (d > 0 && !Try(() => UiText.VisibleLocal(mem, e), false)) continue;
                string t = CleanText(UiText.Text(mem, e));
                if (t.Length >= 15 && t.Contains('?') && t.Contains(' ')) return t;
                if (d >= 14) continue;
                var kids = Try<IList<Element>>(() => Fresh(e)?.Children, null);
                if (kids == null || kids.Count > 200) continue;
                foreach (Element k in kids) queue.Enqueue((k, d + 1));
            }
        }
        return null;
    }

    private void PollScreenMessages(long now)
    {
        if (now < _nextScreenTextsMs) return;
        _nextScreenTextsMs = now + 400;
        Element root = UiRoot();
        if (root == null || root.Address == 0) { _screenTexts = null; return; }
        // The screen's root and the one above it (the creation screen keeps its parts beside it).
        var tops = new List<Element> { root };
        Element above = UiText.ParentOf(Mem, root);
        if (above != null && above.Address != 0) tops.Add(above);

        var texts = new HashSet<string>(StringComparer.Ordinal);
        var mem = Mem;
        foreach (Element top in tops)
        {
            var queue = new Queue<(Element E, int D)>();
            queue.Enqueue((top, 0));
            int visited = 0;
            while (queue.Count > 0 && visited < 600)
            {
                var (e, d) = queue.Dequeue();
                visited++;
                if (d > 0 && !Try(() => UiText.VisibleLocal(mem, e), false)) continue;
                string t = CleanText(UiText.Text(mem, e));
                // A sentence: long enough, with a space, not a button or a label.
                if (t.Length >= 15 && t.Contains(' ')) texts.Add(t);
                if (d >= 3) continue;
                var kids = Try<IList<Element>>(() => Fresh(e)?.Children, null);
                if (kids == null || kids.Count > 80) continue;
                foreach (Element k in kids) queue.Enqueue((k, d + 1));
            }
        }

        if (_screenTexts == null || _screenTextsRoot != root.Address)
        {
            _screenTexts = texts;   // a new screen: its lines are read by the screen's own reader
            _screenTextsRoot = root.Address;
            return;
        }
        // The delete confirmation stays in the screen after it closes, so it is said when the
        // game's prompts turn to its own ("A: Delete Character", "B: Cancel"), not when its text
        // changes (seen 21 Nov 2026: said on entering the character list, and missed when opened
        // again for the same character).
        bool deleting = _legendNow.Any(e => e.StartsWith("A: ", StringComparison.Ordinal) && EndsWithGameWord(e, DeleteCharacterWord));
        if (deleting && !_deleteDialogSaid)
        {
            if (_deleteSinceMs == 0) _deleteSinceMs = now;
            // The question can sit deeper than the screen's other lines (seen 21 Nov 2026: read
            // for one character and missed for another), so the whole screen is searched.
            string question = texts.FirstOrDefault(t => t.Contains('?')) ?? DeepQuestion(tops);
            string how = T("A deletes it, B cancels.", "A lo borra, B cancela.");
            if (question != null)
            {
                _deleteDialogSaid = true;
                PluginLog.Write("Delete confirmation: " + question);
                Say(question + " " + how, true);
            }
            else if (now - _deleteSinceMs > 1200)
            {
                // Not found: the question in plain words, with the character the list was on.
                _deleteDialogSaid = true;
                // The character the list was on ("zireah. Level 1 Ranger. Covens League" -> "zireah").
                int dot = _lastFocusText.IndexOf(". ", StringComparison.Ordinal);
                string who = dot > 0 && _lastFocusText.IndexOf("Level", StringComparison.OrdinalIgnoreCase) > dot ? _lastFocusText.Substring(0, dot) : T("this character", "este personaje");
                PluginLog.Write("Delete confirmation not found on screen; said plainly for " + who + ". Texts: " + string.Join(" | ", texts.Take(30)));
                Say(T($"Delete {who}? Move any items you want to keep to your stash first. {how}", $"¿Borrar {who}? Mueve antes a tu alijo los objetos que quieras conservar. {how}"), true);
            }
        }
        if (!deleting) { _deleteDialogSaid = false; _deleteSinceMs = 0; }
        // A question (a confirmation) only while a dialog is open, which the prompts show with
        // a way to cancel; one left behind in the screen is not said.
        bool dialog = deleting || _legendNow.Any(e => e.EndsWith("Cancel", StringComparison.OrdinalIgnoreCase));
        var fresh = texts.Where(t => !_screenTexts.Contains(t) && (dialog && !deleting || !t.Contains('?'))).ToList();
        _screenTexts = texts;
        foreach (string t in fresh)
        {
            if (_lastFocusText.Length > 0 && t == _lastFocusText) continue;
            // The name and class screens' own lines are said by their reader.
            if (t == _classScreenLine || t.StartsWith("Enter the name", StringComparison.OrdinalIgnoreCase)) continue;
            // Nor what was just said some other way (a class's story, a focused line).
            if (_lastSpoken.Length > 0 && (_lastSpoken.Contains(t) || t.Contains(_lastSpoken))) continue;
            PluginLog.Write("Screen message: " + t);
            Say(t, true);
        }
    }
}
