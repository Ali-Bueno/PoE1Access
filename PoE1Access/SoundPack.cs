using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PoE1Access;

/// <summary>
/// Sound files of your own for loot, chests and the stash, used the way Path of Exile 2 Access
/// uses its "Diablo IV" set: a loop from each of the nearest items (gold, potion, normal, magic,
/// rare and legendary equipment, quest item) and a short sound repeating from gems, scrolls,
/// runes, chests, corpses, weapon racks and the stash.
///
/// No sounds of this kind ship with the mod: put your own in a folder named "sounds" beside
/// PoE1Access.exe; the mod finds the files by name anywhere inside it ("chest.ogg",
/// "legendary equipment.ogg"...). Without the folder the mod's own sounds play.
/// </summary>
internal sealed class SoundPack
{
    private readonly Dictionary<string, string> _files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public string Folder { get; }
    public bool Available => _files.Count > 0;

    // Long files loop (the equipment, gold and potion sounds run 14 seconds); short ones repeat.
    private static readonly HashSet<string> Loops = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "gold", "potion", "normal equipment", "magic equipment", "rare equipment", "legendary equipment", "quest item" };

    public SoundPack()
    {
        foreach (string dir in Candidates())
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (string f in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
                {
                    string ext = Path.GetExtension(f);
                    if (!ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".wav", StringComparison.OrdinalIgnoreCase)) continue;
                    string name = Path.GetFileNameWithoutExtension(f);
                    // The loot folder's files win over same-named ones elsewhere (the exploration
                    // folder has a portal and a shrine of its own).
                    if (!_files.ContainsKey(name) || f.IndexOf("loot", StringComparison.OrdinalIgnoreCase) >= 0) _files[name] = f;
                }
                if (_files.Count > 0) { Folder = dir; return; }
            }
            catch { }
        }
    }

    // "sounds" beside PoE1Access.exe (two folders up from ExileApi), in the app folder, or in
    // ExileApi's own folder.
    private static IEnumerable<string> Candidates()
    {
        string b = AppContext.BaseDirectory;
        yield return Path.GetFullPath(Path.Combine(b, "..", "..", "sounds"));
        yield return Path.GetFullPath(Path.Combine(b, "..", "sounds"));
        yield return Path.Combine(b, "sounds");
    }

    /// <summary>The file for a sound ("chest", "legendary equipment"), or null.</summary>
    public string File(string name) => name != null && _files.TryGetValue(name, out string f) ? f : null;

    public static bool IsLoop(string name) => name != null && Loops.Contains(name);

    public IEnumerable<string> Names => _files.Keys.OrderBy(k => k);

    /// <summary>The pack's sound for an item on the ground, by the mod's kind of loot.</summary>
    public static string LootSound(Synth.Loot kind) => kind switch
    {
        Synth.Loot.Currency => "gold",
        Synth.Loot.Scroll => "scroll",
        Synth.Loot.Gem => "gem",
        Synth.Loot.Flask => "potion",
        Synth.Loot.Quest => "quest item",
        Synth.Loot.Jewel => "rune",
        Synth.Loot.Normal => "normal equipment",
        Synth.Loot.Magic => "magic equipment",
        Synth.Loot.Rare => "rare equipment",
        Synth.Loot.Unique => "legendary equipment",
        _ => null,   // maps, divination cards and the rest keep the game's sound
    };

    /// <summary>The pack's sound for a chest: corpses and racks have their own.</summary>
    public static string ChestSound(string name, string path)
    {
        string s = (name ?? "") + " " + (path ?? "");
        if (s.IndexOf("Corpse", StringComparison.OrdinalIgnoreCase) >= 0 || s.IndexOf("Body", StringComparison.OrdinalIgnoreCase) >= 0) return "corpse";
        if (s.IndexOf("Rack", StringComparison.OrdinalIgnoreCase) >= 0) return "weapon rack";
        return "chest";
    }
}
