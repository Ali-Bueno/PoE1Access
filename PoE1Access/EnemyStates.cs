using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;

namespace PoE1Access;

// Ailments you put on enemies (after Poe2Access's enemy states): when an enemy in sight becomes
// frozen, chilled, ignited, shocked, poisoned, bleeding or cursed, that state's own short sound
// plays from its side (the game's buff names, as for your own states). One sound per state at a
// time, so a nova that freezes a pack plays the frozen sound once.
internal sealed partial class Navigator
{
    public bool EnemyStateSounds = true;
    private readonly Dictionary<long, HashSet<Synth.EnemyState>> _enemyStates = new Dictionary<long, HashSet<Synth.EnemyState>>();
    private readonly Dictionary<Synth.EnemyState, long> _stateCueMs = new Dictionary<Synth.EnemyState, long>();
    private long _nextStateTickMs;

    private static Synth.EnemyState? StateOf(string id)
    {
        switch (id)
        {
            case "frozen": return Synth.EnemyState.Frozen;
            case "chilled": return Synth.EnemyState.Chilled;
            case "ignited": return Synth.EnemyState.Ignited;
            case "shocked": return Synth.EnemyState.Shocked;
            case "poison": case "poisoned": return Synth.EnemyState.Poisoned;
            case "bleeding": case "bleeding_stack": case "corrupted_blood": return Synth.EnemyState.Bleeding;
        }
        return id.StartsWith("curse_", StringComparison.OrdinalIgnoreCase) ? Synth.EnemyState.Cursed : (Synth.EnemyState?)null;
    }

    private void TickEnemyStates(List<(Entity E, long Id, Vector2 G, float Dist, MonsterRarity Rarity)> near, Vector2 p, long now)
    {
        if (!EnemyStateSounds || now < _nextStateTickMs) return;
        _nextStateTickMs = now + 120;
        var alive = new HashSet<long>();
        foreach (var (e, id, g, dist, _) in near)
        {
            alive.Add(id);
            var buffs = Try(() => e.GetComponent<Buffs>()?.BuffsList, null);
            if (buffs == null) continue;
            var states = new HashSet<Synth.EnemyState>();
            foreach (var b in buffs)
            {
                var s = StateOf(Try(() => b.Name, "") ?? "");
                if (s != null) states.Add(s.Value);
            }
            bool known = _enemyStates.TryGetValue(id, out var before);
            _enemyStates[id] = states;
            if (!known) continue;   // first seen: what it already has is not news
            foreach (var s in states)
            {
                if (before.Contains(s)) continue;
                if (_stateCueMs.TryGetValue(s, out long last) && now - last < 250) continue;
                if (!InSight(p, g)) continue;
                _stateCueMs[s] = now;
                float pan = 0f;
                if (dist >= 2.5f)
                {
                    Vector2 sd = ScreenDir(p, g);
                    pan = (float)Math.Round(Math.Max(-0.9f, Math.Min(0.9f, sd.X * 0.9f)), 1);
                }
                var state = s;
                _audio.Play("state:" + state + ":" + pan, () => Synth.EnemyStateCue(state, pan), EnemyVolume?.Invoke() ?? 0.7f);
            }
        }
        foreach (long gone in _enemyStates.Keys.Where(k => !alive.Contains(k)).ToList()) _enemyStates.Remove(gone);
    }
}
