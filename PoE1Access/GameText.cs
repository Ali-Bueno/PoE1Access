using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PoE1Access;

// The game's own words for the panel titles and controller prompts the mod looks for, in the
// client's language. The mod finds windows and prompts by the words the game shows, so it
// needs them in every language the game offers, not only English.
//
// They come from the game's ClientStrings table (Data/<Language>/ClientStrings.datc64), read
// by row Id, which is the same in every language. A row (52 bytes) holds pointers to its Id
// (+0), its text (+8) and the text the controller interface shows instead, when it has its
// own (+0x10): the panel title "Skills" (Text) is "Passives" (XBoxText) with a controller.
// Layout checked 20 Nov 2026 on all ten client languages.
//
// The language is the "language=" entry under [LANGUAGE] in production_Config.ini ("en",
// "es", "pt", "de", "fr", "ru", "th", "ja", "ko", "zh-TW"). In English the table is not read
// at all; if it cannot be read, only the English words are known.
public partial class PoE1AccessCore
{
    /// <summary>A word the game shows: its English text and the ClientStrings rows that hold it.</summary>
    private sealed class GameWord
    {
        public readonly string English;
        public readonly string[] Ids;
        public GameWord(string english, params string[] ids) { English = english; Ids = ids; }
    }

    // Panel titles, as the panels show them with a controller.
    private static readonly GameWord InventoryTitle = new("Inventory", "InventoryPanelTitle");
    private static readonly GameWord CharacterTitle = new("Character", "CharacterPanelTitle");
    private static readonly GameWord SkillsTitle = new("Skills", "ConsoleOpenSkillsPanel");
    private static readonly GameWord WorldTitle = new("World", "WorldPanelTitle");
    private static readonly GameWord PassivesTitle = new("Passives", "PassiveTreePanelTitle", "ActionOpenPassivePanel");
    private static readonly GameWord StashTitle = new("Stash", "StashPanelTitle");

    // Button legend entries: common ones, by which the legend is found, and those of skill
    // binding mode.
    private static readonly GameWord[] LegendWords =
    {
        new("Pick Up", "ConsolePickUp"), new("Move", "ConsoleMove", "HideoutLegendMove"),
        new("Close", "ConsoleClosePanel"), new("Zoom", "ConsoleZoom"), new("Navigate", "ConsoleNavigate"),
        new("Back", "ConsoleBack"), new("Select", "ConsoleSelect", "ConsoleActionSelectItem"), new("Confirm"),
    };
    // The screens before the game (login, character selection, leagues, classes) have small
    // legends of their own; only their own prompts find them, since words such as "Back" are
    // on those screens' buttons too.
    private static readonly GameWord[] PreGameLegendWords =
    {
        new("Options", "LoginScreenOptionsButtonText", "ActionOpenOptionsPanel"), new("Current League Info", "LeagueInfoBindLegendText"),
        new("Change Selection", "ConsoleChangeSelection"), new("Select Character", "ConsoleSelectCharacter"),
        new("Delete Character", "ConsoleDeleteCharacter"), new("Create New Character", "ConsoleCreateNewCharacter"),
        new("Log Out", "ConsoleLogOut"), new("Switch League", "ConsoleSwitchLeague"), new("Select League", "ConsoleSelectLeague"),
        new("Switch Class", "ConsoleSwitchClass"), new("Select Class", "ConsoleSelectClass"),
    };
    private static readonly GameWord NavigateSkillsWord = new("Navigate Skills", "ConsoleNavigateSkills");
    private static readonly GameWord SelectSkillWord = new("Select Skill", "ConsoleSelectSkill");

    // Skills panel labels that show with no value when a skill has none (no cost, no souls),
    // and its "Use Alt Behaviour" toggle; the mod leaves them out.
    private static readonly GameWord[] BareSkillLabels =
    {
        new("Cost", "SkillPopupManaCost"), new("Souls", "SkillPopupSouls"), new("Reservation", "SkillPopupManaReserved"),
        new("Cooldown"), new("Cast Time", "SkillPopupCastTime"), new("Attack Time", "SkillPopupAttackTime"),
    };
    private static readonly GameWord AltBehaviourWord = new("Use Alt Behaviour", "ConsoleSkillBarAlternativeBehaviour");

