using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Elements;
using ExileCore.Shared.Enums;
using SharpDX;

namespace PoE1Access;

// Passive tree links, read the Poe2Access way: each node names the nodes linked to it, with
// the direction each lies on screen, clockwise from up, and their state ("allocated",
// "can be allocated", "not linked"); a node not linked yet says how many points away it is.
//
// The links come from the game's own passive skill graph, loaded in memory from
// Metadata/PassiveSkillGraph.psg (found 18 Nov 2026; ExileApi does not read it):
//   file record +0x28 -> graph
//   graph +0x20: vector of 24-byte entries { PassiveSkills.dat row, file, node }
//   node +0x40 and +0x58: vectors of pointers to links; a link is { node A, node B, ... }
public partial class PoE1AccessCore
{
    private Dictionary<long, List<long>> _passiveLinks;   // PassiveSkills row -> linked rows
    private long _passiveGraphTriedMs = -60000;

    // Tree elements by row, found once per tree (the tree's elements stay while the game runs).
    private Dictionary<long, Element> _treeElements;
    private long _treeElementsRoot;

    private Dictionary<long, List<long>> PassiveLinks(long now)
    {
        if (_passiveLinks != null || now - _passiveGraphTriedMs < 30000) return _passiveLinks;
        _passiveGraphTriedMs = now;
        try
        {
            var mem = Mem;
            long rec = GameController.Files.FindFile("Metadata/PassiveSkillGraph.psg");
            if (rec == 0) return null;
            long graph = mem.Read<long>(rec + 0x28);
            long begin = mem.Read<long>(graph + 0x20), end = mem.Read<long>(graph + 0x28);
            long count = (end - begin) / 24;
            if (graph == 0 || count <= 0 || count > 50000) return null;

            var rowOfNode = new Dictionary<long, long>();
            var nodes = new List<long>();
            for (long k = 0; k < count; k++)
            {
                long row = mem.Read<long>(begin + k * 24), node = mem.Read<long>(begin + k * 24 + 16);
                if (row == 0 || node == 0) continue;
                rowOfNode[node] = row;
                nodes.Add(node);
            }

            var links = new Dictionary<long, List<long>>();
            foreach (long node in nodes)
            {
                long row = rowOfNode[node];
                var list = new List<long>();
                foreach (int vec in new[] { 0x40, 0x58 })
                {
                    long b = mem.Read<long>(node + vec), e = mem.Read<long>(node + vec + 8);
                    if (e <= b || e - b > 8 * 64) continue;
                    for (long p = b; p < e; p += 8)
                    {
                        long link = mem.Read<long>(p);
                        if (link == 0) continue;
                        long a = mem.Read<long>(link), c = mem.Read<long>(link + 8);
                        long other = a == node ? c : c == node ? a : 0;
                        if (other != 0 && rowOfNode.TryGetValue(other, out long otherRow) && !list.Contains(otherRow))
                            list.Add(otherRow);
                    }
                }
                links[row] = list;
            }
            _passiveLinks = links;
            PluginLog.Write("Passive graph: " + links.Count + " nodes, " + links.Values.Sum(l => l.Count) / 2 + " links");
        }
        catch (Exception ex)
        {
            PluginLog.Write("Passive graph not read: " + ex.Message);
        }
        return _passiveLinks;
    }

    // Every passive element of the tree the hovered node belongs to, by PassiveSkills row.
    private Dictionary<long, Element> TreeElements(Element hover)
    {
        // The tree root: the highest ancestor (up to 8 levels) still below the "Passives" panel.
        Element cur = hover, top = hover;
        for (int up = 0; up < 8; up++)
        {
            Element parent = UiText.ParentOf(Mem, cur);
            if (parent == null || parent.Address == 0) break;
            if (IsGameWord(UiText.Title(Mem, parent), PassivesTitle)) { top = cur; break; }
            top = parent;
            cur = parent;
        }
        if (_treeElements != null && _treeElementsRoot == top.Address) return _treeElements;

        var map = new Dictionary<long, Element>();
        var queue = new Queue<(Element E, int D)>();
        queue.Enqueue((top, 0));
        int visited = 0;
        while (queue.Count > 0 && visited < 30000)
        {
            var (e, d) = queue.Dequeue();
            visited++;
            if (Try(() => UiText.TypeOf(Mem, e) == ElementType.PassivePoint, false))
            {
                long row = Try(() => e.AsObject<TreePassiveElement>().PassiveSkill?.Address ?? 0, 0L);
                if (row != 0) map[row] = e;
                continue;
            }
            if (d >= 10) continue;
            var kids = Try<IList<Element>>(() => e.Children, null);
            if (kids == null || kids.Count > 5000) continue;
            foreach (Element k in kids) queue.Enqueue((k, d + 1));
        }
        _treeElements = map;
        _treeElementsRoot = top.Address;
        PluginLog.Write("Passive tree elements: " + map.Count);
        return map;
    }

