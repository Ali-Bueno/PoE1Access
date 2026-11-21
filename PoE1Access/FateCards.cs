using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore.PoEMemory;
using ExileCore.Shared.Interfaces;
using static PoE1Access.Loc;

namespace PoE1Access;

// The Covens league's Fate window, opened at a Coven's Cauldron (found 21 Nov 2026, read live):
//   window   child 3: its title ("Fates"); child 5 > 0: the count ("0/2");
//            child 6 > 0: the cards, one child each (hidden ones are empty places).
//   card     the byte at +0x4FF (+0x507 after the 18 Nov patch's shift) is 1 on the selected card;
//            card > 0 > 0 holds: 4 the name ("Wildfire"), 5 what it does ("Creates walls of
//            blackfire"), 6 > 0 its tier ("1", the feathers on its icon), 8 > 0 its rewards:
//            each shown one has a picture (0) and, hidden, its words (3: "Empowered Monster will
//            drop additional Rare Melee Weapons"); a reward without words is named from its
//            picture (Art/2DItems/Currency/Oils/OpalescentOil.dds -> "Opalescent Oil").
// The window is looked for only near a Coven's Cauldron or the boss cauldron, where it opens.
public partial class PoE1AccessCore
{
    private static int FateSelectedOffset => 0x4FF + UiText.Shift;

    private Element _fateWindow;
    private long _nextFatePollMs, _nextFateSearchMs;
    private int _fateSelected = -1;
    private string _fateCount = "";
    private bool _fateAnnounced;
    private List<string> _fateTexts = new List<string>();

    private void PollFates(long now)
    {
        if (now < _nextFatePollMs) return;
        _nextFatePollMs = now + 120;
        IMemory mem = Mem;

        if (_fateWindow != null && !Try(() => UiText.Visible(mem, _fateWindow), false))
        {
            _fateWindow = null;
            _fateSelected = -1;
            _fateCount = "";
            _fateAnnounced = false;
            _fateTexts.Clear();
        }
        if (_fateWindow == null)
        {
            if (now < _nextFateSearchMs) return;
            _nextFateSearchMs = now + 800;
            if (_nav == null || !_nav.NearCovenCauldron(30f)) return;
            _fateWindow = FindFateWindow(mem);
            if (_fateWindow == null) return;
            PluginLog.Write("Fate window found");
        }

        var win = Fresh(_fateWindow);
        if (win == null) return;
        var cards = FateCardElements(mem, win);
        if (cards.Count == 0) return;
        _fateTexts = cards.Select(c => FateCardText(mem, c)).ToList();

        var wk = Kids(win);
        string title = wk.Count > 3 ? UiText.Text(mem, wk[3]) : "";
        string count = "";
        if (wk.Count > 5) { var ck = Kids(wk[5]); if (ck.Count > 0) count = UiText.Text(mem, ck[0]); }

        int sel = -1;
        for (int i = 0; i < cards.Count; i++)
            if (Try(() => mem.Read<byte>(cards[i].Address + FateSelectedOffset), (byte)0) == 1) { sel = i; break; }

        if (!_fateAnnounced)
        {
            _fateAnnounced = true;
            _fateCount = count;
            string head = string.Join(", ", new[] { title, count }.Where(s => s.Length > 0));
            int n = cards.Count;
            Say(T($"{head}. {n} cards. Move to hear each", $"{head}. {n} cartas. Muévete para oír cada una"), true);
            if (sel >= 0) { _fateSelected = sel; Say(_fateTexts[sel] + ", " + T($"{sel + 1} of {n}", $"{sel + 1} de {n}"), false); }
            return;
        }
        if (sel >= 0 && sel != _fateSelected)
        {
            _fateSelected = sel;
            Say(_fateTexts[sel] + ", " + T($"{sel + 1} of {cards.Count}", $"{sel + 1} de {cards.Count}"), true);
        }
        if (count != _fateCount)
        {
            _fateCount = count;
            if (count.Length > 0) Say(count, false);
        }
    }

    /// <summary>Everything on the selected Fate card (for hold L3), or null when no Fate window is open.</summary>
    private string FateWindowText() =>
        _fateWindow == null || _fateTexts.Count == 0 ? null
        : _fateSelected >= 0 && _fateSelected < _fateTexts.Count ? _fateTexts[_fateSelected] : string.Join(". ", _fateTexts);

