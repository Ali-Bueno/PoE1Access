using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using static PoE1Access.Loc;

namespace PoE1Access;

// A description of the area, made from what the game knows about it (the game has no written
// descriptions of its areas): what it is (town, act, level), how big and how open its ground
// is, which way it stretches on screen, where you are in it, its waypoint, its bosses and the
// areas it leads to.
//
//   "The Coast, act 1, area level 2. A large area, mostly open ground, stretching up and down.
//    You are near its bottom edge. It has a waypoint. It leads to Lioneye's Watch, The Mud Flats
//    and The Tidal Island."
//
// Size counts the walkable squares; openness is the share of them far from any wall (5 on the
// pathfinding grid). Said once per area per session on arriving, and again with the status.
internal sealed partial class Navigator
{
    private string _areaDescribedFor = "";
    private readonly HashSet<string> _areasDescribed = new HashSet<string>();
    private long _areaArrivedMs = -1;
    private uint _areaArrivedHash;

    public bool DescribeAreas = true;

    // Once the area's grid has loaded and the player stands in it: describe it on a first visit.
    private void TickAreaDescription(long now, Vector2 p)
    {
        if (!DescribeAreas || _grid == null || p.X < 0) return;
        if (_areaArrivedHash != _areaHash) { _areaArrivedHash = _areaHash; _areaArrivedMs = now; return; }
        if (_areaArrivedMs < 0 || now - _areaArrivedMs < 2500) return;   // after the arrival speech
        _areaArrivedMs = -1;
        string id = Try(() => _gc.IngameState.Data.CurrentArea?.Id, "") ?? "";
        if (id.Length == 0 || !_areasDescribed.Add(id)) return;
        string text = DescribeArea();
        if (text.Length > 0) _sayQueued(text);
    }

    /// <summary>The whole description of the current area, or "" when it is not known yet.</summary>
    public string DescribeArea()
    {
        var area = Try(() => _gc.IngameState.Data.CurrentArea, null);
        if (area == null) return "";
        var parts = new List<string>();

        string name = Try(() => area.Name, "") ?? "";
        int act = Try(() => area.Act, 0);
        int level = Try(() => _gc.IngameState.Data.CurrentAreaLevel, 0);
        bool town = Try(() => area.IsTown, false), hideout = Try(() => area.IsHideout, false);
        var head = new List<string>();
        if (name.Length > 0) head.Add(name);
        if (hideout) head.Add(act > 0 ? T($"hideout of act {act}", $"guarida del acto {act}") : T("hideout", "guarida"));
        else if (town) head.Add(act > 0 ? T($"town of act {act}", $"pueblo del acto {act}") : T("town", "pueblo"));
        else if (act > 0) head.Add(T($"act {act}", $"acto {act}"));
        if (level > 0 && !town && !hideout) head.Add(T($"area level {level}", $"nivel de área {level}"));
        if (head.Count > 0) parts.Add(string.Join(", ", head));

        string ground = GroundText(PlayerGrid());
        if (ground.Length > 0) parts.Add(ground);

        if (Try(() => area.HasWaypoint, false)) parts.Add(T("It has a waypoint", "Tiene un transportador"));
        var bosses = Try(() => area.Bosses?.Select(b => b?.ToString() ?? "").Where(b => b.Length > 0).ToList(), null);
        if (bosses != null && bosses.Count > 0) { string names = string.Join(", ", bosses); parts.Add(bosses.Count == 1 ? T($"Boss: {names}", $"Jefe: {names}") : T($"Bosses: {names}", $"Jefes: {names}")); }
        var links = Try(() => area.Connections?.Select(c => Try(() => c.Name, "") ?? "").Where(c => c.Length > 0).Distinct().ToList(), null);
        if (links != null && links.Count > 0) parts.Add(T($"It leads to {JoinAnd(links)}", $"Conduce a {JoinAnd(links)}"));
        return string.Join(". ", parts);
    }

    private static string JoinAnd(List<string> items) =>
        items.Count == 1 ? items[0] : T($"{string.Join(", ", items.Take(items.Count - 1))} and {items[items.Count - 1]}", $"{string.Join(", ", items.Take(items.Count - 1))} y {items[items.Count - 1]}");

    // "A large area, mostly open ground, stretching up and down. You are near its bottom edge."
    private string GroundText(Vector2 p)
    {
        var grid = _grid;
        if (grid == null) return "";
        int walkable = 0, open = 0;
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        // Screen coordinates of a grid square relative to the player (right, up), unnormalised.
        float det = _gridRight.X * _gridUp.Y - _gridUp.X * _gridRight.Y;
        bool axes = Math.Abs(det) > 1e-6f;
        Vector2 Screen(float dx, float dy) => new Vector2((dx * _gridUp.Y - _gridUp.X * dy) / det, (_gridRight.X * dy - dx * _gridRight.Y) / det);
        for (int y = 0; y < grid.Length; y += 2)
        {
            int[] row = grid[y];
            if (row == null) continue;
            for (int x = 0; x < row.Length; x += 2)
            {
                int v = row[x];
                if (v <= 0) continue;
                walkable++;
                if (v >= 5) open++;
                if (!axes) continue;
                Vector2 s = Screen(x - p.X, y - p.Y);
                if (s.X < minX) minX = s.X; if (s.X > maxX) maxX = s.X;
                if (s.Y < minY) minY = s.Y; if (s.Y > maxY) maxY = s.Y;
            }
        }
        if (walkable == 0) return "";
        walkable *= 4;   // every second square in both directions was counted
        string size = walkable < 40000 ? T("A small area", "Un área pequeña") : walkable < 150000 ? T("A medium-sized area", "Un área mediana") : walkable < 400000 ? T("A large area", "Un área grande") : T("A very large area", "Un área muy grande");
        double openShare = open * 4.0 / walkable;
        string openness = openShare >= 0.6 ? T("mostly open ground", "terreno mayormente abierto") : openShare >= 0.35 ? T("open ground and narrow paths", "terreno abierto y caminos estrechos") : T("mostly narrow paths", "mayormente caminos estrechos");
        var text = size + ", " + openness;
        if (!axes) return text;

        float w = maxX - minX, h = maxY - minY;
        if (w > 0 && h > 0)
        {
            if (w / h > 1.6f) text += T(", stretching left and right", ", se extiende a izquierda y derecha");
            else if (h / w > 1.6f) text += T(", stretching up and down", ", se extiende arriba y abajo");
        }
        // Where you are in it: the player is at (0, 0) of these coordinates.
        float fx = w > 0 ? -minX / w : 0.5f, fy = h > 0 ? -minY / h : 0.5f;   // 0 = left / bottom
        string where;
        if (fy < 0.25f && fx < 0.25f) where = T("near its bottom left corner", "cerca de su esquina inferior izquierda");
        else if (fy < 0.25f && fx > 0.75f) where = T("near its bottom right corner", "cerca de su esquina inferior derecha");
        else if (fy > 0.75f && fx < 0.25f) where = T("near its top left corner", "cerca de su esquina superior izquierda");
        else if (fy > 0.75f && fx > 0.75f) where = T("near its top right corner", "cerca de su esquina superior derecha");
        else if (fy < 0.25f) where = T("near its bottom edge", "cerca de su borde inferior");
        else if (fy > 0.75f) where = T("near its top edge", "cerca de su borde superior");
        else if (fx < 0.25f) where = T("near its left edge", "cerca de su borde izquierdo");
        else if (fx > 0.75f) where = T("near its right edge", "cerca de su borde derecho");
        else where = T("near its middle", "cerca de su centro");
        return text + T($". You are {where}", $". Estás {where}");
    }
}
