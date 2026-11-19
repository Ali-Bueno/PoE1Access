using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore.Shared.Nodes;

namespace PoE1Access;

// The mod's own spoken settings menu, as in Poe2Access (its "Mod settings" menu): no need to
// see Exile API's F12 window.
//
//   Open:   hold L3 for a second and a half in play, or hold F3 for a second.
//   Move:   right stick up / down (arrow keys up / down).
//   Change: right stick left / right (arrow keys left / right): opens a submenu, toggles a
//           switch, moves a volume or speed in steps of 5, plays the sound it controls.
//   Back:   tap L3 (Backspace) goes up one level; at the top it closes.
//   Close:  double tap L3, or hold F3 again: "Settings closed".
// Exile API saves the values itself (config\PoE1Access\settings), as for its F12 window.
public partial class PoE1AccessCore
{
    private sealed class MenuItem
    {
        public string Label;
        public List<MenuItem> Children;               // a submenu
        public ToggleNode Toggle;                     // a switch
        public RangeNode<int> Range;                  // a number
        public int Step = 5;
        public Func<int, string> Words;               // how a number is said ("100 percent")
        public Action<int> OnChange;                  // a sample of what it controls
    }

    private List<MenuItem> _menuRoot;
    private readonly List<(List<MenuItem> Items, int Index)> _menuStack = new List<(List<MenuItem>, int)>();
    private readonly Dictionary<List<MenuItem>, int> _menuLastIndex = new Dictionary<List<MenuItem>, int>();
    public bool MenuOpen => _menuStack.Count > 0;

    private List<MenuItem> BuildMenu()
    {
        string Pct(int v) => v + " percent";
        MenuItem Vol(string label, RangeNode<int> node, Action<int> sample) =>
            new MenuItem { Label = label, Range = node, Words = Pct, OnChange = sample };
        MenuItem Sw(string label, ToggleNode node) => new MenuItem { Label = label, Toggle = node };
        MenuItem Sub(string label, params MenuItem[] items) => new MenuItem { Label = label, Children = items.ToList() };

        return new List<MenuItem>
        {
            Sub("Sound volumes",
                Vol("Master volume", Settings.MasterVolume, _ => SampleSound("loot")),
                Vol("Enemy, NPC and combat sounds", Settings.EnemyVolume, _ => SampleSound("enemy")),
                Vol("Loot sounds", Settings.LootVolume, _ => SampleSound("loot")),
                Vol("Radar", Settings.RadarVolume, _ => SampleSound("radar")),
                Vol("Guide", Settings.GuideVolume, _ => SampleSound("guide"))),
            Sub("Speech",
                new MenuItem
                {
                    Label = "Speech speed", Range = Settings.SpeechRate, Words = Pct,
                    OnChange = v => ApplySpeechRate(true),
                },
                Sw("Name enemies coming into view", Settings.AnnounceEnemies),
                Sw("Say which way enemies are", Settings.EnemyDirections),
                Sw("Name the targeted enemy", Settings.AnnounceTarget),
                Sw("Life, mana and shield warnings", Settings.AnnounceResources),
                Sw("Name things you walk up to", Settings.AnnounceNearby),
                Sw("Turn prompts while guiding", Settings.RoutePrompts),
                Sw("Announce panels", Settings.AnnouncePanels)),
            Sub("Sounds on or off",
                Sw("Wall radar", Settings.Radar),
                Sw("Unexplored ground pips", Settings.UnexploredMark),
                Sw("Enemy sounds", Settings.EnemySounds),
                Sw("NPC and player sounds", Settings.NpcSounds),
                Sw("Loot sounds", Settings.LootSounds),
                new MenuItem
                {
                    Label = "Which loot sounds", Range = Settings.LootFilter, Step = 1,
                    Words = v => v == 0 ? "all items" : v == 1 ? "magic or better" : "valuable only",
                },
                Sw("Targeting blips", Settings.CrosshairBeep),
                Sw("Melee reach ping", Settings.ReachPing),
                new MenuItem { Label = "Melee reach distance", Range = Settings.ReachDistance, Words = v => v.ToString() }),
            Sub("Reading",
                Sw("Read the controller selection", Settings.ReadFocus),
                Sw("Read what is under the cursor", Settings.ReadOnHover)),
        };
    }

    private void OpenMenu()
    {
        _menuRoot ??= BuildMenu();
        _menuStack.Clear();
        _menuStack.Add((_menuRoot, _menuLastIndex.TryGetValue(_menuRoot, out int i) ? i : 0));
        _pad.MenuMode = true;
        PluginLog.Write("Settings menu opened");
        Say("Mod settings. " + DescribeMenuItem(), true);
    }

