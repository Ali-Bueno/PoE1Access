using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using static PoE1Access.Loc;

namespace PoE1Access;

/// <summary>
/// Navigation for blind players, modelled on the Poe2Access design (radar, scanner, guide),
/// rebuilt for PoE1 on ExileApi. Read-only: it reads positions, the walkable grid and the
/// camera, and only plays sounds and speaks.
///
///  * Radar: four rays along the screen directions over the walkable grid. A cue plays only
///    when what lies ahead changes: tock = wall, breath = open way, tin = exit in that quarter.
///  * Scanner: categories of things around you, nearest (by walkable route) first.
///  * Guide: a chime panned toward where the route goes next, faster as it gets shorter.
/// </summary>
internal sealed partial class Navigator
{
    // World units per grid cell (PoE: 250 / 23).
    private const float GridToWorld = 250f / 23f;
    private const float WallCells = 200f / GridToWorld;     // ~18 cells: a wall closes the way
    private const float OpenCells = 250f / GridToWorld;     // ~23 cells: the way is open
    private const float ExitCells = 750f / GridToWorld;     // ~69 cells: exit reach
    private const float CellsPerStep = 3f;                   // a spoken "step"

    private readonly GameController _gc;
    private readonly AudioEngine _audio;
    private readonly Action<string> _say;        // interrupts
    private readonly Action<string> _sayQueued;  // waits for current speech
    private readonly Func<float> _radarVolume;
    private readonly Func<float> _guideVolume;

    public Navigator(GameController gc, AudioEngine audio, Action<string> say, Action<string> sayQueued,
                     Func<float> radarVolume, Func<float> guideVolume)
    {
        _gc = gc;
        _audio = audio;
        _say = say;
        _sayQueued = sayQueued;
        _radarVolume = radarVolume;
        _guideVolume = guideVolume;
    }

    public bool AnnounceNearby = true;
    public bool RoutePrompts = true;

    // ===================================================================
    // Grid and geometry
    // ===================================================================

    private int[][] _grid;
    private int _gridW, _gridH;
    private long _gridLoadedMs = -100000;
    private uint _areaHash;

    private void RefreshGrid(long now)
    {
        uint hash = 0;
        try { hash = _gc.IngameState.Data.CurrentAreaHash; } catch { }
        if (hash != _areaHash)
        {
            _areaHash = hash;
            _grid = null;
            ResetAreaState();
        }
        // Doors and barriers change the grid, so re-read it now and then.
        if (_grid != null && now - _gridLoadedMs < 4000) return;
        try
        {
            int[][] g = _gc.IngameState.Data.RawPathfindingData;
            if (g != null && g.Length > 0 && g[0] != null)
            {
                _grid = g;
                _gridH = g.Length;
                _gridW = g[0].Length;
                _pathCells = null;
            }
            // What attacks can pass: walls are closed, water and low ground are open.
            int[][] t = _gc.IngameState.Data.RawTerrainTargetingData;
            _sightGrid = t != null && t.Length == _gridH && t[0] != null ? t : null;
        }
        catch { }
        _gridLoadedMs = now;
    }

    private bool Walkable(int x, int y)
    {
        if (_grid == null || x < 0 || y < 0 || y >= _gridH) return false;
        int[] row = _grid[y];
        return row != null && x < row.Length && row[x] > 0;
    }

    // The pathfinding grid holds 0 for no ground and 1 to 5 for how far a square is from a wall
    // or water (5 = open ground). 1 is the very edge, where a character's body does not fit
    // (seen 19 Nov 2026 on the Twilight Strand's shore, where a route along the 1s could not be
    // walked), so routes and places to go use only squares of 2 or more.
    private const int MinPassable = 2;

    private int Clearance(int x, int y)
    {
        if (_grid == null || x < 0 || y < 0 || y >= _gridH) return 0;
        int[] row = _grid[y];
        return row != null && x < row.Length ? row[x] : 0;
    }

    private bool Passable(int x, int y) => Clearance(x, y) >= MinPassable;

    // Extra cost of stepping on a square near an edge, so routes keep to open ground.
    private static int EdgeCost(int clearance) => clearance >= 5 ? 0 : clearance == 4 ? 3 : clearance == 3 ? 10 : 30;

    private Entity Player => _gc.Player;

    private Vector2 PlayerGrid()
    {
        try { return Player.GridPosNum; } catch { return new Vector2(-1, -1); }
    }

    // Screen position of a grid point (at the player's height).
    private Vector2 GridToScreen(Vector2 grid, float z)
    {
        var world = new Vector3(grid.X * GridToWorld, grid.Y * GridToWorld, z);
        return _gc.IngameState.Camera.WorldToScreen(world);
    }

    // Unit grid vectors that move up / right on screen, from the camera at the player.
    private Vector2 _gridUp = new Vector2(-0.7f, -0.7f), _gridRight = new Vector2(0.7f, -0.7f);

    private void UpdateScreenAxes(Vector2 p)
    {
        try
        {
            float z = Player.PosNum.Z;
            Vector2 s0 = GridToScreen(p, z);
            Vector2 sx = GridToScreen(p + new Vector2(10, 0), z) - s0;   // screen delta per +10 grid x
            Vector2 sy = GridToScreen(p + new Vector2(0, 10), z) - s0;   // screen delta per +10 grid y
            // Solve [sx sy] * g = target for screen up (0,-1) and right (1,0).
            float det = sx.X * sy.Y - sy.X * sx.Y;
            if (Math.Abs(det) < 1e-3f) return;
            Vector2 Solve(Vector2 t) => new Vector2((t.X * sy.Y - sy.X * t.Y) / det, (sx.X * t.Y - t.X * sx.Y) / det);
            Vector2 up = Solve(new Vector2(0, -1)), right = Solve(new Vector2(1, 0));
            if (up.Length() > 1e-6f) _gridUp = Vector2.Normalize(up);
            if (right.Length() > 1e-6f) _gridRight = Vector2.Normalize(right);
        }
        catch { }
    }

    // Screen-space direction (x right, y up, unit) from the player to a grid point.
    private Vector2 ScreenDir(Vector2 from, Vector2 to)
    {
        Vector2 d = to - from;
        // Express d in the screen basis: d = a*up + b*right  ->  solve 2x2.
        float det = _gridRight.X * _gridUp.Y - _gridUp.X * _gridRight.Y;
        if (Math.Abs(det) < 1e-6f || d.Length() < 1e-6f) return Vector2.Zero;
        float b = (d.X * _gridUp.Y - _gridUp.X * d.Y) / det;
        float a = (_gridRight.X * d.Y - d.X * _gridRight.Y) / det;
        var v = new Vector2(b, a);
        return v.Length() > 1e-6f ? Vector2.Normalize(v) : Vector2.Zero;
    }

    private static string DirNameAt(int sector) => sector switch
    {
        0 => T("right", "derecha"),
        1 => T("up right", "arriba a la derecha"),
        2 => T("up", "arriba"),
        3 => T("up left", "arriba a la izquierda"),
        4 => T("left", "izquierda"),
        5 => T("down left", "abajo a la izquierda"),
        6 => T("down", "abajo"),
        _ => T("down right", "abajo a la derecha"),
    };

    public static string DirName(Vector2 screenDir)
    {
        if (screenDir == Vector2.Zero) return T("here", "aquí");
        double ang = Math.Atan2(screenDir.Y, screenDir.X);
        int sector = (int)Math.Round(ang / (Math.PI / 4));
        sector = ((sector % 8) + 8) % 8;
        return DirNameAt(sector);
    }

    // ===================================================================
    // Seen ground (for the unexplored mark and the explored percentage)
    // ===================================================================

    // Poe2Access: a look reveals a disc of 60 cells around the player.
    private const int RevealCells = 60;
    private bool[] _seen;
    private int _seenW, _seenH;
    private Vector2 _lastRevealPos = new Vector2(-1000, -1000);
    public bool UnexploredMark = true;

    private void EnsureSeenMap()
    {
        if (_seen != null && _seenW == _gridW && _seenH == _gridH) return;
        _seen = new bool[_gridW * _gridH];
        _seenW = _gridW;
        _seenH = _gridH;
        _lastRevealPos = new Vector2(-1000, -1000);
    }

    private void Reveal(Vector2 p)
    {
        EnsureSeenMap();
        if ((p - _lastRevealPos).Length() < 3f) return;
        _lastRevealPos = p;

        int cx = (int)p.X, cy = (int)p.Y, r2 = RevealCells * RevealCells;
        for (int dy = -RevealCells; dy <= RevealCells; dy++)
        {
            int y = cy + dy;
            if (y < 0 || y >= _gridH) continue;
            int span = (int)Math.Sqrt(r2 - dy * dy);
            int x0 = Math.Max(0, cx - span), x1 = Math.Min(_gridW - 1, cx + span);
            int row = y * _gridW;
            for (int x = x0; x <= x1; x++) _seen[row + x] = true;
        }
    }

    private bool Seen(int x, int y) => _seen != null && InGrid(x, y) && _seen[y * _gridW + x];

    /// <summary>Percent of the walkable ground seen so far in this area.</summary>
    public int ExploredPercent()
    {
        if (_grid == null || _seen == null) return 0;
        long walk = 0, seen = 0;
        for (int y = 0; y < _gridH; y++)
        {
            int[] row = _grid[y];
            if (row == null) continue;
            int w = Math.Min(row.Length, _gridW);
            for (int x = 0; x < w; x++)
            {
                if (row[x] <= 0) continue;
                walk++;
                if (_seen[y * _gridW + x]) seen++;
            }
        }
        return walk > 0 ? (int)Math.Round(100.0 * seen / walk) : 0;
    }