    private Dictionary<string, string> _gameWords;   // row Id -> its text in the client's language
    private string _gameLanguage;
    private long _nextGameWordsTryMs;
    private int _gameWordsTries;

    /// <summary>The client's text language from the game's config ("en" when not known).</summary>
    private string GameLanguage
    {
        get
        {
            if (_gameLanguage != null) return _gameLanguage;
            _gameLanguage = Try(ReadGameLanguage, null) ?? "en";
            PluginLog.Write("Game language: " + _gameLanguage);
            return _gameLanguage;
        }
    }

    private bool EnglishClient => GameLanguage == "en";

    private static string ReadGameLanguage()
    {
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
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

    /// <summary>True if `text` is the word, in English or in the client's language.</summary>
    private bool IsGameWord(string text, GameWord word) =>
        !string.IsNullOrEmpty(text) && WordsFor(word).Any(w => w.Equals(text, StringComparison.OrdinalIgnoreCase));

    /// <summary>True if `text` ends with the word, in English or in the client's language.</summary>
    private bool EndsWithGameWord(string text, GameWord word) =>
        !string.IsNullOrEmpty(text) && WordsFor(word).Any(w => text.EndsWith(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>The word's English text and its texts in the client's language.</summary>
    private IEnumerable<string> WordsFor(GameWord word)
    {
        yield return word.English;
        var table = GameWords();
        if (table == null) yield break;
        foreach (string id in word.Ids)
            if (table.TryGetValue(id, out string text)) yield return text;
    }

    /// <summary>The client's ClientStrings, or null (English client, or not readable yet).</summary>
    private Dictionary<string, string> GameWords()
    {
        if (_gameWords != null) return _gameWords;
        if (EnglishClient || _gameWordsTries >= 10) return null;
        long now = Environment.TickCount64;
        if (now < _nextGameWordsTryMs) return null;
        _nextGameWordsTryMs = now + 5000;   // the game's files load with the game
        _gameWordsTries++;
        _gameWords = Try(ReadClientStrings, null);
        if (_gameWords != null) PluginLog.Write($"Game words ({GameLanguage}): {_gameWords.Count} ClientStrings rows");
        else if (_gameWordsTries == 10) PluginLog.Write("Game words: ClientStrings not readable, only English titles are known");
        return _gameWords;
    }

    private Dictionary<string, string> ReadClientStrings()
    {
        // ExileApi's game, or the launcher's on the screens before the game (login, characters).
        var all = Game?.Files?.AllFiles;
        if (all == null || all.Count == 0) return null;
        // The client's own language folder first ("Data/Spanish/ClientStrings.datc64"), the
        // English table otherwise.
        var keys = all.Keys.Where(k => k.IndexOf("/ClientStrings.", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        string key = keys.FirstOrDefault(k => !k.StartsWith("Data/ClientStrings.", StringComparison.OrdinalIgnoreCase)) ?? keys.FirstOrDefault();
        if (key == null) return null;
        var file = new DatFile(Mem, all[key].Ptr);
        if (file.Count <= 0 || file.Length < 0x18) return null;

        var mem = Mem;
        byte[] block = mem.ReadBytes(file.First, (int)(file.Count * file.Length));
        string Str(long p)
        {
            if (p < 0x10000 || p > 0x7FFF_FFFF_FFFF) return "";
            string s = mem.ReadStringU(p, 400) ?? "";
            return s.All(ch => ch >= 0x20 && ch < 0xFFF0 && !char.IsSurrogate(ch)) ? s : "";
        }
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < file.Count && (i + 1) * file.Length <= block.Length; i++)
        {
            int at = (int)(i * file.Length);
            string id = Str(BitConverter.ToInt64(block, at));
            if (id.Length == 0) continue;
            // The controller interface's text where the row has one: the mod is played with a
            // controller, and the passive tree's mouse title is the skills panel's word.
            string text = CleanText(Str(BitConverter.ToInt64(block, at + 0x10)));
            if (text.Length == 0) text = CleanText(Str(BitConverter.ToInt64(block, at + 8)));
            if (text.Length > 0) map[id] = text;
        }
        // The table is right only if a row everyone has reads as an Id with text.
        if (!map.ContainsKey("InventoryPanelTitle")) return null;
        PluginLog.Write("Game words from " + key + ": inventory is \"" + map["InventoryPanelTitle"] + "\"");
        return map;
    }
}
