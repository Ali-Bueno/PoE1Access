using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore.PoEMemory;
using ExileCore.Shared.Interfaces;
using GemComp = ExileCore.PoEMemory.Components.SkillGem;

namespace PoE1Access;

// What a gem's tooltip shows besides its name, tags, level and description, read from the
// game's own data (the tooltip itself is drawn where the mod cannot read it):
//  - its cost ("Cost: 6 Mana"), from Data/GrantedEffectsPerLevel;
//  - its cooldown, and the cast time of spells;
//  - its stats: those that hold at every level (Data/GrantedEffectStatSets) and those of its
//    level (Data/GrantedEffectStatSetsPerLevel), worded by StatWords, since ExileApi's own
//    stat wording does not work on the 18 Nov 2026 patch;
//  - the experience it needs for its next level.
// Layouts found 20 Nov 2026 (Molten Strike, Spectral Throw); each read is checked, and a
// value that does not look right is left out rather than guessed.
public partial class PoE1AccessCore
{
    private sealed class DatFile : FileInMemory
    {
        public DatFile(IMemory m, long address) : base(m, () => address) { }
        public int Count => NumberOfRecords;
        public long Length => RecordLength;
        public long First => FirstRecord;
        public bool Holds(long row) => Count > 0 && Length > 0 && row >= First && row < First + Count * Length && (row - First) % Length == 0;
    }

    private DatFile _statSets, _statSetsPerLevel;
    private Dictionary<(long Set, int Level), long> _statSetLevels;
    private bool _gemDataFailed;

    private DatFile OpenDat(string name)
    {
        var all = GameController.Files.AllFiles;
        string key = all.Keys.FirstOrDefault(k => k.StartsWith(name, StringComparison.OrdinalIgnoreCase));
        if (key == null) return null;
        var f = new DatFile(Mem, all[key].Ptr);
        return f.Count > 0 && f.Length > 0 ? f : null;
    }

    private bool LoadGemData()
    {
        if (_statSetLevels != null) return true;
        if (_gemDataFailed) return false;
        try
        {
            _statSets = OpenDat("Data/GrantedEffectStatSets.");
            _statSetsPerLevel = OpenDat("Data/GrantedEffectStatSetsPerLevel.");
            if (_statSets == null || _statSetsPerLevel == null) { _gemDataFailed = true; return false; }
            // Every (stat set, level) row, read in one go: its first 8 bytes are the stat set and
            // the int at +0x10 the gem level.
            long len = _statSetsPerLevel.Length;
            int count = _statSetsPerLevel.Count;
            byte[] block = Mem.ReadBytes(_statSetsPerLevel.First, (int)(count * len));
            var map = new Dictionary<(long, int), long>();
            for (int i = 0; i < count && (i + 1) * len <= block.Length; i++)
            {
                long at = i * len;
                long set = BitConverter.ToInt64(block, (int)at);
                int level = BitConverter.ToInt32(block, (int)at + 0x10);
                map[(set, level)] = _statSetsPerLevel.First + at;
            }
            _statSetLevels = map;
            PluginLog.Write($"Gem data: {_statSets.Count} stat sets, {count} levels");
            return true;
        }
        catch (Exception ex)
        {
            _gemDataFailed = true;
            PluginLog.Write("Gem data not readable: " + ex.Message);
            return false;
        }
    }

    private static bool IsPtr(long p) => p > 0x10000 && p < 0x7FFF_FFFF_FFFF;

    private string AsciiAt(long p)
    {
        if (!IsPtr(p)) return null;
        string t = Try(() => Mem.ReadStringU(p, 120), null);
        return !string.IsNullOrEmpty(t) && t.All(c => c >= 32 && c < 127) ? t : null;
    }

    /// <summary>Lines for a gem besides its tags, level and description: cost, cooldown, cast
    /// time, then (after the description) its stats and the experience to its next level.</summary>
    private (List<string> Before, List<string> After) GemDetails(GemComp gem)
    {
        var before = new List<string>();
        var after = new List<string>();
        var effect = Try(() => gem.GemEffect?.GrantedEffect, null);
        if (effect == null) return (before, after);
        int level = Try(() => gem.Level, 0);
        var mem = Mem;

        // Cost, cooldown: the gem level's GrantedEffectsPerLevel row.
        var perLevel = Try(() => effect.PerLevelEffects?.FirstOrDefault(p => p.Level == level), null);
        if (perLevel != null && perLevel.Address != 0)
        {
            Try(() =>
            {
                long row = perLevel.Address;
                long n = mem.Read<long>(row + 0x44), amounts = mem.Read<long>(row + 0x4C);
                long tn = mem.Read<long>(row + 0x54), types = mem.Read<long>(row + 0x5C);
                if (n >= 1 && n <= 4 && n == tn && IsPtr(amounts) && IsPtr(types))
                {
                    var costs = new List<string>();
                    for (int i = 0; i < n; i++)
                    {
                        int amount = mem.Read<int>(amounts + i * 4);
                        string type = AsciiAt(mem.Read<long>(mem.Read<long>(types + i * 16)));
                        if (amount > 0 && amount < 100000 && type != null) costs.Add(amount + " " + type);
                    }
                    if (costs.Count > 0) before.Add("Cost: " + string.Join(", ", costs));
                }
                return 0;
            }, 0);
            int cooldown = Try(() => perLevel.Cooldown, 0);
            if (cooldown > 0 && cooldown < 600000) before.Add("Cooldown: " + Seconds(cooldown));
        }
        var types2 = Try(() => effect.ActiveSkill?.SkillTypes, null);
        bool spell = types2 != null && types2.Any(t => t.ToString() == "Spell");
        bool attack = types2 != null && types2.Any(t => t.ToString() == "Attack");
        int cast = Try(() => effect.CastTime, 0);
        if (spell && !attack && cast > 0 && cast < 60000) before.Add("Cast time: " + Seconds(cast));

        // Stats.
        if (LoadGemData())
        {
            long set = 0;
            for (int off = 0; off < 0x200 && set == 0; off++)
            {
                long p = Try(() => mem.Read<long>(effect.Address + off), 0L);
                if (_statSets.Holds(p)) set = p;
            }
            if (set != 0)
            {
                var stats = new List<(string Key, int Value)>();
                stats.AddRange(StatPairs(set, (int)_statSets.Length));
                if (_statSetLevels.TryGetValue((set, level), out long levelRow))
                    stats.AddRange(StatPairs(levelRow, (int)_statSetsPerLevel.Length));
                var seen = new HashSet<string>();
                foreach (var (key, value) in stats)
                {
                    if (!seen.Add(key) || HiddenGemStat(key)) continue;
                    string words = GemStatWords(key, value);
                    if (words.Length > 0) after.Add(words);
                }
            }
        }

        long toNext = Try(() => (long)gem.ExperienceToNextLevel, 0L);
        int max = Try(() => gem.MaxLevel, 0);
        if (toNext > 0 && (max == 0 || level < max)) after.Add("Experience to next level: " + toNext);
        return (before, after);
    }

