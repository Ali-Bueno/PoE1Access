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

    private static readonly string[] TrapWords = { "trap", "spike", "saw", "blade", "spinner", "roomba", "dart", "arrow", "flame", "fire", "furnace", "sentinel", "sentry", "pendulum" };
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
        if (p.Contains("font")) return LabKind.Font;
        if (p.Contains("door") || p.Contains("gate")) return LabKind.Door;
        if (TrapWords.Any(w => p.Contains(w))) return LabKind.Trap;
        return LabKind.Other;
    }

    /// <summary>A trap's kind in words, from its path: "Spike trap", "Sawblade", "Blade sentry"...</summary>
    public static string TrapName(string path)
    {
        string p = (path ?? "").ToLowerInvariant();
        if (p.Contains("spike")) return T("Spike trap", "Trampa de pinchos");
        if (p.Contains("sawblade") || p.Contains("saw")) return T("Sawblade", "Sierra");
        if (p.Contains("sentry") || p.Contains("sentinel")) return T("Blade sentry", "Centinela de cuchillas");
        if (p.Contains("spinner") || p.Contains("roomba")) return T("Spinning blades", "Cuchillas giratorias");
        if (p.Contains("pendulum")) return T("Swinging blade", "Cuchilla oscilante");
        if (p.Contains("blade")) return T("Blade trap", "Trampa de cuchillas");
        if (p.Contains("dart") || p.Contains("arrow")) return T("Dart trap", "Trampa de dardos");
        if (p.Contains("flame") || p.Contains("fire") || p.Contains("furnace")) return T("Fire trap", "Trampa de fuego");
        return T("Trap", "Trampa");
    }

    private static string LabName(Entity e, LabKind kind)
    {
        string r = Try(() => e.RenderName, "") ?? "";
        string path = Try(() => e.Path, "") ?? "";
        switch (kind)
        {
            case LabKind.Trap: return TrapName(path);
            case LabKind.Plaque: return r.Length > 0 ? T($"{r}, the trial's plaque", $"{r}, la placa de la prueba") : T("Trial plaque", "Placa de la prueba");
            case LabKind.Lever: return r.Length > 0 ? r : T("Lever", "Palanca");
            case LabKind.Key: return r.Length > 0 ? r : T("Key", "Llave");
            case LabKind.Darkshrine: return r.Length > 0 ? r : T("Darkshrine", "Santuario oscuro");
            case LabKind.Font: return r.Length > 0 ? r : T("Font", "Fuente");
            case LabKind.Door: return r.Length > 0 ? r : T("Door", "Puerta");
            default: return r;
        }
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
