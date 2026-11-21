using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ExileCore.PoEMemory.MemoryObjects;

namespace PoE1Access;

// A magic item's name as the game builds it in the client's language.
//
// In languages with grammatical gender an affix's name holds one form per gender and number:
// "<if:MS>{sano}<elif:FS>{sana}<elif:MP>{sanos}<elif:FP>{sanas}" (Spanish Mods.Name). The game
// takes the form of the base item's inflection, a column of BaseItemTypes (+160 of its
// 373-byte row: "M", "F", "MP", "FP"; a bare M or F is singular; empty in English), and puts the
// words in the order of ClientStrings MagicNamePrefix, MagicNameSuffix and
// MagicNamePrefixSuffix ({0} the base, {1} the prefix, {2} the suffix: "{1} {0} {2}" in English,
// "{0} {1} {2}" in Spanish, "Almilla raída sana del oso"). Layout checked 21 Nov 2026 on the
// Spanish client's data files.
public partial class PoE1AccessCore
{
    private const int BaseItemTypesRow = 373, BaseItemInflection = 160;
    private static readonly Regex FormRegex = new Regex(@"<(?:if|elif):([A-Za-z]+)>\{([^{}]*)\}|<else>\{([^{}]*)\}", RegexOptions.Compiled);

    private Dictionary<string, string> _inflections;   // base item Id -> "MS", "FS", "MP", "FP"
    private int _inflectionTries;
    private long _nextInflectionTryMs;

    /// <summary>The form of a gendered text that fits the inflection ("FS"): "sana" from
    /// "&lt;if:MS&gt;{sano}&lt;elif:FS&gt;{sana}…". Text without forms comes back as it is.</summary>
    private static string PickForm(string raw, string inflection)
    {
        if (string.IsNullOrEmpty(raw) || raw.IndexOf("<if:", StringComparison.OrdinalIgnoreCase) < 0) return raw ?? "";
        string first = null, fallback = null;
        foreach (Match m in FormRegex.Matches(raw))
        {
            if (m.Groups[3].Success) { fallback = m.Groups[3].Value; continue; }
            first ??= m.Groups[2].Value;
            if (inflection != null && m.Groups[1].Value.Equals(inflection, StringComparison.OrdinalIgnoreCase)) return m.Groups[2].Value.Trim();
        }
        return (fallback ?? first ?? raw).Trim();
    }

    /// <summary>The base item's inflection ("MS", "FS", "MP", "FP"), or null when it has none.</summary>
    private string Inflection(Entity item)
    {
        if (EnglishClient) return null;
        var table = Inflections();
        string path = Try(() => item.Path, "") ?? "";
        if (table == null || !table.TryGetValue(path, out string inf)) return null;
        return inf switch { "M" => "MS", "F" => "FS", _ => inf };
    }

    private Dictionary<string, string> Inflections()
    {
        if (_inflections != null || _inflectionTries >= 10) return _inflections;
        long now = Environment.TickCount64;
        if (now < _nextInflectionTryMs) return null;
        _nextInflectionTryMs = now + 10000;   // the game's files load with the game
        _inflectionTries++;
        _inflections = Try(ReadInflections, null);
        return _inflections;
    }

    private Dictionary<string, string> ReadInflections()
    {
        var all = GameController.Files.AllFiles;
        var keys = all.Keys.Where(k => k.EndsWith("Data/BaseItemTypes.dat", StringComparison.OrdinalIgnoreCase) ||
                                       k.EndsWith("/BaseItemTypes.datc64", StringComparison.OrdinalIgnoreCase)).ToList();
        // The client's own table holds the inflections; the English one has none. Which key is
        // the client's varies ("es:Data/...", or plain "Data/BaseItemTypes.dat" beside ExileApi's
        // "en:" copy), so each is tried, the likeliest first.
        var ordered = keys.OrderBy(k => k.StartsWith(GameLanguage + ":", StringComparison.OrdinalIgnoreCase) ? 0
                                      : k.IndexOf("/" + GameLanguageFolder() + "/", StringComparison.OrdinalIgnoreCase) >= 0 ? 1
                                      : k.StartsWith("en:", StringComparison.OrdinalIgnoreCase) ? 3 : 2).ToList();
        foreach (string key in ordered)
        {
            var map = Try(() => ReadInflections(all[key].Ptr, key), null);
            if (map != null) return map;
        }
        PluginLog.Write("Item inflections: none in " + string.Join(", ", keys));
        return null;
    }

    private Dictionary<string, string> ReadInflections(long fileAddr, string key)
    {
        var file = new DatFile(Mem, fileAddr);
        if (file.Count <= 0 || file.Length != BaseItemTypesRow) { PluginLog.Write("Item inflections: " + key + " has " + file.Count + " rows of " + file.Length); return null; }
        var mem = Mem;
        byte[] block = mem.ReadBytes(file.First, (int)(file.Count * file.Length));
        string Str(long p, int max)
        {
            if (p < 0x10000 || p > 0x7FFF_FFFF_FFFF) return "";
            string s = mem.ReadStringU(p, max) ?? "";
            return s.All(ch => ch >= 0x20 && ch < 0x7F) ? s : "";
        }
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < file.Count && (i + 1) * file.Length <= block.Length; i++)
        {
            int at = (int)(i * file.Length);
            string inf = Str(BitConverter.ToInt64(block, at + BaseItemInflection), 8);
            if (inf != "M" && inf != "F" && inf != "MP" && inf != "FP") continue;
            string id = Str(BitConverter.ToInt64(block, at), 300);
            if (id.StartsWith("Metadata/", StringComparison.Ordinal)) map[id] = inf;
        }
        PluginLog.Write("Item inflections from " + key + ": " + map.Count + " base items");
        return map.Count > 0 ? map : null;
    }

    private string GameLanguageFolder() => GameLanguage switch
    {
        "es" => "Spanish", "pt" => "Portuguese", "fr" => "French", "de" => "German", "ru" => "Russian",
        "th" => "Thai", "ja" => "Japanese", "ko" => "Korean", "zh-TW" => "Traditional Chinese", _ => "English",
    };

    /// <summary>A magic item's name in the client's order: base, prefix and suffix put into
    /// ClientStrings' MagicName format; English order when the format is not known.</summary>
    private string MagicNameInOrder(string baseName, string prefix, string suffix)
    {
        string id = prefix.Length > 0 && suffix.Length > 0 ? "MagicNamePrefixSuffix" : prefix.Length > 0 ? "MagicNamePrefix" : "MagicNameSuffix";
        string format = null;
        if (!EnglishClient && GameWords() != null) _gameFormats?.TryGetValue(id, out format);
        if (string.IsNullOrEmpty(format) || format.IndexOf("{0}", StringComparison.Ordinal) < 0)
            return string.Join(" ", new[] { prefix, baseName, suffix }.Where(x => x.Length > 0));
        string name = prefix.Length > 0 && suffix.Length > 0
            ? format.Replace("{0}", baseName).Replace("{1}", prefix).Replace("{2}", suffix)
            : format.Replace("{0}", baseName).Replace("{1}", prefix.Length > 0 ? prefix : suffix);
        return Regex.Replace(name, @"\s+", " ").Trim();
    }
}