    private static readonly string[] Directions = { "up", "up right", "right", "down right", "down", "down left", "left", "up left" };

    // 0 = up, clockwise in eighths; screen y grows downward.
    private static int DirectionIndex(Vector2 from, Vector2 to)
    {
        double ang = Math.Atan2(to.X - from.X, -(to.Y - from.Y));   // 0 = up, clockwise
        int i = (int)Math.Round(ang / (Math.PI / 4));
        return ((i % 8) + 8) % 8;
    }

    private static Vector2 Centre(Element e)
    {
        try { var r = e.GetClientRect(); return new Vector2(r.X + r.Width / 2, r.Y + r.Height / 2); }
        catch { return Vector2.Zero; }
    }

    /// <summary>"allocated", "can be allocated" or "not linked" for a node on the tree.</summary>
    private string PassiveState(long row, HashSet<ushort> allocated, Dictionary<long, Element> elements)
    {
        if (_passiveIdOfRow.TryGetValue(row, out ushort pid) && allocated.Contains(pid)) return "allocated";
        if (elements.TryGetValue(row, out Element el) && Try(() => el.AsObject<TreePassiveElement>().CanAllocate, false))
            return "can be allocated";
        // Next to a node you have, with no point to spend now: the game says it cannot be
        // allocated, but it is linked.
        if (_passiveLinks != null && _passiveLinks.TryGetValue(row, out var near) &&
            near.Any(o => _passiveIdOfRow.TryGetValue(o, out ushort op) && allocated.Contains(op)))
            return "linked";
        return "not linked";
    }

    private readonly Dictionary<long, ushort> _passiveIdOfRow = new Dictionary<long, ushort>();
    private readonly Dictionary<long, string> _passiveNameOfRow = new Dictionary<long, string>();
    private readonly Dictionary<long, string> _passiveIdTextOfRow = new Dictionary<long, string>();

    private void LearnPassiveRows()
    {
        if (_passiveNameOfRow.Count > 0) return;
        try
        {
            foreach (var p in GameController.Files.PassiveSkills.EntriesList)
            {
                _passiveIdOfRow[p.Address] = (ushort)p.PassiveId;
                _passiveNameOfRow[p.Address] = CleanText(p.Name ?? "");
                _passiveIdTextOfRow[p.Address] = p.Id ?? "";
            }
        }
        catch { }
    }

    /// <summary>
    /// The links of the hovered node ("Links: up, Life, allocated; down left, Strength, not linked")
    /// and, for a node not linked yet, how many points away it is ("3 points away").
    /// </summary>
    private (string Distance, string Links) DescribePassiveLinks(Element hover, long row)
    {
        long now = Environment.TickCount64;
        var links = PassiveLinks(now);
        if (links == null || !links.TryGetValue(row, out var mine)) return ("", "");
        LearnPassiveRows();
        var elements = TreeElements(hover);
        var allocated = new HashSet<ushort>(Try(() => GameController.IngameState.ServerData.PassiveSkillIds.Select(i => (ushort)i).ToList(), new List<ushort>()));

        Vector2 here = Centre(hover);
        var parts = new List<(int Dir, string Text)>();
        foreach (long other in mine)
        {
            string name = _passiveNameOfRow.TryGetValue(other, out string n) && n.Length > 0 ? n : "unnamed node";
            if (!elements.TryGetValue(other, out Element el)) continue;   // not on this tree (another tree's node)
            Vector2 there = Centre(el);
            // No usable screen positions (ExileApi misreads them after a game patch): the link
            // is named without a direction rather than with a wrong one.
            if (here == Vector2.Zero || there == Vector2.Zero || there == here)
            {
                parts.Add((8, name + ", " + PassiveState(other, allocated, elements)));
                continue;
            }
            int dir = DirectionIndex(here, there);
            parts.Add((dir, Directions[dir] + ", " + name + ", " + PassiveState(other, allocated, elements)));
        }
        string linkText = parts.Count == 0 ? "" : "Links: " + string.Join("; ", parts.OrderBy(p => p.Dir).Select(p => p.Text));

        // Points away, counted from your nodes along the links (PassiveCosts).
        string distance = "";
        string state = PassiveState(row, allocated, elements);
        if (state == "not linked" && PassiveCosts(elements, links, out _).TryGetValue(row, out int d) && d > 0)
            distance = d + (d == 1 ? " point away" : " points away");
        return (distance, linkText);
    }
}
