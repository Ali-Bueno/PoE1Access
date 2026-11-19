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

        var rows = OptionRows(_dialogueFrame);
        if (rows.Count < 2) return;
        _dialogueOptions = rows.Select(r => r.Text).ToList();

        int Marks(Element r)
        {
            long b = Try(() => mem.Read<long>(r.Address + 0xB8 + UiText.Shift), 0L);
            long e = Try(() => mem.Read<long>(r.Address + 0xC0 + UiText.Shift), 0L);
            return e > b && e - b < 8 * 64 ? (int)((e - b) / 8) : 0;
        }
        var counts = rows.Select(r => Marks(r.Row)).ToList();
        int max = counts.Max(), min = counts.Min();
        if (max == min) return;   // nothing selected (yet)
        int idx = counts.IndexOf(max);
        if (counts.Count(c => c == max) > 1) return;

        long sel = rows[idx].Row.Address;
        if (sel == _dialogueSelected) return;
        _dialogueSelected = sel;
        _lastHoverSpoken = JoinSpeech(_dialogueOptions);   // the list as one is not said again
        Say(rows[idx].Text + ", " + (idx + 1) + " of " + rows.Count, true);
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
            if (UiText.Name(mem, e) == "main_frame")
            {
                var rows = OptionRows(e);
                if (rows.Count >= 2) return e;
            }
            if (d >= 14) continue;
            var kids = Try<IList<Element>>(() => e.Children, null);
            if (kids == null || kids.Count > 400) continue;
            foreach (Element k in kids) queue.Enqueue((k, d + 1));
        }
        return null;
    }

    /// <summary>True when the text is the dialogue's whole option list read as one.</summary>
    private bool IsWholeOptionList(string text) =>
        _dialogueFrame != null && _dialogueOptions.Count >= 2 && _dialogueOptions.All(o => text.Contains(o));
}
