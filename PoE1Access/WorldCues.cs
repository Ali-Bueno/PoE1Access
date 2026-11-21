using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;

namespace PoE1Access;

// Things worth walking to (after Poe2Access's): a sound of each kind repeats from its side,
// faster and louder as you get closer, lower when it is below you on screen:
//   chests not yet opened (the three nearest in sight): a low wooden double knock;
//   the nearest shrine you can still take: a bright shimmer;
//   the nearest waypoint: a low droning hum; the nearest portal: a rising swirl;
//   the nearest exit to another area: a soft rush of air; the nearest stash: a lid's clunk.
// Within about two screens; quiet while a window or menu is open, like the other sounds.
internal sealed partial class Navigator
{
    public bool ObjectSounds = true;
    private const float ObjectCells = 140f;    // about two screens
    private readonly Dictionary<long, long> _objectNextMs = new Dictionary<long, long>();
    private long _nextObjectTickMs, _lastObjectCueMs;

    private void TickObjects(long now, Vector2 p)
    {
        if (!ObjectSounds || now < _nextObjectTickMs) return;
        _nextObjectTickMs = now + 80;
        if (now - _lastObjectCueMs < 120) return;

        var found = new List<(long Id, Vector2 G, float Dist, Synth.WorldCue Kind)>();
        foreach (Entity e in Entities())
        {
            EntityType t = Try(() => e.Type, EntityType.Error);
            Synth.WorldCue? kind = t switch
            {
                EntityType.Chest => Try(() => !e.IsOpened, false) ? Synth.WorldCue.Chest : (Synth.WorldCue?)null,
                EntityType.Shrine => Try(() => e.GetComponent<Shrine>()?.IsAvailable ?? false, false) ? Synth.WorldCue.Shrine : (Synth.WorldCue?)null,
                EntityType.Waypoint => Synth.WorldCue.Waypoint,
                EntityType.Portal or EntityType.TownPortal => Synth.WorldCue.Portal,
                EntityType.AreaTransition => Synth.WorldCue.Exit,
                EntityType.Stash => Synth.WorldCue.Stash,
                _ => null,
            };
            if (kind == null) continue;
            long id; Vector2 g;
            try { id = e.Id; g = e.GridPosNum; } catch { continue; }
            float dist = (g - p).Length();
            if (dist > ObjectCells) continue;
            // Chests only in sight (many sit behind walls); the rest lead the way from anywhere near.
            if (kind == Synth.WorldCue.Chest && !InSight(p, g)) continue;
            found.Add((id, g, dist, kind.Value));
        }
        if (found.Count == 0) return;

        // The three nearest chests, the nearest one of every other kind.
        var streams = found.Where(f => f.Kind == Synth.WorldCue.Chest).OrderBy(f => f.Dist).Take(3)
            .Concat(found.Where(f => f.Kind != Synth.WorldCue.Chest).GroupBy(f => f.Kind).Select(gr => gr.OrderBy(f => f.Dist).First()))
            .ToList();
        var due = streams
            .Select(s => (It: s, Due: _objectNextMs.TryGetValue(s.Id, out long d) ? d : long.MinValue / 2))
            .Where(s => s.Due <= now).OrderBy(s => s.Due).ToList();
        if (due.Count == 0) return;
        var it = due[0].It;

        float pan = 0f, pitch = 1f;
        if (it.Dist >= 2.5f)
        {
            Vector2 sd = ScreenDir(p, it.G);
            pan = (float)Math.Round(Math.Max(-0.9f, Math.Min(0.9f, sd.X * 0.9f)), 1);
            float below = sd.Y < 0 ? Math.Min(1f, -sd.Y * it.Dist / ScreenHalfHeightCells) : 0f;
            pitch = (float)(Math.Round((1f - 0.2f * below) * 20) / 20);
        }
        float nearness = Math.Min(1f, it.Dist / ObjectCells);
        float vol = (LootVolume?.Invoke() ?? 0.7f) * (0.35f + 0.65f * (1 - nearness));
        var kindNow = it.Kind;
        _audio.Play("obj:" + kindNow + ":" + pan + ":" + pitch, () => Synth.Object(kindNow, pan, pitch), vol);
        // From every 0.3 s beside it to every 1.6 s two screens off.
        _objectNextMs[it.Id] = now + 300 + (long)(1300 * nearness);
        _lastObjectCueMs = now;
        if (_objectNextMs.Count > 300) _objectNextMs.Clear();
    }
}