    /// <summary>
    /// Unexplored mark for a ray (Poe2Access rules, simplified): follow the ray's own line up to
    /// its first wall (more than 7 blocked cells in a row; smaller obstacles are stepped over)
    /// and at most 120 cells. If it reaches unseen walkable ground: 2 = unexplored ground that
    /// goes on, 1 = a small unexplored pocket (a dead end). 0 = nothing unseen that way.
    /// </summary>
    private int UnexploredPips(Vector2 p, Vector2 d)
    {
        if (_seen == null) return 0;
        const int maxLen = RevealCells * 2;
        int blockedRun = 0;
        Vector2 firstUnseen = new Vector2(-1, -1);
        for (int t = 2; t <= maxLen; t++)
        {
            Vector2 c = p + d * t;
            int x = (int)c.X, y = (int)c.Y;
            if (!Walkable(x, y))
            {
                if (++blockedRun > 7) break;
                continue;
            }
            blockedRun = 0;
            if (!Seen(x, y)) { firstUnseen = c; break; }
        }
        if (firstUnseen.X < 0) return 0;

        // How much unseen ground lies beyond, reachable without crossing seen ground?
        // A search that runs out early means a small pocket, a dead end.
        const int deadEndCells = 1500, cap = 6000;
        var queue = new Queue<int>();
        var visited = new HashSet<int>();
        int sx = (int)firstUnseen.X, sy = (int)firstUnseen.Y;
        queue.Enqueue(sy * _gridW + sx);
        visited.Add(sy * _gridW + sx);
        int unseen = 0;
        while (queue.Count > 0 && visited.Count < cap)
        {
            int cur = queue.Dequeue();
            int cx = cur % _gridW, cy = cur / _gridW;
            unseen++;
            foreach (var (nx, ny) in new[] { (cx + 1, cy), (cx - 1, cy), (cx, cy + 1), (cx, cy - 1) })
            {
                if (!Walkable(nx, ny) || Seen(nx, ny)) continue;
                int ni = ny * _gridW + nx;
                if (visited.Add(ni)) queue.Enqueue(ni);
            }
        }
        bool exhausted = queue.Count == 0;
        return exhausted && unseen < deadEndCells ? 1 : 2;
    }

    // ===================================================================
    // Radar
    // ===================================================================

    private enum Reading { None, Open, Wall, Exit }

    private sealed class Ray
    {
        public string Name;
        public Reading Heard = Reading.None;     // last reading played (or silently set)
        public Reading Pending = Reading.None;
        public int PendingCount;
        public float Pan, WallPitch;
        public int BreathContour;
    }

    private readonly Ray[] _rays =
    {
        new Ray { Name = "up", Pan = 0f, WallPitch = 1f, BreathContour = 1 },
        new Ray { Name = "right", Pan = 0.9f, WallPitch = 1f, BreathContour = 0 },
        new Ray { Name = "down", Pan = 0f, WallPitch = 0.86f, BreathContour = -1 },
        new Ray { Name = "left", Pan = -0.9f, WallPitch = 1f, BreathContour = 0 },
    };

    private readonly Queue<Action> _cueQueue = new Queue<Action>();
    private long _nextCueMs;
    private long _nextRadarMs;
    private bool _radarPrimed;

    private void ResetAreaState()
    {
        foreach (var r in _rays) { r.Heard = Reading.None; r.Pending = Reading.None; r.PendingCount = 0; }
        _radarPrimed = false;
        _cueQueue.Clear();
        _guideTarget = null;
        _guideName = "";
        _scanIndex = -1;
        _nearby.Clear();
        _seen = null;
    }

    // Walking speed in cells per second, smoothed (for turn prompts "half a second before").
    private Vector2 _speedLastPos;
    private long _speedLastMs;
    private float _speed = 25f;

    private void TrackSpeed(long now, Vector2 p)
    {
        long dt = now - _speedLastMs;
        if (dt < 100) return;
        float moved = (p - _speedLastPos).Length();
        if (dt < 1000 && moved > 0.5f)
        {
            float v = moved * 1000f / dt;
            _speed = _speed * 0.8f + Math.Min(120f, v) * 0.2f;
        }
        _speedLastPos = p;
        _speedLastMs = now;
    }

    /// <summary>Called every frame while in game. uiBusy: a panel or menu has focus.</summary>
    public void Tick(long now, bool uiBusy, bool radarOn, Func<Entity, string> itemName)
    {
        RefreshGrid(now);
        if (_grid == null) return;
        Vector2 p = PlayerGrid();
        if (p.X < 0) return;
        TrackSpeed(now, p);
        Reveal(p);
        TrackDeath(p);
        if (!uiBusy) TickAreaDescription(now, p);

        if (uiBusy)
        {
            // Silent in menus and dialogue (the guide keeps its target); start again quietly
            // afterwards, without announcing a turn that went stale meanwhile.
            _radarPrimed = false;
            _cueQueue.Clear();
            ResetPrompts();
            return;
        }

        if (radarOn && now >= _nextRadarMs)
        {
            _nextRadarMs = now + 100;
            UpdateScreenAxes(p);
            PollRadar(p);
        }

        if (_cueQueue.Count > 0 && now >= _nextCueMs)
        {
            _cueQueue.Dequeue()();
            _nextCueMs = now + 90;
        }

        TickGuide(now, p);
        TickBump(now, p);
        if (AnnounceNearby) TickNearby(now, p, itemName);
        if (LootKind != null) TickLoot(now, p);
        TickEnemies(now, p);
        TickNpcs(now, p);
        TickObjects(now, p);
        if (now >= _nextIconLogMs) { _nextIconLogMs = now + 3000; LogMinimapIcons(); }
    }

    private long _nextIconLogMs;

    // ===================================================================
    // Enemies (Poe2Access): a growl for each enemy on screen, faster as it gets closer, with a
    // buzz for magic and rare and a ring for unique; packs named as they come into view with the
    // direction of their middle; two blips when the game's target lands on a living enemy.
    // ===================================================================

    public bool EnemySounds = true, AnnounceEnemies = true, EnemyDirections = true, CrosshairBeep = true, AnnounceTarget = true;
    public Func<float> EnemyVolume;

    private const float EnemyCells = 75f;          // about a screen
    private const int EnemyVoices = 4;
    private readonly Dictionary<long, long> _enemyNextMs = new Dictionary<long, long>();
    private readonly HashSet<long> _enemySeen = new HashSet<long>();
    private long _enemyArea = -1, _nextEnemyTickMs, _nextIntoViewMs, _targetId = -1;
    private string _lastTargetName = "";
    private long _lastTargetNameMs;

