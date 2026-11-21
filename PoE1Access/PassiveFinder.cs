using System;
using System.Collections.Generic;
using System.Linq;
using Vector2 = SharpDX.Vector2;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Elements;

namespace PoE1Access;

// The passive tree finder, after Path of Exile 2 Access's: with the tree open, D-pad up and down
// choose a list (Position, Keystones, Notables, Jewel sockets, Next to your nodes) and left
// and right walk it, the nodes costing fewest points first. The node named becomes the target:
//   "Keystones, 12. Resolute Technique, 8 points away, up left. Allocate first: Life, down"
// and every node the cursor reaches then ends with the way on, "Toward Resolute Technique: up
// left" (or, while it is not linked, toward the next node to allocate on the cheapest line to
// it), until "Arrived at Resolute Technique" and, once it is yours, "You have Resolute
// Technique. Guide off". Back at Position the target is dropped. Nothing is allocated for you.
//
// The cost is the number of points to spend: from the nodes that can be allocated now (1 each)
// along nodes you do not have, as for "N points away".
public partial class PoE1AccessCore
{
    private static readonly string[] FinderLists = { "Position", "Keystones", "Notables", "Jewel sockets", "Next to your nodes" };
    private int _finderList, _finderIndex = -1;
    private long _finderTarget;
    private Element _passiveHover;
    private long _passiveHoverRow;

    private sealed class FinderNode
    {
        public long Row;
        public string Name = "";
        public int Cost;          // 0 allocated, 1 can be allocated, more: points away
        public List<long> Path;   // the nodes to allocate on the way, nearest first (empty when allocated or allocatable)
    }

    /// <summary>A D-pad press in the passive tree.</summary>
    private void FinderStep(int dpad)
    {
        if (_passiveHover == null || _passiveHoverRow == 0) { Say("Move onto a passive node first", true); return; }
        if ((dpad & (ControllerInput.DPadUp | ControllerInput.DPadDown)) != 0)
        {
            int step = (dpad & ControllerInput.DPadDown) != 0 ? 1 : -1;
            _finderList = (_finderList + step + FinderLists.Length) % FinderLists.Length;
            _finderIndex = -1;
            if (_finderList == 0) { SayPosition(); return; }
            FinderMove(1, true);
            return;
        }
        if (_finderList == 0) { SayPosition(); return; }
        FinderMove((dpad & ControllerInput.DPadLeft) != 0 ? -1 : 1, false);
    }

    private void SayPosition()
    {
        bool had = _finderTarget != 0;
        _finderTarget = 0;
        string here = _passiveNameOfRow.TryGetValue(_passiveHoverRow, out string n) && n.Length > 0 ? n : "this node";
        Say("Position: " + here + (had ? ". Target dropped" : ""), true);
    }

    private void FinderMove(int step, bool announceList)
    {
        var nodes = FinderNodes(_finderList);
        string list = FinderLists[_finderList];
        if (nodes.Count == 0) { Say(list + ": none on this tree", true); return; }
        _finderIndex = _finderIndex < 0 ? 0 : (_finderIndex + step + nodes.Count) % nodes.Count;
        var node = nodes[_finderIndex];
        _finderTarget = node.Row;

        var parts = new List<string>();
        if (announceList) parts.Add(list + ", " + nodes.Count);
        string cost = node.Cost == 0 ? "allocated" : node.Cost == 1 ? (CanAllocate(node.Row) ? "can be allocated" : "1 point away") : node.Cost + " points away";
        parts.Add(node.Name + ", " + cost + ", " + (_finderIndex + 1) + " of " + nodes.Count);
        string way = WayTo(node.Row);
        if (way.Length > 0) parts.Add(way);
        if (node.Path != null && node.Path.Count > 0)
            parts.Add("Allocate first: " + NodeName(node.Path[0]) + WayDirection(node.Path[0]));
        // What the node does, last, so the way is heard first.
        parts.AddRange(PassiveStatLines(node.Row));
        // The node under the cursor, whose reading now ends with the new target's way, is not
        // said again over this until the cursor moves.
        _finderQuietRow = _passiveHoverRow;
        Say(JoinSpeech(parts), true);
    }

    /// <summary>What the guide adds to a node's reading: the way to the target or to the next
    /// node to allocate for it. Empty without a target.</summary>
    private string FinderGuide(long row)
    {
        if (_finderTarget == 0) return "";
        var allocated = AllocatedPassives();
        string target = NodeName(_finderTarget);
        if (_passiveIdOfRow.TryGetValue(_finderTarget, out ushort tid) && allocated.Contains(tid))
        {
            _finderTarget = 0;
            return "You have " + target + ". Guide off";
        }
        if (row == _finderTarget)
        {
            var path = PathTo(_finderTarget);
            if (path == null || path.Count == 0) return "Arrived at " + target + (CanAllocate(row) ? ", allocate this one" : "");
            return "Arrived at " + target + ", " + (path.Count + 1) + " points to go. Allocate first: " + NodeName(path[0]) + WayDirection(path[0]);
        }
        var steps = PathTo(_finderTarget);
        if (steps != null && steps.Count > 0)
        {
            if (steps[0] == row) return "Allocate this one for " + target + ", " + (steps.Count + 1) + " points to go";
            if (steps.Contains(row)) return "On the way to " + target + ". Next to allocate: " + NodeName(steps[0]) + WayDirection(steps[0]);
            return target + ", " + (steps.Count + 1) + " points to go. Next to allocate: " + NodeName(steps[0]) + WayDirection(steps[0]);
        }
        return "Toward " + target + WayDirection(_finderTarget);
    }

    private long _finderQuietRow;

