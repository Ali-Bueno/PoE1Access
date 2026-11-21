using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PoE1Access;

/// <summary>
/// The language of the mod's own words. It follows the game's text language, the "language="
/// entry under [LANGUAGE] in production_Config.ini ("en", "es", "pt", "fr", "de", "ru", "th",
/// "ja", "ko", "zh-TW"), read when the mod starts (the game needs a restart to change it).
///
/// Every spoken phrase is written as T("English", "Spanish"): English and Spanish are in the
/// code; the other languages are tables in lang\&lt;code&gt;.json (embedded in the DLL), keyed by
/// the English phrase, or by its composite format for an interpolated one ("Life below {0}
/// percent"). A phrase a table lacks is said in English. tools\localization\phrases.py lists the
/// phrases and checks the tables. Taken from the Path of Exile 2 accessibility mod.
/// </summary>
public static class Loc
{
    public static readonly string[] Languages = { "en", "es", "pt", "fr", "de", "ru", "th", "ja", "ko", "zh-TW" };

    private static Dictionary<string, string> _phrases = new Dictionary<string, string>(StringComparer.Ordinal);

    public static string Language { get; private set; } = "en";

    /// <summary>A phrase in English and Spanish; other languages from their table.</summary>
    public static string T(string en, string es) => Language switch
    {
        "en" => en,
        "es" => es,
        _ => _phrases.TryGetValue(en, out string t) ? t : en,
    };

    /// <summary>An interpolated phrase; other languages look its English composite format up.</summary>
    public static string T(Phrase en, Phrase es) => Language switch
    {
        "en" => en.Text(),
        "es" => es.Text(),
        _ => en.Text(_phrases.TryGetValue(en.Format, out string t) ? t : null),
    };

    /// <summary>Speaks the game's language from its config (English if it cannot be read). Returns what was used.</summary>
    public static string UseGameLanguage(Action<string> log = null)
    {
        string fromGame = null;
        try { fromGame = ReadGameLanguage(); } catch { }
        Use(Normalize(fromGame) ?? "en", log);
        log?.Invoke("Mod language: " + Language + " (game language " + (fromGame ?? "unknown") + ")");
        return Language;
    }

    /// <summary>The supported language a code names ("ES", "pt-BR", "zh_tw"), or null.</summary>
    public static string Normalize(string code)
    {
        string wanted = code?.Trim().Replace('_', '-');
        if (string.IsNullOrEmpty(wanted)) return null;
        string exact = Languages.FirstOrDefault(l => l.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        int dash = wanted.IndexOf('-');
        string stem = dash > 0 ? wanted.Substring(0, dash) : wanted;
        if (stem.Equals("zh", StringComparison.OrdinalIgnoreCase)) return "zh-TW";
        return Languages.FirstOrDefault(l => l.Equals(stem, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The "language=" entry under [LANGUAGE] in the game's config, or null.</summary>
    public static string ReadGameLanguage()
    {
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(docs)) docs = Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? "", "Documents");
        string path = Path.Combine(docs, "My Games", "Path of Exile", "production_Config.ini");
        if (!File.Exists(path)) return null;
        bool inSection = false;
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.StartsWith("[")) { inSection = line.Equals("[LANGUAGE]", StringComparison.OrdinalIgnoreCase); continue; }
            int eq = line.IndexOf('=');
            if (inSection && eq > 0 && line.Substring(0, eq).Trim().Equals("language", StringComparison.OrdinalIgnoreCase))
            {
                string v = line.Substring(eq + 1).Trim();
                return v.Length > 0 ? v : null;
            }
        }
        return null;
    }

    private static void Use(string language, Action<string> log)
    {
        Language = language;
        _phrases = language is "en" or "es" ? new Dictionary<string, string>(StringComparer.Ordinal) : LoadPhrases(language, log);
    }

    // The table is an embedded resource named "lang.<code>.json": { "English phrase": "translation" }.
    private static Dictionary<string, string> LoadPhrases(string language, Action<string> log)
    {
        var empty = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using Stream s = typeof(Loc).Assembly.GetManifestResourceStream("lang." + language + ".json");
            if (s == null) { log?.Invoke("No phrases for '" + language + "'; English is spoken"); return empty; }
            var table = JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new Dictionary<string, string>();
            log?.Invoke("Phrases for '" + language + "': " + table.Count);
            return new Dictionary<string, string>(table.Where(kv => !string.IsNullOrEmpty(kv.Value))
                .ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            log?.Invoke("Phrases for '" + language + "' unreadable, English is spoken: " + ex.Message);
            return empty;
        }
    }
}