    private void TickEnemies(long now, Vector2 p)
    {
        if (now < _nextEnemyTickMs) return;
        _nextEnemyTickMs = now + 60;

        long area = Try(() => (long)_gc.Area.CurrentArea.Hash, 0L);
        if (area != _enemyArea) { _enemyArea = area; _enemySeen.Clear(); _enemyNextMs.Clear(); _targetId = -1; }

        var near = new List<(Entity E, long Id, Vector2 G, float Dist, MonsterRarity Rarity)>();
        Entity targeted = null;
        foreach (Entity e in Entities())
        {
            if (Category(e) != Cat.Enemies) continue;
            long id;
            Vector2 g;
            try { id = e.Id; g = e.GridPosNum; } catch { continue; }
            float dist = (g - p).Length();
            if (dist > EnemyCells) continue;
            near.Add((e, id, g, dist, Try(() => e.Rarity, MonsterRarity.White)));
            if (targeted == null && Try(() => e.GetComponent<Targetable>()?.isTargeted ?? false, false) && InSight(p, g)) targeted = e;
        }

        // Target: blips and the name when it lands on a new living enemy.
        long tid = targeted != null ? Try(() => (long)targeted.Id, -1L) : -1;
        if (tid != _targetId)
        {
            _targetId = tid;
            if (targeted != null)
            {
                if (CrosshairBeep) _audio.Play("crosshair", Synth.Blips, EnemyVolume?.Invoke() ?? 0.7f);
                if (AnnounceTarget)
                {
                    // The same name again within 2 seconds (the target moving between monsters of
                    // one kind) is left to the blips.
                    string named = Describe(targeted, null);
                    if (named != _lastTargetName || now - _lastTargetNameMs > 2000)
                    {
                        int life = (int)Math.Round(Try(() => targeted.GetComponent<Life>().HPPercentage, 1f) * 100);
                        _say(named + (life < 100 ? T($", {life} percent", $", {life} por ciento") : ""));
                    }
                    _lastTargetName = named;
                    _lastTargetNameMs = now;
                }
            }
        }

        // Coming into view: each enemy once per area, a pack in one line.
        if (AnnounceEnemies && now >= _nextIntoViewMs)
        {
            var fresh = near.Where(x => !_enemySeen.Contains(x.Id)).ToList();
            if (fresh.Count > 0)
            {
                _nextIntoViewMs = now + 700;
                foreach (var x in fresh) _enemySeen.Add(x.Id);
                var parts = new List<string>();
                foreach (var grp in fresh.GroupBy(x => (Name: NameOf(x.E, null), x.Rarity)).OrderBy(gp => gp.Min(x => x.Dist)))
                {
                    string rar = grp.Key.Rarity switch
                    {
                        MonsterRarity.Magic => T(", magic", ", mágico"),
                        MonsterRarity.Rare => T(", rare", ", raro"),
                        MonsterRarity.Unique => T(", unique", ", único"),
                        _ => "",
                    };
                    var mid = new Vector2(grp.Average(x => x.G.X), grp.Average(x => x.G.Y));
                    string dir = EnemyDirections ? ", " + ((mid - p).Length() < 2.5f ? T("here", "aquí") : DirName(ScreenDir(p, mid))) : "";
                    int n = grp.Count();
                    parts.Add(grp.Key.Name + (n > 1 ? ", " + n : "") + rar + dir);
                    if (parts.Count >= 4) break;
                }
                _sayQueued(string.Join(". ", parts));
            }
        }

        TickReach(near.Select(x => (x.E, x.G)).ToList(), p);
        TickDodge(near.Select(x => (x.E, x.Id, x.G, x.Dist, x.Rarity)).ToList(), p, now);
        TickEnemyStates(near.Select(x => (x.E, x.Id, x.G, x.Dist, x.Rarity)).ToList(), p, now);


        if (!EnemySounds) return;

        // Poe2Access rules: at most four streams (the nearest unique, then the nearest magic or
        // rare, then the nearest others); one hidden behind a wall (on the game's targeting map)
        // is silent, uniques too, since it cannot be hit from here; a stream
        // pulses every 600 ms at the screen's edge down to 150 ms beside you; starts are at least
        // 100 ms apart and the most overdue goes first; loudness stays the same (nearness is the
        // tempo); pan follows the side of the screen at up to 0.9; only enemies below you are
        // lowered in pitch, to 0.8 at the bottom edge.
        int Rank(MonsterRarity r) => r == MonsterRarity.Unique ? 0 : r == MonsterRarity.Rare || r == MonsterRarity.Magic ? 1 : 2;
        var streams = near
            .Where(x => InSight(p, x.G))   // behind a wall: silent, uniques too
            .OrderBy(x => Rank(x.Rarity)).ThenBy(x => x.Dist)
            .Take(EnemyVoices).ToList();
        if (streams.Count == 0 || now - _lastEnemyCueMs < 100) return;

        var dueNow = streams
            .Select(x => (It: x, Due: _enemyNextMs.TryGetValue(x.Id, out long d) ? d : long.MinValue / 2))
            .Where(x => x.Due <= now)
            .OrderBy(x => x.Due)
            .FirstOrDefault();
        if (dueNow.It.E == null) return;

        var it = dueNow.It;
        float pan = 0f, pitch = 1f;
        if (it.Dist >= 2.5f)
        {
            Vector2 sd = ScreenDir(p, it.G);
            pan = (float)Math.Round(Math.Max(-0.9f, Math.Min(0.9f, sd.X * 0.9f)), 1);
            float below = sd.Y < 0 ? Math.Min(1f, -sd.Y * it.Dist / ScreenHalfHeightCells) : 0f;
            pitch = (float)Math.Round(1f - 0.2f * below, 2);
            pitch = (float)(Math.Round(pitch * 20) / 20);   // steps of 0.05, so the cues cache well
        }
        var rarity = it.Rarity;
        _audio.Play("enemy:" + rarity + ":" + pan + ":" + pitch, () => Synth.Growl(rarity, pan, pitch), EnemyVolume?.Invoke() ?? 0.7f);
        float nearness = Math.Min(1f, it.Dist / EnemyCells);
        _enemyNextMs[it.Id] = now + 150 + (long)(450 * nearness);
        _lastEnemyCueMs = now;
    }

    // ===================================================================
    // NPCs and other players (Poe2Access): a light wooden tap for each of the two nearest NPCs on
    // screen and in sight, a quicker, higher double tap for the nearest other player; same pan,
    // pitch and tempo rules as the enemies (600 ms at the screen's edge, 150 ms beside you).
    // ===================================================================

    public bool NpcSounds = true, PlayerSounds = true;
    private readonly Dictionary<long, long> _npcNextMs = new Dictionary<long, long>();
    private long _lastNpcCueMs, _nextNpcTickMs;

    private void TickNpcs(long now, Vector2 p)
    {
        if (!NpcSounds && !PlayerSounds || now < _nextNpcTickMs) return;
        _nextNpcTickMs = now + 60;
        if (now - _lastNpcCueMs < 100) return;

        long me = Try(() => _gc.Player.Address, 0L);
        var npcs = new List<(long Id, Vector2 G, float Dist, bool Player)>();
        foreach (Entity e in Entities())
        {
            EntityType t = Try(() => e.Type, EntityType.Error);
            bool player = t == EntityType.Player;
            if (t != EntityType.Npc && !player) continue;
            if (player ? !PlayerSounds : !NpcSounds) continue;
            if (player && Try(() => e.Address, 0L) == me) continue;
            long id; Vector2 g;
            try { id = e.Id; g = e.GridPosNum; } catch { continue; }
            float dist = (g - p).Length();
            if (dist > EnemyCells || !InSight(p, g)) continue;
            npcs.Add((id, g, dist, player));
        }
        var streams = npcs.Where(x => !x.Player).OrderBy(x => x.Dist).Take(2)
            .Concat(npcs.Where(x => x.Player).OrderBy(x => x.Dist).Take(1)).ToList();
        var due = streams
            .Select(x => (It: x, Due: _npcNextMs.TryGetValue(x.Id, out long d) ? d : long.MinValue / 2))
            .Where(x => x.Due <= now).OrderBy(x => x.Due).FirstOrDefault();
        if (due.It.Id == 0 && due.Due == 0) return;
        if (!streams.Any(s => s.Id == due.It.Id && s.G == due.It.G)) return;

        var it = due.It;
        float pan = 0f, pitch = 1f;
        if (it.Dist >= 2.5f)
        {
            Vector2 sd = ScreenDir(p, it.G);
            pan = (float)Math.Round(Math.Max(-0.9f, Math.Min(0.9f, sd.X * 0.9f)), 1);
            float below = sd.Y < 0 ? Math.Min(1f, -sd.Y * it.Dist / ScreenHalfHeightCells) : 0f;
            pitch = (float)(Math.Round((1f - 0.2f * below) * 20) / 20);
        }
        bool pl = it.Player;
        _audio.Play("npc:" + pl + ":" + pan + ":" + pitch, () => Synth.Tap(pan, pitch, pl), EnemyVolume?.Invoke() ?? 0.7f);
        float nearness = Math.Min(1f, it.Dist / EnemyCells);
        _npcNextMs[it.Id] = now + (pl ? 120 : 150) + (long)((pl ? 380 : 450) * nearness);
        _lastNpcCueMs = now;
    }

    // ===================================================================
    // Reach ping (Poe2Access): two quick rising notes from the side of the nearest enemy in
    // sight when the gap between its body and yours falls to ReachDistance (world units, 40);
    // it sounds again only after every enemy has stepped back past that distance plus 25.
    // A body's size is half the width of its bounding box (Render component).
    // ===================================================================

    public bool ReachPing = true;
    public int ReachDistance = 40;
    private bool _reachArmed = true;

    private void TickReach(List<(Entity E, Vector2 G)> enemies, Vector2 p)
    {
        if (!ReachPing) return;
        Entity me = Try(() => _gc.Player, null);
        if (me == null) return;
        Vector3 mePos = Try(() => me.PosNum, Vector3.Zero);
        float meR = Radius(me);

        float best = float.MaxValue;
        Vector2 bestG = Vector2.Zero;
        foreach (var (e, g) in enemies)
        {
            if (!InSight(p, g)) continue;
            Vector3 pos = Try(() => e.PosNum, Vector3.Zero);
            if (pos == Vector3.Zero) continue;
            float gap = new Vector2(pos.X - mePos.X, pos.Y - mePos.Y).Length() - meR - Radius(e);
            if (gap < best) { best = gap; bestG = g; }
        }

        if (_reachArmed && best <= ReachDistance)
        {
            _reachArmed = false;
            Vector2 sd = (bestG - p).Length() < 2.5f ? Vector2.Zero : ScreenDir(p, bestG);
            float pan = (float)Math.Round(Math.Max(-0.9f, Math.Min(0.9f, sd.X * 0.9f)), 1);
            _audio.Play("reach:" + pan, () => Synth.ReachPing(pan), EnemyVolume?.Invoke() ?? 0.7f);
        }
        else if (!_reachArmed && best > ReachDistance + 25)
            _reachArmed = true;
    }

    private static float Radius(Entity e)
    {
        var r = Try(() => e.GetComponent<Render>(), null);
        if (r == null) return 0f;
        Vector3 b = Try(() => r.BoundsNum, Vector3.Zero);
        return Math.Max(0f, Math.Min(b.X, b.Y) * 0.5f);
    }

    private const float ScreenHalfHeightCells = 45f;   // about half the screen's height in map cells
    private long _lastEnemyCueMs;

    private int[][] _sightGrid;

