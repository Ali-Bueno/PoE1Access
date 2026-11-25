using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using static PoE1Access.Loc;

namespace PoE1Access;

// Trials of Ascendancy and the Lord's Labyrinth: traps, the trial's plaque, levers, keys,
// Darkshrines and the like, for the scanner and the object sounds.
//
// What the traps do and how the chambers are named is from the Path of Exile Wiki ("The Lord's
// Labyrinth", last edited 19 Nov 2026).
//
// NOT YET SEEN IN THE GAME (written 25 Nov 2026, before reaching a trial). The game's own names
// for these objects were not known, so they are matched by words in their paths ("Labyrinth"
// with "Spike", "Saw", "Blade", "Dart", "Flame", "Plaque", "Lever"...). Every object in a trial or
// the Labyrinth is written to the log once ("Labyrinth object: <path> ..."), so the first test
// there shows the real names, and this can be made exact.
//
// Traps get a scanner kind of their own (Traps) and a quick metallic ticking from the nearest
// ones, faster as you get closer; "Name things you walk up to" names each as you come near.
internal sealed partial class Navigator
{
    public enum LabKind { None, Trap, Plaque, Lever, Key, Darkshrine, Font, Door, Other }

    private static readonly string[] TrapWords = { "trap", "spike", "saw", "blade", "spinner", "spinning", "roomba", "dart", "arrow", "flame", "fire", "furnace", "sentinel", "sentry", "pendulum", "cleaver", "pressureplate", "pressure_plate" };
    private static readonly Dictionary<string, LabKind> LabKindCache = new Dictionary<string, LabKind>();

    /// <summary>What a Labyrinth or trial object is, from its path (cached per path).</summary>
    public static LabKind LabObject(Entity e)
    {
        string path = Try(() => e.Path, "") ?? "";
        if (path.Length == 0) return LabKind.None;
        lock (LabKindCache)
        {
            if (LabKindCache.TryGetValue(path, out LabKind k)) return k;
            k = LabKindOf(path);
            if (LabKindCache.Count < 4000) LabKindCache[path] = k;
            return k;
        }
    }

    private static LabKind LabKindOf(string path)
    {
        string p = path.ToLowerInvariant();
        bool lab = p.Contains("labyrinth") || p.Contains("/trial") || p.Contains("izaro");
        if (!lab) return LabKind.None;
        if (p.Contains("/monsters/") && !p.Contains("trap")) return LabKind.None;   // Izaro and the lab's monsters stay enemies
        if (p.Contains("plaque")) return LabKind.Plaque;
        if (p.Contains("darkshrine")) return LabKind.Darkshrine;
        if (p.Contains("lever") || p.Contains("switch")) return LabKind.Lever;
        if (p.Contains("key")) return LabKind.Key;
        if (p.Contains("font") || p.Contains("altar")) return LabKind.Font;   // the Divine Font, the Altar of Ascendancy
        if (p.Contains("door") || p.Contains("gate")) return LabKind.Door;
        if (TrapWords.Any(w => p.Contains(w))) return LabKind.Trap;
        return LabKind.Other;
    }

    /// <summary>A trap's kind in words, from its path: "Spike trap", "Sawblade", "Blade sentry"...</summary>
    public static string TrapName(string path)
    {
        string p = (path ?? "").ToLowerInvariant();
        if (p.Contains("pressureplate") || p.Contains("pressure_plate")) return T("Pressure plate, sets off a trap", "Placa de presión, activa una trampa");
        if (p.Contains("cleaver")) return T("Cleaver trap", "Trampa de cuchilla");
        if (p.Contains("spike")) return T("Spike trap", "Trampa de pinchos");
        if (p.Contains("sawblade") || p.Contains("saw")) return T("Sawblade", "Sierra");
        // Sentinel traps (the map trials and the Eternal Labyrinth) are switched off for a while by damaging them.
        if (p.Contains("sentinel")) return T("Sentinel trap, damage it to switch it off", "Trampa centinela, dáñala para apagarla");
        if (p.Contains("sentry")) return T("Blade sentry", "Centinela de cuchillas");
        if (p.Contains("spinner") || p.Contains("spinning") || p.Contains("roomba")) return T("Spinning blades", "Cuchillas giratorias");
        if (p.Contains("pendulum")) return T("Swinging blade", "Cuchilla oscilante");
        if (p.Contains("blade")) return T("Blade trap", "Trampa de cuchillas");
        if (p.Contains("dart") || p.Contains("arrow")) return T("Dart trap", "Trampa de dardos");
        if (p.Contains("furnace")) return T("Furnace trap", "Trampa de horno");
        if (p.Contains("flame") || p.Contains("fire")) return T("Fire trap", "Trampa de fuego");
        return T("Trap", "Trampa");
    }