    private static string Seconds(int ms) => (ms / 1000.0).ToString("0.##") + " seconds";

    // Stats in a data row: a list of Stats rows (16 bytes each) followed somewhere after it by a
    // list of as many ints, its values ("base_number_of_projectiles" with 4).
    private List<(string Key, int Value)> StatPairs(long row, int length)
    {
        var result = new List<(string, int)>();
        var mem = Mem;
        var statsFile = Try(() => GameController.Files.Stats, null);
        if (statsFile == null) return result;
        byte[] b = Try(() => mem.ReadBytes(row, length), null);
        if (b == null) return result;
        var lists = new List<(int Off, long Count, long Ptr)>();
        for (int off = 0; off + 16 <= b.Length; off += 8)
        {
            long n = BitConverter.ToInt64(b, off), p = BitConverter.ToInt64(b, off + 8);
            if (n >= 1 && n <= 40 && IsPtr(p)) lists.Add((off, n, p));
        }
        for (int i = 0; i < lists.Count; i++)
        {
            var (off, n, p) = lists[i];
            var keys = new List<string>();
            for (int j = 0; j < n; j++)
            {
                long statRow = Try(() => mem.Read<long>(p + j * 16), 0L);
                string key = Try(() => statsFile.GetStatByAddress(statRow)?.Key, null);
                if (string.IsNullOrEmpty(key)) { keys = null; break; }
                keys.Add(key);
            }
            if (keys == null) continue;
            // Its values: the next list of the same length that is not itself a list of stats.
            for (int k = i + 1; k < lists.Count; k++)
            {
                if (lists[k].Count != n) continue;
                long vp = lists[k].Ptr;
                long first = Try(() => mem.Read<long>(vp), 0L);
                if (Try(() => statsFile.GetStatByAddress(first), null) != null) break;   // another stat list: no values
                for (int j = 0; j < n; j++) result.Add((keys[j], Try(() => mem.Read<int>(vp + j * 4), 0)));
                i = k;
                break;
            }
        }
        return result;
    }

    // Stats the game uses for its own display or bookkeeping, not shown as lines.
    private static bool HiddenGemStat(string key) =>
        key.StartsWith("show_") || key.Contains("display") || key.StartsWith("is_") ||
        key.StartsWith("skill_can_") || key.EndsWith("_is_gem") ||
        // Internal tuning the tooltip does not show ("spectral_throw_forget_hit_list_time_override").
        key.Contains("override") || key.Contains("hit_list") || key.Contains("_visual") || key.Contains("_art_");

    // Gem stats in words, for the patterns gems use most; others by StatWords.
    private static string GemStatWords(string key, int value)
    {
        string Words(string s) => s.Replace("_", " ").Replace("  ", " ").Trim();
        var m = System.Text.RegularExpressions.Regex.Match(key, @"^(?:skill_|base_)?(\w+?)_damage_%_to_convert_to_(\w+)$");
        if (m.Success) return value + "% of " + Words(m.Groups[1].Value) + " damage converted to " + Words(m.Groups[2].Value) + " damage";
        m = System.Text.RegularExpressions.Regex.Match(key, @"^(?:skill_|base_)?(\w+?)_damage_%_to_add_as_(\w+)$");
        if (m.Success) return "Gain " + value + "% of " + Words(m.Groups[1].Value) + " damage as extra " + Words(m.Groups[2].Value) + " damage";
        if (key == "base_number_of_projectiles" || key == "number_of_additional_projectiles")
            return key.StartsWith("base_") ? (value == 1 ? "Fires 1 projectile" : "Fires " + value + " projectiles")
                                           : value + " additional projectile" + (value == 1 ? "" : "s");
        m = System.Text.RegularExpressions.Regex.Match(key, @"^(\w+?)_%_chance$");
        if (m.Success) return value + "% chance to " + Words(m.Groups[1].Value);
        m = System.Text.RegularExpressions.Regex.Match(key, @"^(\w+?)_%_chance_(\w+)$");
        if (m.Success) return value + "% chance " + Words(m.Groups[2].Value) + " to " + Words(m.Groups[1].Value);
        string k = key.StartsWith("active_skill_") ? key.Substring("active_skill_".Length) : key;
        if (k.EndsWith("_ms")) return Words(k[..^3]) + ": " + Seconds(value);
        return StatWords(k, value);
    }
}
