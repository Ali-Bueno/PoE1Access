using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using ExileCore.PoEMemory.MemoryObjects;
using static PoE1Access.Loc;

namespace PoE1Access;

// The endgame: map items, the map device, progress inside a map, and the Atlas.
//
// Written 26 Nov 2026 for the 3.30 Atlas (Covens of Umbra) BEFORE ANYONE HAS TESTED IT IN THE
// GAME. It reads what Exile API names for these (its Atlas node list, completed and bonus-completed
// maps, Shaped Regions, the map device's window, the monsters left), which may have moved with the
// league's patch; each part writes what it found to the log ("Atlas: ...", "Map device: ...") so
// the first maps show what to fix.
//
//   A map item: "Map, tier 14. Atlas Drops 72 percent, rarity 40 percent, pack size 25 percent.
//   Warning: monsters reflect elemental damage", before its full description. The warnings are
//   mods that end many builds (Path of Exile's usual list); "Warn about dangerous map mods" turns
//   them off.
//
//   The map device: what is in it, as it opens and as it changes: "Map device: Cemetery Map,
//   tier 14. 3 Breach Thread. 2 Ritual Thread".
//
//   In a map: the monsters left, as the game counts them, at 50, 20, 10, 5 and none.
//
//   The Atlas: with it open, the D-pad goes through lists of maps, as the passive tree finder
//   does: up and down choose a list (Not completed, Bonus objective left, Shaped Regions, Unique
//   maps), left and right go through it, nearest the Atlas's centre first, each with its base
//   tier, whether it is done and where it lies on the Atlas ("Cemetery, base tier 3, not
//   completed, Breach region, north east"). Moving on the Atlas itself is the game's.
public partial class PoE1AccessCore
{
    // ---- Map items ----

    private Dictionary<string, int> _mapTiers;

    private static bool IsMapItem(Entity item) =>
        (Try(() => item.Path, "") ?? "").StartsWith("Metadata/Items/Maps/", StringComparison.OrdinalIgnoreCase);

    private int MapTier(Entity item)
    {
        if (_mapTiers == null)
        {
            _mapTiers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var keys = Try(() => GameController.Files.MapKeys.EntriesList.ToList(), null);
            if (keys != null)
                foreach (var k in keys)
                {
                    string path = Try(() => k.ItemType?.Metadata, null);
                    if (!string.IsNullOrEmpty(path)) _mapTiers[path] = Try(() => (int)k.Tier, 0);
                }
            PluginLog.Write("Atlas: map tiers known for " + _mapTiers.Count + " maps");
        }
        string p = Try(() => item.Path, "") ?? "";
        return _mapTiers.TryGetValue(p, out int t) ? t : 0;
    }

    // Mods that end many builds, by words in the mod's text (the game's own wording).
    private static readonly (string Words, string En, string Es)[] DangerousMapMods =
    {
        ("reflect", "monsters reflect damage", "los monstruos reflejan daño"),
        ("cannot regenerate", "you cannot regenerate", "no puedes regenerar"),
        ("cannot leech", "no leech", "sin robo de vida"),
        ("cannot be leeched", "no leech", "sin robo de vida"),
        ("less recovery", "less life and mana recovery", "menos recuperación de vida y maná"),
        ("maximum resistances", "lower maximum resistances", "resistencias máximas más bajas"),
        ("maximum player resistances", "lower maximum resistances", "resistencias máximas más bajas"),
        ("players are cursed", "you are cursed", "estás maldito"),
        ("reduced flask", "fewer flask charges", "menos cargas de frasco"),
        ("flask charges", "fewer flask charges", "menos cargas de frasco"),
        ("burning ground", "burning ground", "suelo ardiente"),
        ("shocked ground", "shocked ground", "suelo electrizado"),
        ("desecrated ground", "desecrated ground", "suelo profanado"),
        ("chilled ground", "chilled ground", "suelo helado"),
        ("additional projectiles", "monsters fire extra projectiles", "los monstruos disparan proyectiles extra"),
        ("extra damage as", "monsters deal extra damage", "los monstruos hacen daño extra"),
        ("avoid ailments", "monsters avoid ailments", "los monstruos evitan alteraciones"),
    };