    // In sight: a straight line the game's targeting map leaves open (so a wall of any thickness
    // hides what is behind it, while water does not); a single closed cell is let through, as
    // the map has specks on open ground. Without that map, the walkable grid with runs of more
    // than 8 blocked cells, as before.
    private bool InSight(Vector2 from, Vector2 to)
    {
        int[][] sight = _sightGrid;
        if (sight != null)
        {
            float l = (to - from).Length();
            int n = (int)Math.Ceiling(l);
            int run = 0;
            for (int s = 1; s < n; s++)
            {
                Vector2 q = from + (to - from) * (s / (float)n);
                int x = (int)q.X, y = (int)q.Y;
                bool open = y >= 0 && y < sight.Length && sight[y] != null && x >= 0 && x < sight[y].Length && sight[y][x] > 0;
                if (open) run = 0;
                else if (++run > 1) return false;
            }
            return true;
        }
        if (_grid == null) return true;
        float len = (to - from).Length();
        int steps = (int)Math.Ceiling(len);
        int blockedRun = 0;
        for (int s = 1; s < steps; s++)
        {
            Vector2 q = from + (to - from) * (s / (float)steps);
            if (Walkable((int)q.X, (int)q.Y)) blockedRun = 0;
            else if (++blockedRun > 8) return false;
        }
        return true;
    }

    // ===================================================================
    // Loot sounds: items on the ground keep sounding while in range (Poe2Access item sounds)
    // ===================================================================

    /// <summary>The cue for an item on the ground, or null for an item that should stay quiet.</summary>
    public Func<Entity, Synth.Loot?> LootKind;
    public Func<float> LootVolume;
    // The game's own sounds (GameSounds) and the drop sound of an item on the ground.
    public GameSounds GameAudio;
    public Func<Entity, string> LootEvent;
    private readonly Dictionary<long, string> _lootEvents = new Dictionary<long, string>();

    private const float LootCells = 90f;          // about a screen
    private const int LootVoices = 4;             // the nearest four sound at once
    private readonly Dictionary<long, long> _lootNextMs = new Dictionary<long, long>();
    private readonly Dictionary<long, Synth.Loot?> _lootKinds = new Dictionary<long, Synth.Loot?>();
    private long _nextLootTickMs, _lootCleanMs;

    private void TickLoot(long now, Vector2 p)
    {
        if (now < _nextLootTickMs) return;
        _nextLootTickMs = now + 120;

        var near = new List<(Entity E, long Id, Vector2 G, float Dist, Synth.Loot Kind)>();
        foreach (Entity e in Entities())
        {
            if (Try(() => e.Type, EntityType.Error) != EntityType.WorldItem) continue;
            long id;
            Vector2 g;
            try { id = e.Address; g = e.GridPosNum; } catch { continue; }
            float dist = (g - p).Length();
            if (dist > LootCells) continue;
            if (!_lootKinds.TryGetValue(id, out var kind))
            {
                kind = Try(() => LootKind(e), (Synth.Loot?)null);
                _lootKinds[id] = kind;
            }
            if (kind == null) continue;
            near.Add((e, id, g, dist, kind.Value));
        }

        if (now - _lootCleanMs > 10000)
        {
            _lootCleanMs = now;
            var live = new HashSet<long>(Entities().Select(x => Try(() => x.Address, 0L)));
            foreach (long k in _lootKinds.Keys.Where(k => !live.Contains(k)).ToList()) { _lootKinds.Remove(k); _lootNextMs.Remove(k); _lootEvents.Remove(k); }
        }

        // One cue per tick: the nearest due item among the nearest few.
        foreach (var it in near.OrderBy(x => x.Dist).Take(LootVoices))
        {
            if (_lootNextMs.TryGetValue(it.Id, out long due) && now < due) continue;
            float closeness = 1f - it.Dist / LootCells;
            float pan = 0f, pitch = 1f;
            if (it.Dist >= 2.5f)
            {
                Vector2 sd = ScreenDir(p, it.G);
                pan = (float)Math.Round(Math.Max(-1f, Math.Min(1f, sd.X)), 1);
                pitch = sd.Y > 0.4f ? 1.12f : sd.Y < -0.4f ? 0.89f : 1f;   // up the screen higher, down lower
            }
            var kind = it.Kind;
            float vol = LootVolume() * 1.4f * (0.6f + 0.4f * closeness);
            // The game's own drop sound for the item (from your installed game), with the game's
            // unique drop over a unique and the mod's short rarity tone over a magic or rare one;
            // the mod's own cue when the game's sounds cannot be played.
            string drop = null;
            if (GameAudio != null && GameAudio.Available && LootEvent != null && !_lootEvents.TryGetValue(it.Id, out drop))
                _lootEvents[it.Id] = drop = Try(() => LootEvent(it.E), null);
            float gameVol = vol * AudioEngine.Master;
            if (drop != null && GameAudio.Play(drop, pan, pitch, gameVol))
            {
                if (kind == Synth.Loot.Unique) GameAudio.Play(GameSounds.UniqueLayer, pan, pitch, gameVol);
                else if (kind == Synth.Loot.Rare || kind == Synth.Loot.Magic)
                    _audio.Play("rarity:" + kind + ":" + pan + ":" + pitch, () => Synth.LootCue(kind, pan, pitch), vol * 0.55f);
            }
            else
                _audio.Play("loot:" + kind + ":" + pan + ":" + pitch, () => Synth.LootCue(kind, pan, pitch), vol);
            _lootNextMs[it.Id] = now + 400 + (long)(1000 * (1 - closeness));   // more often as you get closer
            break;
        }
    }

    private static T Try<T>(Func<T> read, T fallback)
    {
        try { return read(); } catch { return fallback; }
    }

    // ===================================================================
    // Nearby: what you walk up to is named once (Poe2Access proximity rules)
    // ===================================================================

    private const float NearCells = 200f / GridToWorld;     // named within 200 units
    private const float RearmCells = 300f / GridToWorld;    // again only after going 300 units away...
    private const long RearmMs = 60000;                      // ...and a minute passing (items: no wait)

    private sealed class NearbyState
    {
        public bool Said;
        public bool WentAway;
        public long SaidAtMs;
    }

    private readonly Dictionary<long, NearbyState> _nearby = new Dictionary<long, NearbyState>();
    private long _nextNearbyMs;
    private long _nextNearbySpeechMs;

    private void TickNearby(long now, Vector2 p, Func<Entity, string> itemName)
    {
        if (now < _nextNearbyMs) return;
        _nextNearbyMs = now + 250;

        var toSay = new List<(float Dist, string Text, long Id)>();
        foreach (Entity e in Entities())
        {
            Cat cat = Category(e);
            if (cat == Cat.None || cat == Cat.Enemies || cat == Cat.Players && IsMe(e)) continue;

            long id;
            Vector2 g;
            try { id = e.Address; g = e.GridPosNum; } catch { continue; }
            float dist = (g - p).Length();

            if (!_nearby.TryGetValue(id, out var st)) { st = new NearbyState(); _nearby[id] = st; }
            if (dist > RearmCells) { st.WentAway = true; continue; }
            if (dist > NearCells) continue;

            bool due = !st.Said ||
                       (st.WentAway && (cat == Cat.Items || now - st.SaidAtMs >= RearmMs));
            if (!due) continue;

            string dir = dist < 2.5f ? T("here", "aquí") : DirName(ScreenDir(p, g));
            toSay.Add((dist, Describe(e, itemName) + ", " + dir, id));
        }

        if (toSay.Count == 0 || now < _nextNearbySpeechMs) return;

        var batch = toSay.OrderBy(x => x.Dist).Take(4).ToList();
        foreach (var x in batch)
        {
            var st = _nearby[x.Id];
            st.Said = true;
            st.WentAway = false;
            st.SaidAtMs = now;
        }
        _sayQueued(string.Join(". ", batch.Select(x => x.Text)));
        _nextNearbySpeechMs = now + 900;
    }

    private void PollRadar(Vector2 p)
    {
        var exits = Entities().Where(e => Category(e) == Cat.Exits).Select(e => e.GridPosNum).ToList();

        Vector2[] dirs = { _gridUp, _gridRight, -_gridUp, -_gridRight };
        for (int i = 0; i < 4; i++)
        {
            Ray ray = _rays[i];
            Vector2 d = dirs[i];
            Vector2 perp = new Vector2(-d.Y, d.X);

            float open = MarchOpen(p, d, perp, OpenCells + 2);
            bool exit = exits.Any(x =>
            {
                Vector2 v = x - p;
                float dist = v.Length();
                if (dist < 1 || dist > ExitCells) return false;
                return Vector2.Dot(Vector2.Normalize(v), d) > 0.7071f;   // within 45 degrees
            });

            Reading r;
            if (exit) r = Reading.Exit;
            else if (open < WallCells) r = Reading.Wall;
            else if (open >= OpenCells) r = Reading.Open;
            else r = ray.Heard == Reading.None ? Reading.Wall : ray.Heard;   // hysteresis band

            if (r == ray.Heard) { ray.PendingCount = 0; continue; }
            if (r != ray.Pending) { ray.Pending = r; ray.PendingCount = 1; continue; }
            if (++ray.PendingCount < 2) continue;

            ray.Heard = r;
            ray.PendingCount = 0;
            if (_radarPrimed)
            {
                int pips = UnexploredMark && (r == Reading.Open || r == Reading.Exit) ? UnexploredPips(p, d) : 0;
                QueueRayCue(ray, r, pips);
            }
        }
        _radarPrimed = true;
    }

    // Distance (cells) the way stays open along d. A lane three cells wide counts as blocked only
    // when at least two of its cells are, so a lone post does not read as a wall.
    private float MarchOpen(Vector2 p, Vector2 d, Vector2 perp, float max)
    {
        for (float t = 1; t <= max; t += 1)
        {
            Vector2 c = p + d * t;
            int blocked = 0;
            for (int k = -1; k <= 1; k++)
            {
                Vector2 q = c + perp * k;
                if (!Walkable((int)q.X, (int)q.Y)) blocked++;
            }
            if (blocked >= 2) return t;
        }
        return max;
    }

