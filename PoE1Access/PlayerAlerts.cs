using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;

namespace PoE1Access;

// Alerts about you and the fight, after Poe2Access's (by the game's own buff names, found
// 21 Nov 2026 in Data/BuffDefinitions):
//  - Your harmful states as they begin and end, with a short low buzz: "Frozen", "No longer
//    frozen"; ignited, chilled, shocked, poisoned, bleeding, corrupted blood, maimed, blinded,
//    hindered, and curses ("Cursed: Temporal Chains").
//  - Harmful ground: two harsh low stabs repeating while you stand in it (burning, chilled,
//    shocked, tarred, desecrated or caustic ground, vortexes, spores, a monster's degeneration
//    aura), and its name the first time.
//  - Frenzy, power and endurance charges: a rising or falling pair of notes as they change, and
//    the count ("3 frenzy charges").
//  - A boss (unique monster) near you: its life as it falls past 75, 50, 25 and 10 percent, and
//    "defeated".
//  - Level up: three rising notes and "Level 12".
public partial class PoE1AccessCore
{
    private static readonly Dictionary<string, string> AilmentNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ignited"] = "Ignited", ["chilled"] = "Chilled", ["frozen"] = "Frozen", ["shocked"] = "Shocked",
        ["poison"] = "Poisoned", ["poisoned"] = "Poisoned", ["bleeding"] = "Bleeding", ["corrupted_blood"] = "Corrupted blood",
        ["maim"] = "Maimed", ["blinded"] = "Blinded", ["hinder"] = "Hindered",
    };
    private static readonly string[] SafeGround = { "ground_consecration", "ground_haste", "ground_tar_gold" };
    private static readonly string[] ChargeIds = { "frenzy_charge", "power_charge", "endurance_charge" };
    private static readonly string[] ChargeWords = { "frenzy", "power", "endurance" };

    private HashSet<string> _states = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _groundNamed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly int[] _charges = { -1, -1, -1 };
    private long _nextAlertsMs, _nextGroundCueMs;
    private int _lastLevel = -1;
    private readonly Dictionary<long, (string Name, int Step)> _bosses = new Dictionary<long, (string, int)>();
    private long _alertsArea = -1;

    private static bool IsHarmfulGround(string id) =>
        !SafeGround.Contains(id, StringComparer.OrdinalIgnoreCase) &&
        (id.StartsWith("ground_", StringComparison.OrdinalIgnoreCase) || id.StartsWith("caustic_cloud", StringComparison.OrdinalIgnoreCase) ||
         id.Contains("_ground_", StringComparison.OrdinalIgnoreCase) || id.EndsWith("_ground", StringComparison.OrdinalIgnoreCase) ||
         id.StartsWith("monster_aura_", StringComparison.OrdinalIgnoreCase) && (id.Contains("degen") || id.Contains("burning")));

    private void PollPlayerAlerts(long now)
    {
        if (now < _nextAlertsMs) return;
        _nextAlertsMs = now + 150;
        Entity player = Try(() => GameController.Player, null);
        if (player == null) return;
        float vol = Settings.EnemyVolume.Value / 100f;

        long area = Try(() => (long)GameController.Area.CurrentArea.Hash, 0L);
        bool newArea = area != _alertsArea;
        if (newArea) { _alertsArea = area; _bosses.Clear(); _groundNamed.Clear(); }

        var buffs = Try(() => player.GetComponent<Buffs>()?.BuffsList, null) ?? new List<Buff>();
        var names = buffs.Select(b => (Id: Try(() => b.Name, "") ?? "", Buff: b)).Where(b => b.Id.Length > 0).ToList();

        // Your harmful states.
        if (Settings.StatusAlerts.Value)
        {
            var now2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var said = new List<string>();
            foreach (var (id, b) in names)
            {
                string spoken = AilmentNames.TryGetValue(id, out string n) ? n
                    : id.StartsWith("curse_", StringComparison.OrdinalIgnoreCase) ? "Cursed: " + (Try(() => b.DisplayName, "") is string dn && dn.Length > 0 ? dn : id.Substring(6).Replace('_', ' '))
                    : null;
                if (spoken == null || !now2.Add(spoken)) continue;
                if (!_states.Contains(spoken) && !newArea) said.Add(spoken);
            }
            var ended = _states.Where(s => !now2.Contains(s)).ToList();
            if (said.Count > 0)
            {
                _audio?.Play("ailment", Synth.Ailment, vol);
                Say(string.Join(". ", said), false);
            }
            if (ended.Count > 0) Say(string.Join(". ", ended.Select(s => "No longer " + (s.StartsWith("Cursed") ? "cursed" : s.ToLowerInvariant()))), false);
            _states = now2;
        }

        // Harmful ground: a repeating cue while you stand in it, its name the first time.
        if (Settings.HarmfulGroundSound.Value)
        {
            var ground = names.Where(n => IsHarmfulGround(n.Id)).ToList();
            if (ground.Count > 0 && now >= _nextGroundCueMs)
            {
                _nextGroundCueMs = now + 700;
                _audio?.Play("harmful-ground", Synth.HarmfulGround, vol);
                foreach (var (id, b) in ground)
                    if (_groundNamed.Add(id))
                    {
                        string n = Try(() => b.DisplayName, "") ?? "";
                        Say(n.Length > 0 ? n : "Harmful ground", false);
                    }
            }
        }

        // Charges.
        if (Settings.ChargeAlerts.Value)
            for (int k = 0; k < 3; k++)
            {
                var c = names.FirstOrDefault(n => n.Id.Equals(ChargeIds[k], StringComparison.OrdinalIgnoreCase));
                int count = c.Buff == null ? 0 : Try(() => (int)c.Buff.BuffCharges, 0);
                if (count == 0 && c.Buff != null) count = Try(() => (int)c.Buff.Charges, 1);
                if (_charges[k] >= 0 && count != _charges[k] && !newArea)
                {
                    bool gained = count > _charges[k];
                    int kind = k;
                    _audio?.Play("charge:" + k + ":" + gained, () => Synth.Charge(kind, gained), vol);
                    Say(count == 0 ? "No " + ChargeWords[k] + " charges" : count + " " + ChargeWords[k] + (count == 1 ? " charge" : " charges"), false);
                }
                _charges[k] = count;
            }

        // Level up.
        int level = Try(() => player.GetComponent<ExileCore.PoEMemory.Components.Player>()?.Level ?? 0, 0);
        if (level > 0)
        {
            if (_lastLevel > 0 && level > _lastLevel && Settings.AnnounceLevelUp.Value)
            {
                _audio?.Play("levelup", Synth.LevelUp, vol);
                Say("Level " + level, true);
            }
            _lastLevel = level;
        }

        if (Settings.AnnounceBossLife.Value) PollBossLife(player);
    }

    // A unique monster near you: its life past 75, 50, 25 and 10 percent once it has been hit,
    // and "defeated".
    private static readonly int[] BossSteps = { 75, 50, 25, 10 };

    private void PollBossLife(Entity player)
    {
        var me = Try(() => player.GridPosNum, System.Numerics.Vector2.Zero);
        var seen = new HashSet<long>();
        foreach (Entity e in Try(() => GameController.EntityListWrapper.OnlyValidEntities, null) ?? (ICollection<Entity>)Array.Empty<Entity>())
        {
            if (Try(() => e.Type, EntityType.Error) != EntityType.Monster) continue;
            if (Try(() => e.Rarity, MonsterRarity.White) != MonsterRarity.Unique || !Try(() => e.IsHostile, false)) continue;
            long id = Try(() => (long)e.Id, 0L);
            if (id == 0) continue;
            float dist = (Try(() => e.GridPosNum, System.Numerics.Vector2.Zero) - me).Length();
            bool alive = Try(() => e.IsAlive, true);
            if (!_bosses.TryGetValue(id, out var known))
            {
                if (!alive || dist > 90) continue;
                string name = Try(() => e.RenderName, "") ?? "";
                if (name.Length == 0) name = "The boss";
                _bosses[id] = known = (name, 0);
            }
            seen.Add(id);
            if (!alive)
            {
                if (known.Step >= 0) Say(known.Name + " defeated", true);
                _bosses[id] = (known.Name, -1);
                continue;
            }
            if (known.Step < 0) continue;
            int pct = (int)Math.Round(Try(() => e.GetComponent<Life>()?.HPPercentage ?? 1f, 1f) * 100);
            int step = known.Step;
            while (step < BossSteps.Length && pct <= BossSteps[step]) step++;
            if (step != known.Step)
            {
                _bosses[id] = (known.Name, step);
                Say(known.Name + ", " + pct + " percent", false);
            }
        }
        // Bosses gone from the list (left behind or unloaded) are forgotten after a while.
        if (_bosses.Count > 50) foreach (long gone in _bosses.Keys.Where(k => !seen.Contains(k)).ToList()) _bosses.Remove(gone);
    }
}