    // A node's stats in words ("20% increased mana regeneration rate"), from its PassiveSkills
    // row, as a node is read when the game's tooltip cannot be found.
    private List<string> PassiveStatLines(long row)
    {
        var lines = new List<string>();
        var skill = Try(() => GameController.Files.PassiveSkills.EntriesList.FirstOrDefault(p => p.Address == row), null);
        if (skill == null) return lines;
        foreach (var (stat, value) in Try(() => skill.Stats.ToList(), new List<(ExileCore.PoEMemory.FilesInMemory.StatsDat.StatRecord, int)>()))
        {
            string text = StatWords(stat?.Key, value);
            if (text.Length > 0) lines.Add(text);
        }
        return lines;
    }

    private string NodeName(long row) => _passiveNameOfRow.TryGetValue(row, out string n) && n.Length > 0 ? n : "unnamed node";

    // ": up left", from the node under the cursor; "" without screen positions.
    private string WayDirection(long row)
    {
        var elements = _treeElements;
        if (elements == null || _passiveHover == null || !elements.TryGetValue(row, out Element el)) return "";
        Vector2 here = Centre(_passiveHover), there = Centre(el);
        if (here == Vector2.Zero || there == Vector2.Zero) return "";
        if (row == _passiveHoverRow) return ": here";
        return ": " + Directions[DirectionIndex(here, there)];
    }

    private string WayTo(long row)
    {
        string d = WayDirection(row);
        return d.Length > 0 ? "From here" + d : "";
    }

    private HashSet<ushort> AllocatedPassives() =>
        new HashSet<ushort>(Try(() => GameController.IngameState.ServerData.PassiveSkillIds.Select(i => (ushort)i).ToList(), new List<ushort>()));

    private bool CanAllocate(long row) =>
        _treeElements != null && _treeElements.TryGetValue(row, out Element el) && Try(() => el.AsObject<TreePassiveElement>().CanAllocate, false);

    // The nodes of a list, fewest points first (allocated ones last), nodes with no way to them
    // (another class's ascendancy) left out.
    private List<FinderNode> FinderNodes(int list)
    {
        var result = new List<FinderNode>();
        var elements = _passiveHover != null ? TreeElements(_passiveHover) : null;
        var links = PassiveLinks(Environment.TickCount64);
        if (elements == null || links == null) return result;
        LearnPassiveRows();
        var costs = PassiveCosts(elements, links, out var parents);
        var mem = Mem;
        foreach (var kv in elements)
        {
            long row = kv.Key;
            if (!costs.TryGetValue(row, out int cost)) continue;
            bool keep = list switch
            {
                1 => Try(() => mem.Read<byte>(row + 0x4A), (byte)0) == 1,
                2 => Try(() => mem.Read<byte>(row + 0x4B), (byte)0) == 1 && Try(() => mem.Read<byte>(row + 0x4A), (byte)0) != 1,
                3 => _passiveIdTextOfRow.TryGetValue(row, out string pid) && pid.StartsWith("jewel_slot", StringComparison.OrdinalIgnoreCase),
                4 => cost == 1,
                _ => false,
            };
            if (!keep) continue;
            result.Add(new FinderNode { Row = row, Name = NodeName(row), Cost = cost, Path = PathFromParents(row, parents) });
        }
        // Fewest points first; those you have at the end; then by name, so the order holds.
        return result.OrderBy(n => n.Cost == 0 ? int.MaxValue : n.Cost).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The nodes to allocate before the target, nearest first; null without a way.</summary>
    private List<long> PathTo(long target)
    {
        var elements = _treeElements;
        var links = PassiveLinks(Environment.TickCount64);
        if (elements == null || links == null) return null;
        var costs = PassiveCosts(elements, links, out var parents);
        if (!costs.ContainsKey(target)) return null;
        return PathFromParents(target, parents);
    }

    private static List<long> PathFromParents(long row, Dictionary<long, long> parents)
    {
        var path = new List<long>();
        long cur = row;
        while (parents.TryGetValue(cur, out long p) && p != 0) { path.Add(p); cur = p; }
        path.Reverse();   // the first to allocate first
        return path;
    }

    // Points to each node: 0 for those you have, 1 for those linked to them (or that the game
    // says can be allocated, such as your class's start before any point is spent), then one
    // more per node along the links through nodes you do not have. Counted from your nodes, not
    // from the game's "can be allocated", which is false everywhere while you have no point to
    // spend (seen 21 Nov 2026). Parents give the line taken (0: next to your nodes).
    private Dictionary<long, int> PassiveCosts(Dictionary<long, Element> elements, Dictionary<long, List<long>> links, out Dictionary<long, long> parents)
    {
        var allocated = AllocatedPassives();
        var cost = new Dictionary<long, int>();
        parents = new Dictionary<long, long>();
        var queue = new Queue<long>();
        foreach (var kv in elements)
            if (_passiveIdOfRow.TryGetValue(kv.Key, out ushort pid) && allocated.Contains(pid)) { cost[kv.Key] = 0; queue.Enqueue(kv.Key); }
        foreach (var kv in elements)
            if (!cost.ContainsKey(kv.Key) && Try(() => kv.Value.AsObject<TreePassiveElement>().CanAllocate, false))
            { cost[kv.Key] = 1; parents[kv.Key] = 0; queue.Enqueue(kv.Key); }
        while (queue.Count > 0)
        {
            long cur = queue.Dequeue();
            if (!links.TryGetValue(cur, out var next)) continue;
            foreach (long nx in next)
            {
                if (cost.ContainsKey(nx) || !elements.ContainsKey(nx)) continue;
                cost[nx] = cost[cur] + 1;
                parents[nx] = cost[cur] == 0 ? 0 : cur;
                queue.Enqueue(nx);
            }
        }
        return cost;
    }
}