    /// <summary>"Map, tier 14. Atlas Drops 72 percent, ... Warning: ...", or "" when not a map.</summary>
    private string MapSummary(Entity item, IEnumerable<string> lines)
    {
        if (item == null || !IsMapItem(item)) return "";
        var all = lines.ToList();
        var parts = new List<string>();
        int tier = MapTier(item);
        parts.Add(tier > 0 ? T($"Map, tier {tier}", $"Mapa, nivel {tier}") : T("Map", "Mapa"));
        var values = new List<string>();
        string Value(string words)
        {
            foreach (string l in all)
                if (l.IndexOf(words, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var m = Regex.Match(l, @"(\d+)\s*%");
                    if (m.Success) return m.Groups[1].Value;
                }
            return null;
        }
        if (Value("Atlas Drops") is string ad) values.Add(T($"Atlas Drops {ad} percent", $"Botín del Atlas {ad} por ciento"));
        if (Value("Item Rarity") is string ir) values.Add(T($"rarity {ir} percent", $"rareza {ir} por ciento"));
        if (Value("Pack Size") is string ps) values.Add(T($"pack size {ps} percent", $"tamaño de grupo {ps} por ciento"));
        if (values.Count > 0) parts.Add(string.Join(", ", values));
        if (Settings.MapModWarnings.Value)
        {
            var warn = new List<string>();
            foreach (string l in all)
                foreach (var d in DangerousMapMods)
                    if (l.IndexOf(d.Words, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        string w = T(d.En, d.Es);
                        if (!warn.Contains(w)) warn.Add(w);
                    }
            if (warn.Count > 0) parts.Add(T("Warning: ", "Atención: ") + string.Join(", ", warn));
        }
        return string.Join(". ", parts);
    }

    // ---- The map device ----

    private string _deviceSig = "";
    private long _nextDevicePollMs;

    private void PollMapDevice(long now)
    {
        if (now < _nextDevicePollMs) return;
        _nextDevicePollMs = now + 300;
        var ui = Try(() => GameController.IngameState.IngameUi, null);
        var win = Try(() => ui?.MapDeviceWindow, null);
        if (win == null || !Try(() => win.IsVisible, false)) { _deviceSig = ""; return; }
        var items = Try(() => win.Items?.Select(i => Try(() => i.Item, null)).Where(i => i != null && i.Address != 0).ToList(), null) ?? new List<Entity>();
        var names = new List<string>();
        foreach (var it in items.Where(IsMapItem)) { int t = MapTier(it); names.Add(ItemTitle(it) + (t > 0 ? T($", tier {t}", $", nivel {t}") : "")); }
        // Threads and the rest, counted by name.
        foreach (var g in items.Where(i => !IsMapItem(i)).GroupBy(i => ItemTitle(i, false)))
        {
            int n = g.Sum(i => Math.Max(1, Try(() => GetComp<ExileCore.PoEMemory.Components.Stack>(i)?.Size ?? 1, 1)));
            names.Add(n + " " + g.Key);
        }
        string sig = string.Join("|", names);
        if (sig == _deviceSig) return;
        bool opened = _deviceSig.Length == 0;
        _deviceSig = sig.Length > 0 ? sig : "(empty)";
        PluginLog.Write("Map device: " + sig);
        string text = names.Count > 0 ? string.Join(". ", names) : T("empty", "vacío");
        Say(opened ? T($"Map device: {text}", $"Dispositivo de mapas: {text}") : text, !opened);
    }

    // ---- In a map: monsters left ----

    private int _monstersSaidAt = int.MaxValue, _monstersMost;
    private long _monstersArea;

    private void PollMonstersLeft()
    {
        string id = Try(() => GameController.Area.CurrentArea.Area.Id, "") ?? "";
        long area = Try(() => (long)GameController.Area.CurrentArea.Hash, 0L);
        if (area != _monstersArea) { _monstersArea = area; _monstersSaidAt = int.MaxValue; _monstersMost = 0; }
        if (!id.StartsWith("MapWorlds", StringComparison.OrdinalIgnoreCase)) return;
        int left = Try(() => (int)GameController.IngameState.ServerData.MonstersRemaining, -1);
        if (left < 0 || left > 250) return;
        // Said only as the count comes down past a mark it was above, so a count the game has not
        // filled in yet (0 at the start) says nothing.
        _monstersMost = Math.Max(_monstersMost, left);
        foreach (int mark in new[] { 50, 20, 10, 5, 0 })
        {
            if (left > mark || _monstersSaidAt <= mark || _monstersMost <= mark) continue;
            _monstersSaidAt = mark;
            PluginLog.Write("Monsters left: " + left);
            Say(left == 0 ? T("No monsters left", "No quedan monstruos") : T($"{left} monsters left", $"Quedan {left} monstruos"), false);
            return;
        }
    }

    // ---- The Atlas ----

    private int _atlasList, _atlasIndex = -1;
    private static readonly string[] AtlasListNames = { "Not completed", "Bonus objective left", "Shaped Regions", "Unique maps" };

    private bool AtlasOpen() => Try(() => GameController.IngameState.IngameUi.Atlas?.IsVisible ?? false, false);

    private string AtlasListName(int i) => i switch
    {
        0 => T("Not completed", "Sin completar"),
        1 => T("Bonus objective left", "Objetivo extra pendiente"),
        2 => T("Shaped Regions", "Regiones moldeadas"),
        _ => T("Unique maps", "Mapas únicos"),
    };

    private sealed class AtlasEntry { public AtlasNode Node; public string Region = ""; }

    private List<AtlasEntry> AtlasEntries(int list, out Vector2 centre)
    {
        centre = Vector2.Zero;
        var nodes = Try(() => GameController.Files.AtlasNodes.EntriesList.Where(n => n != null && Try(() => n.Area != null, false) && Try(() => n.Connections.Count > 0, false)).ToList(), null) ?? new List<AtlasNode>();
        if (nodes.Count == 0) return new List<AtlasEntry>();
        centre = new Vector2(nodes.Average(n => Try(() => n.PosX, 0f)), nodes.Average(n => Try(() => n.PosY, 0f)));
        var server = Try(() => GameController.IngameState.ServerData, null);
        var done = new HashSet<string>(Try(() => server.CompletedNodes.Select(n => n.Id).ToList(), new List<string>()));
        var bonus = new HashSet<string>(Try(() => server.BonusCompletedNodes.Select(n => n.Id).ToList(), new List<string>()));
        var regions = new Dictionary<string, string>();
        foreach (var r in Try(() => server.InfluencedRegions.ToList(), new List<AtlasInfluencedRegion>()))
        {
            string type = Try(() => r.Type?.Id, "") ?? "";
            foreach (var m in Try(() => r.Maps.ToList(), new List<AtlasInfluencedMap>()))
            {
                string id = Try(() => m.Node?.Id, null);
                if (id != null) regions[id] = type;
            }
        }
        IEnumerable<AtlasNode> pick = list switch
        {
            0 => nodes.Where(n => !done.Contains(n.Id)),
            1 => nodes.Where(n => done.Contains(n.Id) && !bonus.Contains(n.Id)),
            2 => nodes.Where(n => regions.ContainsKey(n.Id)),
            _ => nodes.Where(n => Try(() => n.IsUniqueMap, false)),
        };
        var c = centre;
        var entries = pick.Select(n => new AtlasEntry { Node = n, Region = regions.TryGetValue(n.Id, out string t) ? t : "" })
            .OrderBy(e => list == 2 ? e.Region : "").ThenBy(e => (new Vector2(e.Node.PosX, e.Node.PosY) - c).LengthSquared()).ToList();
        PluginLog.Write($"Atlas: {nodes.Count} maps, {done.Count} completed, {bonus.Count} bonus completed, {regions.Count} in Shaped Regions; list {AtlasListNames[list]} has {entries.Count}");
        return entries;
    }

    private void AtlasStep(int dpad)
    {
        if ((dpad & (ControllerInput.DPadUp | ControllerInput.DPadDown)) != 0)
        {
            int step = (dpad & ControllerInput.DPadDown) != 0 ? 1 : -1;
            _atlasList = (_atlasList + step + AtlasListNames.Length) % AtlasListNames.Length;
            _atlasIndex = 0;
        }
        else _atlasIndex += (dpad & ControllerInput.DPadLeft) != 0 ? -1 : 1;
        var entries = AtlasEntries(_atlasList, out Vector2 centre);
        string listName = AtlasListName(_atlasList);
        if (entries.Count == 0) { Say(T($"{listName}, none", $"{listName}, ninguno"), true); return; }
        bool edge = _atlasIndex < 0 || _atlasIndex >= entries.Count;
        _atlasIndex = Math.Max(0, Math.Min(entries.Count - 1, _atlasIndex));
        var server = Try(() => GameController.IngameState.ServerData, null);
        var e = entries[_atlasIndex];
        string name = Try(() => e.Node.Area.Name, "") ?? e.Node.Id;
        bool done = Try(() => server.CompletedNodes.Any(n => n.Id == e.Node.Id), false);
        bool bonus = Try(() => server.BonusCompletedNodes.Any(n => n.Id == e.Node.Id), false);
        string state = bonus ? T("completed with its bonus", "completado con su extra") : done ? T("completed, bonus objective left", "completado, objetivo extra pendiente") : T("not completed", "sin completar");
        var parts = new List<string> { name, T($"base tier {e.Node.BaseTier}", $"nivel base {e.Node.BaseTier}"), state };
        if (e.Region.Length > 0) parts.Add(T($"{e.Region} region", $"región de {e.Region}"));
        parts.Add(AtlasDirection(new Vector2(e.Node.PosX, e.Node.PosY) - centre));
        parts.Add(T($"{_atlasIndex + 1} of {entries.Count}", $"{_atlasIndex + 1} de {entries.Count}"));
        string text = string.Join(", ", parts) + (edge ? (_atlasIndex == 0 ? T(", first", ", primero") : T(", last", ", último")) : "");
        Say((dpad & (ControllerInput.DPadUp | ControllerInput.DPadDown)) != 0 ? listName + ". " + text : text, true);
    }

    // Where a map lies from the Atlas's centre: "north east", "near the centre". Screen y grows down.
    private static string AtlasDirection(Vector2 d)
    {
        if (d.Length() < 250f) return T("near the centre", "cerca del centro");
        double a = Math.Atan2(-d.Y, d.X) * 180 / Math.PI;
        string[] en = { "east", "north east", "north", "north west", "west", "south west", "south", "south east" };
        string[] es = { "este", "noreste", "norte", "noroeste", "oeste", "suroeste", "sur", "sureste" };
        int i = (int)Math.Round(((a + 360) % 360) / 45) % 8;
        return T(en[i], es[i]);
    }
}
