using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore.PoEMemory;

namespace PoE1Access;

// NPC dialogue options ("Purchase Items", "Sell Items", "Goodbye"), read one at a time.
//
// The game does not report a hovered element for the option the controller selects, so the
// whole list used to be read as one. The options sit in an element named "main_frame"; each
// option is a row whose first child holds its text. The selected row carries one more entry
// than the others in a short list at +0xC0 (begin) / +0xC8 (end) of the row (found 19 Nov 2026
// by moving the selection on a merchant's options; +0xB8 before the patch's 8-byte shift).
public partial class PoE1AccessCore
{
    private Element _dialogueFrame;
    private long _nextDialogueSearchMs, _nextDialoguePollMs;
    private long _dialogueSelected;
    private List<string> _dialogueOptions = new List<string>();
    private readonly Dictionary<long, int> _rowMinKids = new Dictionary<long, int>();
    private readonly Dictionary<long, int> _rowLastKids = new Dictionary<long, int>();
    private bool _dialogueAnnounced;

    private void PollDialogue(long now)
    {
        if (now < _nextDialoguePollMs) return;
        _nextDialoguePollMs = now + 100;
        var mem = Mem;

        if (_dialogueFrame != null && !Try(() => UiText.Visible(mem, _dialogueFrame), false))
        {
            _dialogueFrame = null;
            _dialogueSelected = 0;
            _dialogueOptions.Clear();
            _rowMinKids.Clear();
            _rowLastKids.Clear();
            _dialogueAnnounced = false;
        }
        if (_dialogueFrame == null)
        {
            // Look only while something was just read from the screen (a dialogue opening is
            // read that way), so play is not slowed by searching the whole interface.
            if (now < _nextDialogueSearchMs || now - _lastHoverTextMs > 3000) return;
            _nextDialogueSearchMs = now + 700;
            _dialogueFrame = FindDialogueFrame();
            if (_dialogueFrame == null) return;
        }

        // Read afresh: ExileApi caches an element's children and refreshes them only when it
        // ticks, so a kept element's counts went stale and the selection was missed.
        _dialogueFrame = Fresh(_dialogueFrame);
        if (_dialogueFrame == null) return;

        // A dialogue window can hold several option lists (an NPC's talk topics above, then
        // Purchase, Gamble, Goodbye...): every shown list, in order, counts as one list of options.
        var frames = DialogueFrames();
        if (frames.Count == 0) return;
        var rows = new List<(Element Row, string Text, int Frame)>();
        for (int f = 0; f < frames.Count; f++)
            foreach (var (row, text) in OptionRows(frames[f])) rows.Add((row, text, f));
        if (rows.Count < 2) return;
        bool opened = !_dialogueAnnounced;
        _dialogueAnnounced = true;
        _dialogueOptions = rows.Select(r => r.Text).ToList();
        if (opened) Say(rows.Count + " options. Move to hear each", false);

        // The game adds its highlight to the selected option as one more child. Options differ in
        // their own children (some carry an 18x18 icon), and one can keep an extra child after the
        // selection has left it (Introduction, after coming back from a topic; seen 20 Nov 2026),
        // so the selected option is the one whose count has just gone up; before any move, the one
        // above the fewest children it has been seen with, if only one is.
        var kids = rows.Select(r => Try(() => (int)r.Row.ChildCount, 0)).ToList();
        int idx = -1;
        var rose = new List<int>();
        for (int i = 0; i < rows.Count; i++)
        {
            long a = rows[i].Row.Address;
            if (_rowLastKids.TryGetValue(a, out int last) && kids[i] > last) rose.Add(i);
            _rowLastKids[a] = kids[i];
            _rowMinKids[a] = _rowMinKids.TryGetValue(a, out int m) ? Math.Min(m, kids[i]) : kids[i];
        }
        if (rose.Count == 1) idx = rose[0];
        else if (_dialogueSelected == 0)
        {
            var above = Enumerable.Range(0, rows.Count).Where(i => kids[i] > _rowMinKids[rows[i].Row.Address]).ToList();
            if (above.Count == 1) idx = above[0];
        }
        if (idx < 0) return;   // nothing new selected

        long sel = rows[idx].Row.Address;
        if (sel == _dialogueSelected) return;
        _dialogueSelected = sel;
        _lastHoverSpoken = JoinSpeech(_dialogueOptions);   // the list as one is not said again
        Say(rows[idx].Text + ", " + (idx + 1) + " of " + rows.Count, true);
    }

