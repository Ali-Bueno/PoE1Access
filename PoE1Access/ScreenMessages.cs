using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore.PoEMemory;

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
        var fresh = texts.Where(t => !_screenTexts.Contains(t)).ToList();
        _screenTexts = texts;
        foreach (string t in fresh)
        {
            // The name and class screens' own lines are said by their reader.
            if (t == _classScreenLine || t.StartsWith("Enter the name", StringComparison.OrdinalIgnoreCase)) continue;
            // Nor what was just said some other way (a class's story, a focused line).
            if (_lastSpoken.Length > 0 && (_lastSpoken.Contains(t) || t.Contains(_lastSpoken))) continue;
            PluginLog.Write("Screen message: " + t);
            Say(t, true);
        }
    }
}