    private List<Element> Kids(Element e)
    {
        var f = Fresh(e);
        var k = f == null ? null : Try(() => f.Children, null);
        return k == null ? new List<Element>() : k.ToList();
    }

    private List<Element> FateCardElements(IMemory mem, Element win)
    {
        var wk = Kids(win);
        if (wk.Count < 7) return new List<Element>();
        var holder = Kids(wk[6]);
        if (holder.Count == 0) return new List<Element>();
        return Kids(holder[0]).Where(c => Try(() => UiText.VisibleLocal(mem, c), false) && FateInner(mem, c) != null).ToList();
    }

    // card > 0 > 0, with the name at 4 and what it does at 5.
    private Element FateInner(IMemory mem, Element card)
    {
        var a = Kids(card);
        if (a.Count == 0) return null;
        var b = Kids(a[0]);
        if (b.Count == 0) return null;
        var inner = b[0];
        var k = Kids(inner);
        if (k.Count < 9) return null;
        if (UiText.Text(mem, k[4]).Length == 0 || UiText.Text(mem, k[5]).Length == 0) return null;
        return inner;
    }

    // "Wildfire, tier 1. Creates walls of blackfire. Reward: Empowered Monster will drop
    // additional Rare Melee Weapons"
    private string FateCardText(IMemory mem, Element card)
    {
        var inner = FateInner(mem, card);
        if (inner == null) return "";
        var k = Kids(inner);
        string name = UiText.Text(mem, k[4]), does = UiText.Text(mem, k[5]);
        string tier = "";
        var tk = Kids(k[6]);
        if (tk.Count > 0) tier = UiText.Text(mem, tk[0]);
        var rewards = new List<string>();
        var rk = Kids(k[8]);
        if (rk.Count > 0)
            foreach (var slot in Kids(rk[0]))
            {
                if (!Try(() => UiText.VisibleLocal(mem, slot), false)) continue;
                var sk = Kids(slot);
                string words = sk.Count > 3 ? UiText.Text(mem, sk[3]) : "";
                if (words.Length == 0 && sk.Count > 0) words = PictureName(mem, sk[0]);
                if (words.Length > 0) rewards.Add(words);
            }
        string text = name + (tier.Length > 0 ? T($", tier {tier}", $", nivel {tier}") : "") + ". " + does;
        if (rewards.Count > 0) text += ". " + T("Reward: ", "Recompensa: ") + string.Join(". ", rewards);
        return text;
    }

    // An item picture's name, from its file: "Art/2DItems/Currency/Oils/OpalescentOil.dds" ->
    // "Opalescent Oil".
    private string PictureName(IMemory mem, Element e)
    {
        byte[] b = Try(() => mem.ReadBytes(e.Address, 0x600), null);
        if (b == null) return "";
        for (int o = 0; o + 8 <= b.Length; o += 8)
        {
            long p = BitConverter.ToInt64(b, o);
            if (p < 0x10000 || p > 0x7FFFFFFFFFFF) continue;
            long q = Try(() => mem.Read<long>(p + 8), 0L);
            if (q < 0x10000 || q > 0x7FFFFFFFFFFF) continue;
            string s = Try(() => mem.ReadStringU(q, 200), null);
            if (s == null || !s.StartsWith("Art/2DItems/", StringComparison.OrdinalIgnoreCase)) continue;
            string file = s.Split('|')[0];
            file = System.IO.Path.GetFileNameWithoutExtension(file);
            return System.Text.RegularExpressions.Regex.Replace(file, "(?<=[a-z])(?=[A-Z0-9])", " ");
        }
        return "";
    }

    // The window: shown somewhere under the game's interface, with a card holder at child 6
    // whose cards have a name and what they do.
    private Element FindFateWindow(IMemory mem)
    {
        var ui = Try(() => GameController.IngameState.IngameUi, null);
        if (ui == null) return null;
        Element found = null;
        void Look(Element e, int depth)
        {
            if (found != null || e == null || depth > 5) return;
            if (depth > 0 && !Try(() => UiText.VisibleLocal(mem, e), false)) return;
            var k = Kids(e);
            if (k.Count > 300) return;
            if (k.Count >= 7 && FateCardElements(mem, e).Count > 0) { found = e; return; }
            foreach (var c in k) Look(c, depth + 1);
        }
        Look(ui, 0);
        return found;
    }
}