    private void QueueRayCue(Ray ray, Reading r, int pips)
    {
        float vol = _radarVolume();
        string side = ray.Name;
        // Pips are slightly lower on the down ray, like the other cues.
        float pipPitch = ray.WallPitch < 1f ? 0.9f : 1f;
        switch (r)
        {
            case Reading.Wall:
                _cueQueue.Enqueue(() => _audio.Play("tock:" + side, () => Synth.Tock(ray.Pan, ray.WallPitch), vol));
                break;
            case Reading.Open:
                _cueQueue.Enqueue(() => _audio.Play("breath:" + side + ":" + pips,
                    () => Synth.WithPips(Synth.Breath(ray.Pan, ray.BreathContour), ray.Pan, pipPitch, pips, 1f / 0.8f), vol * 0.8f));
                break;
            case Reading.Exit:
                _cueQueue.Enqueue(() => _audio.Play("tin:" + side + ":" + pips,
                    () => Synth.WithPips(Synth.Tin(ray.Pan, ray.WallPitch), ray.Pan, pipPitch, pips, 1f), vol));
                break;
        }
    }

    // ===================================================================
    // Wall bump: pushing into a wall without moving
    // ===================================================================

    private Vector2 _bumpLastPos;
    private long _bumpSinceMs;
    private bool _bumpArmed = true;
    public Vector2 LeftStick;   // set by the controller reader (x right, y up), -1..1

    private void TickBump(long now, Vector2 p)
    {
        if (LeftStick.Length() < 0.5f)
        {
            _bumpArmed = true;
            _bumpLastPos = p;
            _bumpSinceMs = now;
            return;
        }
        if ((p - _bumpLastPos).Length() > 1.4f)
        {
            _bumpLastPos = p;
            _bumpSinceMs = now;
            _bumpArmed = true;
            return;
        }
        if (!_bumpArmed || now - _bumpSinceMs < 200) return;

        // Is the grid blocked just ahead in the push direction?
        Vector2 push = Vector2.Normalize(_gridRight * LeftStick.X + _gridUp * LeftStick.Y);
        bool blocked = false;
        for (float t = 1; t <= 45f / GridToWorld + 1; t += 1)
        {
            Vector2 q = p + push * t;
            if (!Walkable((int)q.X, (int)q.Y)) { blocked = true; break; }
        }
        if (!blocked) return;

        _bumpArmed = false;
        float pan = Math.Max(-0.9f, Math.Min(0.9f, LeftStick.X));
        _audio.Play("thud:" + Math.Round(pan, 1), () => Synth.Thud(pan), _radarVolume());
    }

    // ===================================================================
    // Pathfinding (A* over the walkable grid, 8 directions, no corner cutting)
    // ===================================================================

    private int[] _pathCells;        // generation-stamped scratch arrays, one cell each
    private int[] _gScore;
    private int[] _came;
    private int _stamp;

    /// <summary>Route from a to b as a list of cells, or null. Capped so it never stalls.</summary>
    // Routes keep to squares of 3 or more (well clear of walls and water) when such a route
    // exists, and use squares of 2 only where there is no other way (narrow passages); a route
    // along a shore's 2s could not be walked (Twilight Strand, 19 Nov 2026).
    private List<(int X, int Y)> FindPath(Vector2 a, Vector2 b, int maxExpand = 250000)
    {
        return FindPathWith(a, b, maxExpand, 3) ?? FindPathWith(a, b, maxExpand, MinPassable);
    }

    private int _pathMin = MinPassable;
    private bool PathOk(int x, int y) => Clearance(x, y) >= _pathMin;

    private List<(int X, int Y)> FindPathWith(Vector2 a, Vector2 b, int maxExpand, int minClearance)
    {
        _pathMin = minClearance;
        if (_grid == null) return null;
        int sx = (int)a.X, sy = (int)a.Y, tx = (int)b.X, ty = (int)b.Y;
        if (!InGrid(sx, sy) || !InGrid(tx, ty)) return null;
        if (!PathOk(tx, ty)) { var t2 = NearestPathOk(tx, ty, 8); if (t2 == null) return null; (tx, ty) = t2.Value; }
        if (!PathOk(sx, sy)) { var s2 = NearestPathOk(sx, sy, 4); if (s2 == null) return null; (sx, sy) = s2.Value; }

        int n = _gridW * _gridH;
        if (_pathCells == null || _pathCells.Length != n)
        {
            _pathCells = new int[n];
            _gScore = new int[n];
            _came = new int[n];
            _stamp = 0;
        }
        _stamp++;

        int start = sy * _gridW + sx, goal = ty * _gridW + tx;
        var open = new PriorityQueue<int, int>();
        _pathCells[start] = _stamp;
        _gScore[start] = 0;
        _came[start] = -1;
        open.Enqueue(start, H(sx, sy, tx, ty));

        int expanded = 0;
        int[] dx = { 1, -1, 0, 0, 1, 1, -1, -1 };
        int[] dy = { 0, 0, 1, -1, 1, -1, 1, -1 };
        while (open.Count > 0 && expanded++ < maxExpand)
        {
            int cur = open.Dequeue();
            if (cur == goal) break;
            int cx = cur % _gridW, cy = cur / _gridW;
            int g0 = _gScore[cur];
            for (int k = 0; k < 8; k++)
            {
                int nx = cx + dx[k], ny = cy + dy[k];
                if (!PathOk(nx, ny)) continue;
                if (k >= 4 && (!PathOk(cx + dx[k], cy) || !PathOk(cx, cy + dy[k]))) continue;
                int ni = ny * _gridW + nx;
                int ng = g0 + (k < 4 ? 10 : 14) + EdgeCost(Clearance(nx, ny));
                if (_pathCells[ni] == _stamp && _gScore[ni] <= ng) continue;
                _pathCells[ni] = _stamp;
                _gScore[ni] = ng;
                _came[ni] = cur;
                open.Enqueue(ni, ng + H(nx, ny, tx, ty));
            }
        }
        if (_pathCells[goal] != _stamp) return null;

        var path = new List<(int, int)>();
        for (int c = goal; c != -1; c = _came[c]) path.Add((c % _gridW, c / _gridW));
        path.Reverse();
        return path;
    }

    private static int H(int x, int y, int tx, int ty)
    {
        int ax = Math.Abs(x - tx), ay = Math.Abs(y - ty);
        return 10 * Math.Max(ax, ay) + 4 * Math.Min(ax, ay);
    }

    private bool InGrid(int x, int y) => x >= 0 && y >= 0 && x < _gridW && y < _gridH;

    private (int, int)? NearestPathOk(int x, int y, int r)
    {
        for (int d = 1; d <= r; d++)
            for (int b = y - d; b <= y + d; b++)
                for (int a = x - d; a <= x + d; a++)
                    if (PathOk(a, b)) return (a, b);
        return null;
    }

    private (int, int)? NearestPassable(int x, int y, int r)
    {
        for (int d = 1; d <= r; d++)
            for (int b = y - d; b <= y + d; b++)
                for (int a = x - d; a <= x + d; a++)
                    if (Passable(a, b)) return (a, b);
        return null;
    }

    private (int, int)? NearestWalkable(int x, int y, int r)
    {
        for (int d = 1; d <= r; d++)
            for (int i = -d; i <= d; i++)
                foreach (var (a, b) in new[] { (x + i, y - d), (x + i, y + d), (x - d, y + i), (x + d, y + i) })
                    if (Walkable(a, b)) return (a, b);
        return null;
    }

    private bool LineWalkable(Vector2 a, Vector2 b)
    {
        Vector2 d = b - a;
        float len = d.Length();
        if (len < 1) return true;
        Vector2 step = d / len;
        for (float t = 0; t <= len; t += 0.5f)
        {
            Vector2 q = a + step * t;
            if (!PathOk((int)q.X, (int)q.Y)) return false;   // shortcuts keep the route's clearance
        }
        return true;
    }

    // Where to push now along a route: the farthest route point in a straight walkable line.
    private (Vector2 Point, int Index) NextLeg(Vector2 from, List<(int X, int Y)> path, int startIndex = 0)
    {
        int best = Math.Min(startIndex + 1, path.Count - 1);
        for (int i = best; i < path.Count && i < startIndex + 120; i++)
        {
            var c = new Vector2(path[i].X + 0.5f, path[i].Y + 0.5f);
            if (LineWalkable(from, c)) best = i; else break;
        }
        return (new Vector2(path[best].X + 0.5f, path[best].Y + 0.5f), best);
    }

    private static float PathLength(List<(int X, int Y)> path, int from = 0)
    {
        float len = 0;
        for (int i = from + 1; i < path.Count; i++)
        {
            int dx = Math.Abs(path[i].X - path[i - 1].X), dy = Math.Abs(path[i].Y - path[i - 1].Y);
            len += dx + dy == 2 ? 1.414f : 1f;
        }
        return len;
    }

    private static int Steps(float cells) => Math.Max(1, (int)Math.Round(cells / CellsPerStep));

