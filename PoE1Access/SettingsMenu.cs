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
        public Action Play;                           // the sound glossary: plays the sound
        public string Description;                    // ...and says what it means
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
                Sw("Describe areas on arrival", Settings.DescribeAreas),
                Sw("Passive tree finder on the D-pad", Settings.PassiveFinder),
                Sw("Dodge warning when a monster attacks you", Settings.DodgeWarnings),
                Sw("Dodge warning for every enemy, not only rares and bosses", Settings.DodgeWarningsAll),
                Sw("Sounds for chests, shrines, waypoints, portals, exits and the stash", Settings.ObjectSounds),
                Sw("Say when you are frozen, poisoned, cursed and the like", Settings.StatusAlerts),
                Sw("Sound while standing in harmful ground", Settings.HarmfulGroundSound),
                Sw("Charges gained and lost", Settings.ChargeAlerts),
                Sw("A boss's life as it falls", Settings.AnnounceBossLife),
                Sw("Level up", Settings.AnnounceLevelUp),
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
                Sw("NPC sounds", Settings.NpcSounds),
                Sw("Other player sounds", Settings.PlayerSounds),
                Sw("Loot sounds", Settings.LootSounds),
                Sw("Loot uses the game's own drop sounds", Settings.GameLootSounds),
                new MenuItem
                {
                    Label = "Which loot sounds", Range = Settings.LootFilter, Step = 1,
                    Words = v => v == 0 ? "all items" : v == 1 ? "magic or better" : "valuable only",
                },
                Sw("Targeting blips", Settings.CrosshairBeep),
                Sw("Melee reach ping", Settings.ReachPing),
                Sw("Sounds for ailments you put on enemies", Settings.EnemyStateSounds),
                new MenuItem { Label = "Melee reach distance", Range = Settings.ReachDistance, Words = v => v.ToString() }),
            Sub("Reading",
                Sw("Read the controller selection", Settings.ReadFocus),
                Sw("Read what is under the cursor", Settings.ReadOnHover)),
            Sub("Sound glossary", Glossary().ToArray()),
        };
    }

    // Every sound the mod makes, what it means, and left or right to hear it (as in Poe2Access's
    // sound glossary), at the volume of its kind.
    private List<MenuItem> Glossary()
    {
        float V(RangeNode<int> node) => node.Value / 100f;
        MenuItem S(string label, string meaning, Func<float[]> make, Func<float> volume) =>
            new MenuItem { Label = label, Description = meaning, Play = () =>
            {
                AudioEngine.Master = Settings.MasterVolume.Value / 100f;
                _audio?.Play("glossary:" + label, make, volume());
            } };
        Func<float> radar = () => V(Settings.RadarVolume), enemy = () => V(Settings.EnemyVolume),
                    loot = () => V(Settings.LootVolume) * 1.4f, guide = () => V(Settings.GuideVolume);
        var items = new List<MenuItem>
        {
            S("Wall", "radar: a wall closes the way on that side", () => Synth.Tock(0f, 1f), radar),
            S("Open way", "radar: the way opens again on that side", () => Synth.Breath(0f, 0), radar),
            S("Exit ahead", "radar: an exit lies that way", () => Synth.Tin(0f, 1f), radar),
            S("Unexplored ground", "pips over the radar: ground you have not seen lies that way", () => Synth.Pips(0f, 1f), radar),
            S("Wall bump", "you pushed into a wall and stopped", () => Synth.Thud(0f), radar),
            S("Guide", "the guide: the way to what you chose", () => Synth.Chime(0f, 1f), guide),
            S("Enemy", "a normal enemy, faster as it gets closer", () => Synth.Growl(ExileCore.Shared.Enums.MonsterRarity.White, 0f, 1f), enemy),
            S("Magic enemy", "a magic enemy", () => Synth.Growl(ExileCore.Shared.Enums.MonsterRarity.Magic, 0f, 1f), enemy),
            S("Rare enemy", "a rare enemy", () => Synth.Growl(ExileCore.Shared.Enums.MonsterRarity.Rare, 0f, 1f), enemy),
            S("Unique enemy", "a unique enemy, a boss", () => Synth.Growl(ExileCore.Shared.Enums.MonsterRarity.Unique, 0f, 1f), enemy),
            S("Character", "an NPC near you", () => Synth.Tap(0f, 1f, false), enemy),
            S("Player", "another player near you", () => Synth.Tap(0f, 1f, true), enemy),
            S("Target", "the game's target landed on an enemy", Synth.Blips, enemy),
            S("Melee reach", "an enemy came within melee reach", () => Synth.ReachPing(0f), enemy),
            S("Dodge warning", "a monster starts an attack aimed at you: dodge", () => Synth.DodgeWarning(0f), enemy),
            S("Harmful ground", "you stand in burning, caustic or other harmful ground: move", Synth.HarmfulGround, enemy),
            S("Your ailment", "you are frozen, poisoned, cursed or the like; its name follows", Synth.Ailment, enemy),
        };
        foreach (Synth.EnemyState st in Enum.GetValues(typeof(Synth.EnemyState)))
        {
            var state = st;
            items.Add(S("Enemy " + st.ToString().ToLowerInvariant(), "you made an enemy " + st.ToString().ToLowerInvariant(), () => Synth.EnemyStateCue(state, 0f), enemy));
        }
        items.Add(S("Charge gained", "a frenzy, power or endurance charge gained; its count follows", () => Synth.Charge(0, true), enemy));
        items.Add(S("Charge lost", "a charge lost", () => Synth.Charge(0, false), enemy));
        items.Add(S("Level up", "you gained a level", Synth.LevelUp, enemy));
        var objects = new (Synth.WorldCue Kind, string Label, string Meaning)[]
        {
            (Synth.WorldCue.Chest, "Chest", "a chest you have not opened"),
            (Synth.WorldCue.Shrine, "Shrine", "a shrine you can still take"),
            (Synth.WorldCue.Waypoint, "Waypoint", "the nearest waypoint"),
            (Synth.WorldCue.Portal, "Portal", "the nearest portal"),
            (Synth.WorldCue.Exit, "Exit", "the nearest way to another area"),
            (Synth.WorldCue.Stash, "Stash", "the nearest stash"),
        };
        foreach (var (kind, label, meaning) in objects)
        {
            var k = kind;
            items.Add(S(label, meaning + ", faster as you get closer", () => Synth.Object(k, 0f, 1f), loot));
        }
        var drops = new (Synth.Loot Kind, string Label)[]
        {
            (Synth.Loot.Currency, "currency"), (Synth.Loot.Scroll, "a scroll"), (Synth.Loot.Gem, "a gem"), (Synth.Loot.Flask, "a flask"),
            (Synth.Loot.Map, "a map"), (Synth.Loot.Card, "a divination card"), (Synth.Loot.Jewel, "a jewel"), (Synth.Loot.Quest, "a quest item"),
            (Synth.Loot.Normal, "a normal item"), (Synth.Loot.Magic, "a magic item"), (Synth.Loot.Rare, "a rare item"), (Synth.Loot.Unique, "a unique item"),
        };
        foreach (var (kind, label) in drops)
        {
            var k = kind;
            items.Add(S("Loot: " + label, label + " on the ground, the mod's own sound", () => Synth.LootCue(k, 0f, 1f), loot));
        }

        // The game's own drop sounds, which loot plays by default (GameSounds), from the game.
        var gameDrops = new (string Event, string Label)[]
        {
            ("orb", "currency"), ("Scroll", "a scroll"), ("Gem", "a gem"), ("Flask", "a flask"), ("Map", "a map"),
            ("DivinationCard", "a divination card"), ("Jewel", "a jewel"), ("EssenceItem", "an essence"), ("DelveFossil", "a fossil"),
            ("Ring", "a ring"), ("Amulet", "an amulet"), ("Belt", "a belt"), ("quiver", "a quiver"),
            ("Helm", "a helmet"), ("cap", "a cap"), ("Metal_armour", "metal body armour"), ("Leather_armour", "leather body armour"),
            ("cloth_armour", "cloth body armour"), ("chain_armour", "chain body armour"),
            ("chain_gloves", "metal gloves"), ("Leather_glove", "leather gloves"),
            ("Metal_boots", "metal boots"), ("Boots", "leather boots"), ("chain_boots", "chain boots"),
            ("Metal_shield", "a metal shield"), ("wooden_shield", "a wooden shield"), ("Metal_and_wooden_shield", "a mixed shield"),
            ("Sword", "a sword"), ("Large_sword", "a two handed sword"), ("1HandAxe", "an axe"), ("1HandMace", "a mace"),
            ("Maul", "a two handed mace"), ("Scepter", "a sceptre"), ("1HandDagger", "a dagger"), ("1HandClaw", "a claw"),
            ("wand", "a wand"), ("Staff", "a staff"), ("Bow", "a bow"), ("item_drop_large", "any other item"),
        };
        MenuItem G(string label, string meaning, string eventPath) =>
            new MenuItem { Label = label, Description = meaning, Play = () =>
            {
                if (_gameSounds == null || !_gameSounds.Available) { Say("The game's sounds cannot be played here", true); return; }
                _gameSounds.Play(eventPath, 0f, 1f, V(Settings.LootVolume) * 1.4f * Settings.MasterVolume.Value / 100f);
            } };
        foreach (var (ev, label) in gameDrops)
            items.Add(G("Game loot: " + label, label + " on the ground, the game's own drop sound", "event:/Sound Effects/ItemSounds/" + ev + ".ogg"));
        items.Add(G("Game loot: unique", "played over a unique item's drop sound, the game's own", GameSounds.UniqueLayer));
        return items;
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
        if (it.Play != null) return it.Label + ", " + it.Description + ", " + (index + 1) + " of " + items.Count;
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
        if (it.Play != null) { it.Play(); return; }
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