    // Every shown option list of the dialogue window: the found list, its siblings' lists and
    // the lists in its parent's siblings (the window keeps talk topics and trade options apart).
    private List<Element> DialogueFrames()
    {
        var mem = Mem;
        var frames = new List<Element>();
        // Three levels up holds every list of the window (talk topics, quests, trade), whichever
        // list was found first.
        Element window = UiText.ParentOf(mem, UiText.ParentOf(mem, UiText.ParentOf(mem, _dialogueFrame)));
        if (window == null) return new List<Element> { _dialogueFrame };
        var queue = new Queue<(Element E, int D)>();
        queue.Enqueue((window, 0));
        while (queue.Count > 0)
        {
            var (e, d) = queue.Dequeue();
            if (d > 0 && !Try(() => UiText.VisibleLocal(mem, e), false)) continue;
            if (UiText.Name(mem, e) == "main_frame" && OptionRows(e).Count > 0) { frames.Add(e); continue; }
            if (d >= 5) continue;
            var kids = Try<IList<Element>>(() => e.Children, null);
            if (kids == null || kids.Count > 60) continue;
            foreach (Element k in kids) queue.Enqueue((k, d + 1));
        }
        if (!frames.Any(f => f.Address == _dialogueFrame.Address)) frames.Insert(0, _dialogueFrame);
        return frames;
    }

    // Rows of the dialogue frame: children whose first child shows text.
    private List<(Element Row, string Text)> OptionRows(Element frame)
    {
        var mem = Mem;
        var result = new List<(Element, string)>();
        var kids = Try<IList<Element>>(() => frame.Children, null);
        if (kids == null || kids.Count > 40) return result;
        foreach (Element r in kids)
        {
            if (!Try(() => UiText.VisibleLocal(mem, r), false)) continue;
            var rk = Try<IList<Element>>(() => r.Children, null);
            if (rk == null || rk.Count == 0 || rk.Count > 4) continue;
            string t = CleanText(UiText.Text(mem, rk[0]));
            if (t.Length > 0) result.Add((r, t));
        }
        return result;
    }

    // A shown element named "main_frame" holding two or more option rows, one of them "Goodbye"
    // or the like: NPC dialogue windows end with a way out.
    private Element FindDialogueFrame()
    {
        Element root = UiRoot();
        if (root == null) return null;
        var mem = Mem;
        var queue = new Queue<(Element E, int D)>();
        queue.Enqueue((root, 0));
        int visited = 0;
        while (queue.Count > 0 && visited < 8000)
        {
            var (e, d) = queue.Dequeue();
            visited++;
            if (d > 0 && !Try(() => UiText.VisibleLocal(mem, e), false)) continue;
            if (UiText.Name(mem, e) == "main_frame" && OptionRows(e).Count >= 1) return e;
            if (d >= 14) continue;
            var kids = Try<IList<Element>>(() => e.Children, null);
            if (kids == null || kids.Count > 400) continue;
            foreach (Element k in kids) queue.Enqueue((k, d + 1));
        }
        return null;
    }

    /// <summary>True when the text is dialogue options read together (or one option the
    /// dialogue reader says itself). The dialogue window is looked for at once if it is not
    /// known yet, so the first group is not read as one either.</summary>
    private bool IsWholeOptionList(string text)
    {
        if (_dialogueFrame == null)
        {
            _dialogueFrame = Try(FindDialogueFrame, null);
            if (_dialogueFrame == null) return false;
            _nextDialogueSearchMs = 0;
            var options = new List<string>();
            foreach (Element f in DialogueFrames())
                options.AddRange(OptionRows(f).Select(r => r.Text));
            if (options.Count >= 2) _dialogueOptions = options;
            else return false;
        }
        if (_dialogueOptions.Count < 2) return false;
        if (_dialogueOptions.Any(o => string.Equals(o, text, StringComparison.OrdinalIgnoreCase))) return true;
        return _dialogueOptions.Count(o => text.Contains(o)) >= 2;
    }
}