    /// <summary>"14 steps, right 3, then up" for a target; straight line if no route.</summary>
    private string RouteText(Vector2 p, Vector2 target)
    {
        float straight = (target - p).Length();
        if (straight < 2.5f) return T("here", "aquí");

        var path = FindPath(p, target);
        if (path == null || path.Count < 2)
            {
            string toward = DirName(ScreenDir(p, target));
            return T($"{Steps(straight)} steps, {toward}, no route", $"{Steps(straight)} pasos, {toward}, sin ruta");
        }

        float total = PathLength(path);
        var legs = BuildLegs(path);
        if (legs.Count == 0)
        {
            string toward = DirName(ScreenDir(p, target));
            return T($"{Steps(total)} steps, {toward}", $"{Steps(total)} pasos, {toward}");
        }
        if (legs.Count > 1)
            return T($"{Steps(total)} steps, {legs[0].Dir} {Steps(legs[0].Len)}, then {legs[1].Dir}", $"{Steps(total)} pasos, {legs[0].Dir} {Steps(legs[0].Len)}, luego {legs[1].Dir}");
        return T($"{Steps(total)} steps, {legs[0].Dir}", $"{Steps(total)} pasos, {legs[0].Dir}");
    }

    // ===================================================================
    // Scanner
    // ===================================================================

    public enum Cat { Quest, Death, Exits, Unexplored, Waypoints, Characters, Players, Enemies, Items, Chests, Objects, None }

    private static readonly Cat[] Order = { Cat.Quest, Cat.Death, Cat.Exits, Cat.Unexplored, Cat.Waypoints, Cat.Characters, Cat.Players, Cat.Enemies, Cat.Items, Cat.Chests, Cat.Objects };

    private static string CatName(Cat c) => c switch
    {
        Cat.Quest => T("Quest", "Misión"),
        Cat.Death => T("Where you died", "Donde moriste"),
        Cat.Exits => T("Exits", "Salidas"),
        Cat.Unexplored => T("Unexplored", "Sin explorar"),
        Cat.Waypoints => T("Waypoints", "Transportadores"),
        Cat.Characters => T("Characters", "Personajes"),
        Cat.Players => T("Other players", "Otros jugadores"),
        Cat.Enemies => T("Enemies", "Enemigos"),
        Cat.Items => T("Items", "Objetos"),
        Cat.Chests => T("Chests", "Cofres"),
        Cat.Objects => T("Objects", "Objetos del entorno"),
        _ => "",
    };

    /// <summary>A scanner entry: a game object, or a place on the map (unexplored ground).</summary>
    private sealed class ScanItem
    {
        public Entity E;          // null for a place
        public Vector2 Pos;       // a place's grid position
        public string Label = ""; // a place's name
        public Cat Cat;
    }

    private static ScanItem Of(Entity e) => new ScanItem { E = e, Cat = Category(e) };

    private static Vector2 PosOf(ScanItem s)
    {
        if (s.E == null) return s.Pos;
        try { return s.E.GridPosNum; } catch { return s.Pos; }
    }

    private static bool StillThere(ScanItem s)
    {
        if (s.E == null) return true;
        try { return s.E.IsValid; } catch { return false; }
    }

    private static string NameOf(ScanItem s, Func<Entity, string> itemName) => s.E == null ? s.Label : NameOf(s.E, itemName);
    private static string Describe(ScanItem s, Func<Entity, string> itemName) => s.E == null ? s.Label : Describe(s.E, itemName);

    private int _catIndex;
    private int _scanIndex = -1;
    private long _lastScanMs;
    private ScanItem _scanChoice;

    private bool IsMe(Entity e) => Try(() => e.Address == _gc.Player.Address, false);

    private IEnumerable<Entity> Entities()
    {
        ICollection<Entity> list = null;
        try { list = _gc.EntityListWrapper.OnlyValidEntities; } catch { }
        return list ?? (IEnumerable<Entity>)Array.Empty<Entity>();
    }

    // ---- Where you died: the spot of your last death in each area (the scanner's "Where you
    // died" category, there while you are back in that same area) ----

    private readonly Dictionary<long, (Vector2 At, DateTime When)> _deaths = new Dictionary<long, (Vector2, DateTime)>();
    private bool _wasDead;
    private Vector2 _lastAlivePos = new Vector2(-1, -1);

    private void TrackDeath(Vector2 p)
    {
        bool dead;
        try
        {
            var life = _gc.Player.GetComponent<Life>();
            dead = life != null && life.MaxHP > 0 && life.CurHP <= 0;
        }
        catch { return; }
        if (!dead) _lastAlivePos = p;
        if (dead && !_wasDead)
        {
            long area = Try(() => (long)_gc.Area.CurrentArea.Hash, 0L);
            Vector2 at = _lastAlivePos.X >= 0 ? _lastAlivePos : p;
            _deaths[area] = (at, DateTime.Now);
            PluginLog.Write("Death noted at " + at + " in " + Try(() => _gc.Area.CurrentArea.DisplayName, "?"));
        }
        _wasDead = dead;
    }

    private List<ScanItem> DeathList()
    {
        long area = Try(() => (long)_gc.Area.CurrentArea.Hash, 0L);
        if (!_deaths.TryGetValue(area, out var d)) return new List<ScanItem>();
        int mins = (int)(DateTime.Now - d.When).TotalMinutes;
        string label = mins < 1 ? T("Where you died, just now", "Donde moriste, hace un momento")
            : mins == 1 ? T("Where you died, 1 minute ago", "Donde moriste, hace 1 minuto")
            : T($"Where you died, {mins} minutes ago", $"Donde moriste, hace {mins} minutos");
        return new List<ScanItem> { new ScanItem { Pos = d.At, Label = label, Cat = Cat.Death } };
    }