    private void CloseMenu()
    {
        if (!MenuOpen) return;
        foreach (var (items, index) in _menuStack) _menuLastIndex[items] = index;
        _menuStack.Clear();
        _pad.MenuMode = false;
        PluginLog.Write("Settings menu closed");
        Say("Settings closed", true);
    }

    private string DescribeMenuItem()
    {
        var (items, index) = _menuStack[^1];
        MenuItem it = items[index];
        string value = it.Children != null ? "submenu"
            : it.Toggle != null ? (it.Toggle.Value ? "on" : "off")
            : it.Range != null ? (it.Words ?? (v => v.ToString()))(it.Range.Value)
            : "";
        return it.Label + ", " + value + ", " + (index + 1) + " of " + items.Count;
    }

    // dy: -1 up, +1 down; dx: -1 left, +1 right.
    private void MenuStep(int dy, int dx)
    {
        if (!MenuOpen) return;
        var (items, index) = _menuStack[^1];
        if (dy != 0)
        {
            int next = Math.Max(0, Math.Min(items.Count - 1, index + dy));
            _menuStack[^1] = (items, next);
            Say(DescribeMenuItem() + (next == index ? (dy > 0 ? ", last" : ", first") : ""), true);
            return;
        }

        MenuItem it = items[index];
        if (it.Children != null)
        {
            if (dx < 0) { Say(DescribeMenuItem(), true); return; }
            _menuLastIndex[items] = index;
            _menuStack.Add((it.Children, _menuLastIndex.TryGetValue(it.Children, out int i) ? i : 0));
            Say(it.Label + ". " + DescribeMenuItem(), true);
            return;
        }
        if (it.Toggle != null)
        {
            it.Toggle.Value = !it.Toggle.Value;
            Say(it.Label + ", " + (it.Toggle.Value ? "on" : "off"), true);
            return;
        }
        if (it.Range != null)
        {
            int v = Math.Max(it.Range.Min, Math.Min(it.Range.Max, it.Range.Value + dx * it.Step));
            bool edge = v == it.Range.Value;
            it.Range.Value = v;
            Say((it.Words ?? (n => n.ToString()))(v) + (edge ? (dx > 0 ? ", highest" : ", lowest") : ""), true);
            it.OnChange?.Invoke(v);
        }
    }

    private void MenuBack()
    {
        if (!MenuOpen) return;
        if (_menuStack.Count == 1) { CloseMenu(); return; }
        var (items, index) = _menuStack[^1];
        _menuLastIndex[items] = index;
        _menuStack.RemoveAt(_menuStack.Count - 1);
        Say(DescribeMenuItem(), true);
    }

    // A sample of the sound a volume controls, at the new volume.
    private void SampleSound(string kind)
    {
        if (_audio == null) return;
        float master = Settings.MasterVolume.Value / 100f;
        AudioEngine.Master = master;
        switch (kind)
        {
            case "enemy":
                _audio.Play("sample:enemy", () => Synth.Growl(ExileCore.Shared.Enums.MonsterRarity.White, 0f, 1f), Settings.EnemyVolume.Value / 100f);
                break;
            case "loot":
                _audio.Play("sample:loot", () => Synth.LootCue(Synth.Loot.Rare, 0f, 1f), Settings.LootVolume.Value / 100f * 1.4f);
                break;
            case "radar":
                _audio.Play("sample:radar", () => Synth.Tock(0f, 1f), Settings.RadarVolume.Value / 100f);
                break;
            case "guide":
                _audio.Play("sample:guide", () => Synth.Chime(0f, 1f), Settings.GuideVolume.Value / 100f);
                break;
        }
    }

    // Speech speed: Prism passes it to voices that take one (the Windows voice); NVDA and JAWS
    // keep the speed set in the screen reader.
    private int _appliedRate = -1;

    private void ApplySpeechRate(bool fromMenu)
    {
        int rate = Settings.SpeechRate.Value;
        if (rate == _appliedRate && !fromMenu) return;
        if (!fromMenu && _appliedRate < 0 && rate == 50) { _appliedRate = rate; return; }   // untouched: leave the voice as it is
        _appliedRate = rate;
        bool ok = _speech is PrismSpeech p && p.SetRate(rate / 100f);
        if (fromMenu && !ok)
            Say("Your screen reader keeps its own speed. Change it in " + (_speech?.Name ?? "the screen reader") + "'s settings", false);
    }
}
