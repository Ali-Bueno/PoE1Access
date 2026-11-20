using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

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
        string kind = hideout ? "hideout" : town ? "town" : "";
        var head = new List<string>();
        if (name.Length > 0) head.Add(name);
        if (kind.Length > 0) head.Add(kind + (act > 0 ? " of act " + act : ""));
        else if (act > 0) head.Add("act " + act);
        if (level > 0 && !town && !hideout) head.Add("area level " + level);
        if (head.Count > 0) parts.Add(string.Join(", ", head));

        string ground = GroundText(PlayerGrid());
        if (ground.Length > 0) parts.Add(ground);

        if (Try(() => area.HasWaypoint, false)) parts.Add("It has a waypoint");
        var bosses = Try(() => area.Bosses?.Select(b => b?.ToString() ?? "").Where(b => b.Length > 0).ToList(), null);
        if (bosses != null && bosses.Count > 0) parts.Add((bosses.Count == 1 ? "Boss: " : "Bosses: ") + string.Join(", ", bosses));
        var links = Try(() => area.Connections?.Select(c => Try(() => c.Name, "") ?? "").Where(c => c.Length > 0).Distinct().ToList(), null);
        if (links != null && links.Count > 0) parts.Add("It leads to " + JoinAnd(links));
        return string.Join(". ", parts);
    }

    private static string JoinAnd(List<string> items) =>
        items.Count == 1 ? items[0] : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[items.Count - 1];

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
        string size = walkable < 40000 ? "A small area" : walkable < 150000 ? "A medium-sized area" : walkable < 400000 ? "A large area" : "A very large area";
        double openShare = open * 4.0 / walkable;
        string openness = openShare >= 0.6 ? "mostly open ground" : openShare >= 0.35 ? "open ground and narrow paths" : "mostly narrow paths";
        var text = size + ", " + openness;
        if (!axes) return text;

        float w = maxX - minX, h = maxY - minY;
        if (w > 0 && h > 0)
        {
            if (w / h > 1.6f) text += ", stretching left and right";
            else if (h / w > 1.6f) text += ", stretching up and down";
        }
        // Where you are in it: the player is at (0, 0) of these coordinates.
        float fx = w > 0 ? -minX / w : 0.5f, fy = h > 0 ? -minY / h : 0.5f;   // 0 = left / bottom
        string horiz = fx < 0.25f ? "left" : fx > 0.75f ? "right" : "";
        string vert = fy < 0.25f ? "bottom" : fy > 0.75f ? "top" : "";
        string where = vert.Length > 0 && horiz.Length > 0 ? "near its " + vert + " " + horiz + " corner"
                     : vert.Length > 0 ? "near its " + vert + " edge"
                     : horiz.Length > 0 ? "near its " + horiz + " edge"
                     : "near its middle";
        return text + ". You are " + where;
    }
}