    /// <summary>The game shows a quest icon for it on the minimap (shown, not hidden).</summary>
    private static bool QuestMarked(Entity e)
    {
        var icon = Try(() => e.GetComponent<MinimapIcon>(), null);
        if (icon == null || icon.Address == 0) return false;
        if (!Try(() => icon.IsVisible, false) || Try(() => icon.IsHide, true)) return false;
        string name = Try(() => icon.Name, "") ?? "";
        return name.IndexOf("Quest", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // Logs once per area every minimap icon the game shows, to check what is marked.
    private long _iconsLoggedArea = -1;

    private void LogMinimapIcons()
    {
        long area = Try(() => (long)_gc.Area.CurrentArea.Hash, 0L);
        if (area == _iconsLoggedArea) return;
        var seen = new SortedDictionary<string, int>();
        foreach (Entity e in Entities())
        {
            var icon = Try(() => e.GetComponent<MinimapIcon>(), null);
            if (icon == null || icon.Address == 0) continue;
            if (!Try(() => icon.IsVisible, false) || Try(() => icon.IsHide, true)) continue;
            string key = (Try(() => icon.Name, "") ?? "") + " (" + Try(() => e.Type.ToString(), "?") + ")";
            seen[key] = seen.TryGetValue(key, out int n) ? n + 1 : 1;
        }
        if (seen.Count == 0) return;   // the entity list may not be filled yet
        _iconsLoggedArea = area;
        PluginLog.Write("Minimap icons shown in " + Try(() => _gc.Area.CurrentArea.DisplayName, "?") + ": " +
                        string.Join(", ", seen.Select(kv => kv.Key + (kv.Value > 1 ? " x" + kv.Value : ""))));
    }

    private static Cat Category(Entity e)
    {
        EntityType t;
        try { t = e.Type; } catch { return Cat.None; }
        // Only what the game itself marks for a quest on the minimap (a visible quest icon): no
        // marker of the mod's own, which a sighted player would not have. Monsters keep their
        // enemy sounds.
        if (t != EntityType.Monster && QuestMarked(e)) return Cat.Quest;
        switch (t)
        {
            case EntityType.AreaTransition:
            case EntityType.Portal:
            case EntityType.TownPortal:
                return Cat.Exits;
            case EntityType.Waypoint:
                return Cat.Waypoints;
            case EntityType.Npc:
                return Cat.Characters;
            case EntityType.Player:
                return Cat.Players;   // yourself is left out where the lists are made (IsMe)
            case EntityType.Monster:
                try { return e.IsHostile && e.IsAlive && e.IsTargetable ? Cat.Enemies : Cat.None; } catch { return Cat.None; }
            case EntityType.WorldItem:
                return Cat.Items;
            case EntityType.Chest:
                try { return e.IsOpened ? Cat.None : Cat.Chests; } catch { return Cat.Chests; }
            case EntityType.Stash:
            case EntityType.Shrine:
            case EntityType.QuestObject:
            case EntityType.Door:
            case EntityType.DoorSwitch:
            case EntityType.CraftUnlock:
                return Cat.Objects;
            default:
                return Cat.None;
        }
    }

    private bool OnScreen(Entity e)
    {
        try
        {
            var s = _gc.IngameState.Camera.WorldToScreen(e.PosNum);
            var w = _gc.Window.GetWindowRectangleTimeCache;
            return s.X >= 0 && s.Y >= 0 && s.X <= w.Width && s.Y <= w.Height;
        }
        catch { return false; }
    }

    private List<ScanItem> List(Cat cat, Vector2 p)
    {
        if (cat == Cat.Unexplored) return UnexploredList(p);
        if (cat == Cat.Death) return DeathList();
        var list = Entities().Where(e => Category(e) == cat && !IsMe(e));
        if (cat == Cat.Enemies) list = list.Where(OnScreen);   // only what a sighted player sees
        return list.OrderBy(e => (e.GridPosNum - p).LengthSquared()).Take(40).Select(Of).ToList();
    }

    // ===================================================================
    // Unexplored ground (Poe2Access rules, simplified)
    // ===================================================================
    //  Where seen walkable ground touches unseen walkable ground, one entry per stretch, named by
    //  how much unseen ground lies behind it, reachable without crossing seen ground:
    //    "Large unexplored ground"  10 reveal discs or more (a good part of the area)
    //    "Unexplored ground"        a way that goes on
    //    "Small unexplored ground"  under 2 discs: a room or a short branch that soon ends
    //    "Unexplored nook"          all of it within one look (60 cells) of its edge: a dead end
    //  Large first, then ground, small, nooks; nearest first within each.

    private const float RevealDisc = (float)(Math.PI * RevealCells * RevealCells);   // ~11,300 cells
    private int[] _uxStamp;
    private int _uxGen;
    private List<ScanItem> _uxCache;
    private long _uxCacheMs = -100000;
    private Vector2 _uxCachePos = new Vector2(-1000, -1000);

    private List<ScanItem> UnexploredList(Vector2 p)
    {
        if (_grid == null || _seen == null) return new List<ScanItem>();
        long now = Environment.TickCount64;
        if (_uxCache != null && now - _uxCacheMs < 1500 && (p - _uxCachePos).Length() < 10) return _uxCache;

        int n = _gridW * _gridH;
        if (_uxStamp == null || _uxStamp.Length != n) { _uxStamp = new int[n]; _uxGen = 0; }

        // 1. The seen ground reachable from the player, and its edges with unseen ground.
        int seenGen = ++_uxGen;
        var edges = new List<(int Seen, int Unseen)>();
        var queue = new Queue<int>();
        int sx = (int)p.X, sy = (int)p.Y;
        if (!Passable(sx, sy)) { var w = NearestPassable(sx, sy, 4); if (w == null) return new List<ScanItem>(); (sx, sy) = w.Value; }
        int start = sy * _gridW + sx;
        _uxStamp[start] = seenGen;
        queue.Enqueue(start);
        int visited = 0;
        while (queue.Count > 0 && visited++ < 3_000_000)
        {
            int cur = queue.Dequeue();
            int cx = cur % _gridW, cy = cur / _gridW;
            foreach (var (nx, ny) in new[] { (cx + 1, cy), (cx - 1, cy), (cx, cy + 1), (cx, cy - 1) })
            {
                if (!Passable(nx, ny)) continue;
                int ni = ny * _gridW + nx;
                if (!_seen[ni]) { edges.Add((cur, ni)); continue; }
                if (_uxStamp[ni] == seenGen) continue;
                _uxStamp[ni] = seenGen;
                queue.Enqueue(ni);
            }
        }

        // 2. Each unseen region behind those edges: its size, and how far it reaches from its edge.
        var regionOf = new Dictionary<int, int>();          // unseen cell -> region id
        var regions = new List<(int Size, float Reach, bool Capped)>();
        foreach (var (_, unseen) in edges)
        {
            if (regionOf.ContainsKey(unseen) || _uxStamp[unseen] > seenGen) continue;
            int id = regions.Count;
            int gen = ++_uxGen;
            int ex = unseen % _gridW, ey = unseen / _gridW;
            int size = 0;
            float reach = 0;
            queue.Clear();
            _uxStamp[unseen] = gen;
            queue.Enqueue(unseen);
            // No cap: each unseen region is measured once, so the total is at most the map.
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                size++;
                int cx = cur % _gridW, cy = cur / _gridW;
                float d = (float)Math.Sqrt((cx - ex) * (cx - ex) + (cy - ey) * (cy - ey));
                if (d > reach) reach = d;
                foreach (var (nx, ny) in new[] { (cx + 1, cy), (cx - 1, cy), (cx, cy + 1), (cx, cy - 1) })
                {
                    if (!Passable(nx, ny)) continue;
                    int ni = ny * _gridW + nx;
                    if (_seen[ni] || _uxStamp[ni] == gen) continue;
                    _uxStamp[ni] = gen;
                    queue.Enqueue(ni);
                }
            }
            regions.Add((size, reach, false));
            // Every edge cell of this region belongs to it.
            foreach (var (_, u) in edges)
                if (_uxStamp[u] == gen) regionOf[u] = id;
        }

        // 3. One entry per stretch: edge cells of a region at least 30 cells apart, nearest first.
        var entries = new List<(int Rank, float Dist, ScanItem Item)>();
        var reps = new List<(int Region, Vector2 At)>();
        foreach (var (seenCell, unseen) in edges.OrderBy(e => DistSq(e.Seen, p)))
        {
            if (!regionOf.TryGetValue(unseen, out int id)) continue;
            var at = new Vector2(unseen % _gridW + 0.5f, unseen / _gridW + 0.5f);
            if (reps.Any(r => r.Region == id && (r.At - at).Length() < 30)) continue;
            reps.Add((id, at));

            var (size, reach, capped) = regions[id];
            int rank;
            string label;
            if (!capped && reach <= RevealCells && size < RevealDisc) { rank = 3; label = T("Unexplored nook", "Rincón sin explorar"); }
            else if (!capped && size < 2 * RevealDisc) { rank = 2; label = T("Small unexplored ground", "Terreno pequeño sin explorar"); }
            else if (capped || size >= 10 * RevealDisc) { rank = 0; label = T("Large unexplored ground", "Terreno grande sin explorar"); }
            else { rank = 1; label = T("Unexplored ground", "Terreno sin explorar"); }

            entries.Add((rank, (at - p).Length(), new ScanItem { Pos = at, Label = label, Cat = Cat.Unexplored }));
            if (entries.Count >= 30) break;
        }

        _uxCache = entries.OrderBy(e => e.Rank).ThenBy(e => e.Dist).Take(20).Select(e => e.Item).ToList();
        _uxCacheMs = now;
        _uxCachePos = p;
        return _uxCache;
    }

    private float DistSq(int cell, Vector2 p)
    {
        float dx = cell % _gridW - p.X, dy = cell / _gridW - p.Y;
        return dx * dx + dy * dy;
    }

    public static string NameOf(Entity e, Func<Entity, string> itemName)
    {
        try
        {
            if (e.Type == EntityType.WorldItem)
            {
                string n = itemName(e);
                if (!string.IsNullOrEmpty(n)) return n;
            }
            string r = e.RenderName;
            if (!string.IsNullOrWhiteSpace(r)) return r;
            string path = e.Path ?? "";
            int i = path.LastIndexOf('/');
            return i >= 0 ? path.Substring(i + 1) : path;
        }
        catch { return T("Unknown", "Desconocido"); }
    }

    private static string Describe(Entity e, Func<Entity, string> itemName)
    {
        string name = NameOf(e, itemName);
        try
        {
            if (e.Type == EntityType.Player)
            {
                var pc = e.GetComponent<Player>();
                int level = pc?.Level ?? 0;
                string who = pc?.PlayerName;
                if (string.IsNullOrWhiteSpace(who)) who = name;
                return who + (level > 0 ? T($", level {level}", $", nivel {level}") : "");
            }
            if (e.Type == EntityType.Monster)
            {
                string rar = e.Rarity switch
                {
                    MonsterRarity.Magic => T($"magic {name}", $"{name} mágico"),
                    MonsterRarity.Rare => T($"rare {name}", $"{name} raro"),
                    MonsterRarity.Unique => T($"unique {name}", $"{name} único"),
                    _ => name,
                };
                return rar;
            }
        }
        catch { }
        return name;
    }

    /// <summary>Scanner step. dCat: -1/+1 change category; dItem: -1/+1 move in it.</summary>
    public void Scan(int dCat, int dItem, long now, Func<Entity, string> itemName)
    {
        Vector2 p = PlayerGrid();
        if (_grid == null || p.X < 0) { _say(T("No map yet", "Aún no hay mapa")); return; }

        // After ten quiet seconds the next flick starts again from the nearest entry.
        if (now - _lastScanMs > 10000) _scanIndex = -1;
        _lastScanMs = now;

        bool catChanged = false;
        if (dCat != 0)
        {
            for (int tries = 0; tries < Order.Length; tries++)
            {
                _catIndex = ((_catIndex + dCat) % Order.Length + Order.Length) % Order.Length;
                if (List(Order[_catIndex], p).Count > 0) break;
            }
            _scanIndex = -1;
            catChanged = true;
        }

        var list = List(Order[_catIndex], p);
        if (list.Count == 0) { _say(T($"{CatName(Order[_catIndex])}, none", $"{CatName(Order[_catIndex])}, ninguno")); _scanChoice = null; return; }

        if (dItem != 0 || _scanIndex < 0)
            _scanIndex = _scanIndex < 0 ? 0 : ((_scanIndex + dItem) % list.Count + list.Count) % list.Count;
        if (_scanIndex >= list.Count) _scanIndex = 0;

        _scanChoice = list[_scanIndex];
        string text = Describe(_scanChoice, itemName) + ", " + RouteText(p, PosOf(_scanChoice));
        if (catChanged) text = T($"{CatName(Order[_catIndex])}. {text}", $"{CatName(Order[_catIndex])}. {text}");
        _say(text);
    }

    /// <summary>Where the scanner's choice is now.</summary>
    public bool SayChoice(Func<Entity, string> itemName)
    {
        if (_scanChoice == null) return false;
        Vector2 p = PlayerGrid();
        if (!StillThere(_scanChoice)) { _say(T($"{NameOf(_scanChoice, itemName)} is gone", $"{NameOf(_scanChoice, itemName)} ya no está")); _scanChoice = null; return true; }
        _say(Describe(_scanChoice, itemName) + ", " + RouteText(p, PosOf(_scanChoice)));
        return true;
    }

    // ===================================================================
    // Guide beacon
    // ===================================================================

    private ScanItem _guideTarget;
    private Vector2 _guideLastPos;
    private string _guideName = "";
    private List<(int X, int Y)> _guidePath;
    private long _guidePathMs;
    private long _nextChimeMs;
    private Func<Entity, string> _guideItemName;
    private Cat _guideCat = Cat.None;

    public bool Guiding => _guideTarget != null;

    // ---- Turn prompts while guiding (Poe2Access route-following rules) ----
    //  * "Now up, 5" about half a second before each turn, at your walking pace, never before
    //    half of the current leg has been walked;
    //  * if the way changes without a prompt (re-plan, a short-cut), the new leg is said;
    //  * "Off the route" once when you are more than 8 cells from the route for 2 seconds.
    private Vector2 _legEnd = new Vector2(-1, -1);
    private float _legStartLen;
    private bool _legPrompted;
    private string _lastPromptDir = "";
    private long _lastPromptMs;
    private long _offRouteSinceMs = -1;
    private bool _offRouteSaid;
    private const float OffRouteCells = 8f;

    private void ResetPrompts()
    {
        _legEnd = new Vector2(-1, -1);
        _legPrompted = false;
        _lastPromptDir = "";
        _offRouteSinceMs = -1;
        _offRouteSaid = false;
    }

    // The route as legs: stretches that go one screen direction. Sampled every 4 cells; legs
    // shorter than 5 cells are folded into the one before, so staircase steps do not count as
    // turns. A leg's end is a fixed place on the ground, so a turn is announced once.
    private sealed class Leg
    {
        public string Dir;
        public Vector2 End;
        public int EndIdx;
        public float Len;
    }

    private List<Leg> BuildLegs(List<(int X, int Y)> path)
    {
        var legs = new List<Leg>();
        if (path == null || path.Count < 2) return legs;

        const int sample = 4;
        int prev = 0;
        for (int i = sample; ; i += sample)
        {
            int cur = Math.Min(i, path.Count - 1);
            var a = new Vector2(path[prev].X, path[prev].Y);
            var b = new Vector2(path[cur].X, path[cur].Y);
            string dir = DirName(ScreenDir(a, b));
            float len = (b - a).Length();

            if (legs.Count > 0 && legs[legs.Count - 1].Dir == dir)
            {
                var last = legs[legs.Count - 1];
                last.End = b; last.EndIdx = cur; last.Len += len;
            }
            else legs.Add(new Leg { Dir = dir, End = b, EndIdx = cur, Len = len });

            prev = cur;
            if (cur == path.Count - 1) break;
        }

        // Fold short legs into the previous one, then merge neighbours that now match.
        var folded = new List<Leg>();
        foreach (var leg in legs)
        {
            if (folded.Count > 0 && (leg.Len < 5f || folded[folded.Count - 1].Dir == leg.Dir))
            {
                var last = folded[folded.Count - 1];
                last.End = leg.End; last.EndIdx = leg.EndIdx; last.Len += leg.Len;
            }
            else folded.Add(leg);
        }
        return folded;
    }

    private List<Leg> _guideLegs;

    private void RoutePrompt(long now, Vector2 p, int nearest, float offRoute)
    {
        if (_guideLegs == null || _guideLegs.Count == 0) return;

        // The leg you are on: the first whose end lies ahead of your place on the route.
        int k = _guideLegs.FindIndex(l => l.EndIdx > nearest);
        if (k < 0) return;
        Leg leg = _guideLegs[k];

        // Off the route.
        if (offRoute > OffRouteCells)
        {
            if (_offRouteSinceMs < 0) _offRouteSinceMs = now;
            if (!_offRouteSaid && now - _offRouteSinceMs >= 2000)
            {
                _offRouteSaid = true;
                {
                    string back = DirName(ScreenDir(p, new Vector2(_guidePath[nearest].X, _guidePath[nearest].Y)));
                    _say(T($"Off the route. Go {back}", $"Fuera de la ruta. Ve {back}"));
                }
                _lastPromptMs = now;
            }
            return;
        }
        if (offRoute < OffRouteCells / 2) { _offRouteSinceMs = -1; _offRouteSaid = false; }

        float toTurn = PathLength(_guidePath.GetRange(nearest, leg.EndIdx - nearest + 1));

        // A different leg than before (passed a turn, or the route was re-planned).
        if ((leg.End - _legEnd).Length() > 6f)
        {
            bool first = _legEnd.X < 0;
            _legEnd = leg.End;
            _legStartLen = toTurn;
            _legPrompted = false;

            if (first) _lastPromptDir = leg.Dir;
            else if (leg.Dir != _lastPromptDir && now - _lastPromptMs > 1500 && toTurn >= 4f)
            {
                // Not announced before the turn: say it now.
                _say(T($"Now {leg.Dir}, {Steps(toTurn)}", $"Ahora {leg.Dir}, {Steps(toTurn)}"));
                _lastPromptDir = leg.Dir;
                _lastPromptMs = now;
            }
        }

        // About half a second before the turn (never before half the leg is walked): the next leg.
        if (_legPrompted || k + 1 >= _guideLegs.Count) return;
        float lead = Math.Max(3f, _speed * 0.5f);
        if (toTurn > lead || toTurn > _legStartLen / 2) return;

        Leg next = _guideLegs[k + 1];
        _legPrompted = true;
        _say(T($"Now {next.Dir}, {Steps(next.Len)}", $"Ahora {next.Dir}, {Steps(next.Len)}"));
        _lastPromptDir = next.Dir;
        _lastPromptMs = now;
    }

    public void ToggleGuide(Func<Entity, string> itemName)
    {
        if (_guideTarget != null)
        {
            _say(T("Guide off", "Guía desactivada"));
            _guideTarget = null;
            return;
        }
        if (_scanChoice == null) { _say(T("Choose something with the scanner first", "Primero elige algo con el escáner")); return; }

        _guideTarget = _scanChoice;
        _guideCat = _guideTarget.Cat;
        _guideItemName = itemName;
        _guideName = NameOf(_guideTarget, itemName);
        _guideLastPos = PosOf(_guideTarget);
        _guidePath = null;
        _guidePathMs = 0;
        ResetPrompts();
        Vector2 p = PlayerGrid();
        _say(T($"Guiding to {_guideName}, {RouteText(p, _guideLastPos)}", $"Guiando a {_guideName}, {RouteText(p, _guideLastPos)}"));
    }

    private void TickGuide(long now, Vector2 p)
    {
        if (_guideTarget == null) return;

        bool valid = StillThere(_guideTarget);
        if (valid) _guideLastPos = PosOf(_guideTarget);

        float dist = (_guideLastPos - p).Length();

        // Unexplored ground: the guide leads to where the unseen ground began (about 60 cells of
        // new ground on), then the list is worked out again.
        if (_guideTarget.E == null && dist < 13f)
        {
            _say(T($"Arrived at {_guideName}. Scan unexplored again to go on.", $"Llegaste a {_guideName}. Escanea de nuevo lo sin explorar para seguir."));
            _guideTarget = null;
            _uxCache = null;
            return;
        }

        // Arrived: as close as the game lets you walk. NPCs and objects stop you about 12 cells
        // away to interact, so they count from 13 cells; items on the ground from 6.
        float arrive = _guideCat == Cat.Items ? 6f : 13f;
        if (dist < arrive)
        {
            _say(T($"Arrived at {_guideName}", $"Llegaste a {_guideName}"));
            _guideTarget = null;
            return;
        }
        if (!valid && dist < arrive + 4f)
        {
            _say(T($"Arrived where {_guideName} was", $"Llegaste a donde estaba {_guideName}"));
            _guideTarget = null;
            return;
        }

        if (_guidePath == null || now - _guidePathMs > 700)
        {
            _guidePath = FindPath(p, _guideLastPos, 150000);
            _guideLegs = BuildLegs(_guidePath);
            _guidePathMs = now;
        }

        Vector2 aim;
        float remaining;
        if (_guidePath != null && _guidePath.Count >= 2)
        {
            int nearest = 0;
            float best = float.MaxValue;
            for (int i = 0; i < _guidePath.Count; i += 2)
            {
                float d = (new Vector2(_guidePath[i].X, _guidePath[i].Y) - p).LengthSquared();
                if (d < best) { best = d; nearest = i; }
            }
            (aim, _) = NextLeg(p, _guidePath, nearest);
            remaining = PathLength(_guidePath, nearest);
            if (RoutePrompts) RoutePrompt(now, p, nearest, (float)Math.Sqrt(best));
        }
        else
        {
            aim = _guideLastPos;
            remaining = dist;
        }

        if (now < _nextChimeMs) return;

        Vector2 sd = ScreenDir(p, aim);
        float pan = Math.Max(-1f, Math.Min(1f, sd.X));
        float pitch = sd.Y > 0.4f ? 1.25f : sd.Y < -0.4f ? 0.8f : 1f;
        _audio.Play("chime:" + Math.Round(pan, 1) + ":" + pitch, () => Synth.Chime((float)Math.Round(pan, 1), pitch), _guideVolume());

        // 1.2 s far away down to 0.25 s close.
        float t = Math.Min(1f, remaining / 120f);
        _nextChimeMs = now + (long)(250 + 950 * t);
    }
}