    private static string LabName(Entity e, LabKind kind)
    {
        string r = Try(() => e.RenderName, "") ?? "";
        string path = Try(() => e.Path, "") ?? "";
        switch (kind)
        {
            // A Sentinel trap by its own name when it has one ("Unending Frost").
            case LabKind.Trap:
                return path.IndexOf("sentinel", StringComparison.OrdinalIgnoreCase) >= 0 && r.Length > 0
                    ? T($"{r}, sentinel trap, damage it to switch it off", $"{r}, trampa centinela, dáñala para apagarla")
                    : TrapName(path);
            case LabKind.Plaque: return r.Length > 0 ? T($"{r}, the trial's plaque", $"{r}, la placa de la prueba") : T("Trial plaque", "Placa de la prueba");
            case LabKind.Lever: return r.Length > 0 ? r : T("Lever", "Palanca");
            case LabKind.Key: return r.Length > 0 ? r : T("Key", "Llave");
            case LabKind.Darkshrine: return r.Length > 0 ? r : T("Darkshrine", "Santuario oscuro");
            case LabKind.Font: return r.Length > 0 ? r : T("Font", "Fuente");
            case LabKind.Door: return r.Length > 0 ? r : T("Door", "Puerta");
            default: return r;
        }
    }

    // ---- The Labyrinth's chambers ----
    //
    // A chamber's name says its look (Estate, Domain, Basilica, Sepulchre, Sanitorium, Mansion)
    // and how many ways it has: Annex 1 (a dead end), Passage 2, Halls 3, Atrium 4. Estate and
    // Domain chambers have names of their own (Estate Enclosure, Path, Walkways, Crossing), whose
    // counts are not known. The fights with Izaro are in the Aspirant's Trials.

    private static readonly string[] ChamberLooks = { "Estate", "Domain", "Basilica", "Sepulchre", "Sanitorium", "Mansion" };

    public static bool IsLabChamber(string name)
    {
        string n = (name ?? "").Trim();
        if (n.StartsWith("Aspirant's Trial", StringComparison.OrdinalIgnoreCase)) return true;
        return ChamberLooks.Any(l => n.StartsWith(l + " ", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>What a chamber's name means ("a dead end", "three ways"), or "".</summary>
    public static string ChamberMeaning(string name)
    {
        string n = (name ?? "").Trim();
        if (!IsLabChamber(n)) return "";
        if (n.StartsWith("Aspirant's Trial", StringComparison.OrdinalIgnoreCase)) return T("a fight with Izaro", "un combate contra Izaro");
        if (n.EndsWith(" Annex", StringComparison.OrdinalIgnoreCase)) return T("a dead end", "un callejón sin salida");
        if (n.EndsWith(" Passage", StringComparison.OrdinalIgnoreCase)) return T("two ways", "dos caminos");
        if (n.EndsWith(" Halls", StringComparison.OrdinalIgnoreCase)) return T("three ways", "tres caminos");
        if (n.EndsWith(" Atrium", StringComparison.OrdinalIgnoreCase)) return T("four ways", "cuatro caminos");
        return "";
    }

    // ---- Recording what a trial holds, for the first test there ----

    private readonly HashSet<string> _labLogged = new HashSet<string>();
    private long _labLogArea, _nextLabLogMs;

    /// <summary>Whether an object of a kind is within `cells` of you.</summary>
    public bool NearKind(ExileCore.Shared.Enums.EntityType type, float cells)
    {
        var p = PlayerGrid();
        foreach (Entity e in Entities())
            if (Try(() => e.Type, ExileCore.Shared.Enums.EntityType.Error) == type && Try(() => (e.GridPosNum - p).Length(), 1e9f) <= cells) return true;
        return false;
    }

    /// <summary>True once a trap or a trial's plaque has been seen in this area.</summary>
    public bool TrialHere { get; private set; }

    private void TickLabyrinth(long now)
    {
        if (now < _nextLabLogMs) return;
        _nextLabLogMs = now + 1000;
        long area = Try(() => (long)_gc.Area.CurrentArea.Hash, 0L);
        if (area != _labLogArea) { _labLogArea = area; _labLogged.Clear(); TrialHere = false; }
        foreach (Entity e in Entities())
        {
            LabKind kind = LabObject(e);
            if (kind == LabKind.None) continue;
            if (kind == LabKind.Trap || kind == LabKind.Plaque) TrialHere = true;
            string path = Try(() => e.Path, "") ?? "";
            if (_labLogged.Count > 300 || !_labLogged.Add(path)) continue;
            string states = "";
            try
            {
                var sm = e.GetComponent<StateMachine>();
                if (sm != null && sm.Address != 0) states = string.Join(",", sm.States.Select(s => s.Name + "=" + s.Value));
            }
            catch { }
            PluginLog.Write("Labyrinth object: " + path + " kind=" + kind + " type=" + Try(() => e.Type.ToString(), "?") +
                            " name=" + Try(() => e.RenderName, "") + " targetable=" + Try(() => e.IsTargetable, false) +
                            (states.Length > 0 ? " states=" + states : ""));
        }
    }
}
