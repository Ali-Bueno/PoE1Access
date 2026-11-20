using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore.PoEMemory;
using ExileCore.Shared.Interfaces;
using GemComp = ExileCore.PoEMemory.Components.SkillGem;
using static PoE1Access.Loc;

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
                    if (costs.Count > 0)
                    {
                        string costList = string.Join(", ", costs);
                        before.Add(T($"Cost: {costList}", $"Coste: {costList}"));
                    }
                }
                return 0;
            }, 0);
            int cooldown = Try(() => perLevel.Cooldown, 0);
            if (cooldown > 0 && cooldown < 600000) before.Add(T($"Cooldown: {Seconds(cooldown)}", $"Reutilización: {Seconds(cooldown)}"));
        }
        var types2 = Try(() => effect.ActiveSkill?.SkillTypes, null);
        bool spell = types2 != null && types2.Any(t => t.ToString() == "Spell");
        bool attack = types2 != null && types2.Any(t => t.ToString() == "Attack");
        int cast = Try(() => effect.CastTime, 0);
        if (spell && !attack && cast > 0 && cast < 60000) before.Add(T($"Cast time: {Seconds(cast)}", $"Tiempo de lanzamiento: {Seconds(cast)}"));

        // Stats: the gem's own skill, then a skill it adds (the Prismatic Burst a support
        // triggers, a Vaal gem's Vaal skill), its lines named after it.
        if (LoadGemData())
        {
            after.AddRange(EffectStatLines(effect, level, "", true, before));
            var second = Try(() => gem.GemEffect?.GrantedEffect2, null);
            if (second != null && second.Address != 0 && second.Address != effect.Address)
            {
                string name = Try(() => second.ActiveSkill?.DisplayName, "") ?? "";
                var extra = new List<string>();
                var lines = EffectStatLines(second, level, name, false, extra);
                extra.AddRange(lines);
                after.AddRange(extra.Select(l => name.Length > 0 ? name + ": " + l : l));
            }
        }

        long toNext = Try(() => (long)gem.ExperienceToNextLevel, 0L);
        int max = Try(() => gem.MaxLevel, 0);
        if (toNext > 0 && (max == 0 || level < max)) after.Add(T($"Experience to next level: {toNext}", $"Experiencia para el siguiente nivel: {toNext}"));
        return (before, after);
    }

    // A skill's stat lines at a gem level. The level row (Data/GrantedEffectStatSetsPerLevel,
    // 0xB8 bytes on the 18 Nov 2026 patch) holds: +0x18 spell critical strike chance and +0x1C
    // attack critical strike chance (hundredths of a percent), +0x20 attack damage and +0x24
    // effectiveness of added damage (hundredths of a percent over 100), then lists of 16 bytes
    // (count, pointer): +0x28 the values of the stats at +0x68 (base damage and the like),
    // +0x38 the values of the stats at +0x88, +0x58 stats that are simply on.
    // Spectral Throw's attack damage (0 at level 1, 5000 at 20: 100% to 150%) and Prismatic
    // Burst's damage (3 to 5 cold at level 1) checked against the game.
    private List<string> EffectStatLines(ExileCore.PoEMemory.FilesInMemory.GrantedEffect effect, int level, string skillName, bool primary, List<string> headLines)
    {
        var lines = new List<string>();
        var mem = Mem;
        long set = 0;
        for (int off = 0; off < 0x200 && set == 0; off++)
        {
            long p = Try(() => mem.Read<long>(effect.Address + off), 0L);
            if (_statSets.Holds(p)) set = p;
        }
        if (set == 0) return lines;

        var stats = new List<(string Key, int Value)>();
        stats.AddRange(StatPairs(set, (int)_statSets.Length));   // the same at every level
        if (_statSetLevels.TryGetValue((set, level), out long row) && _statSetsPerLevel.Length >= 0xB8)
        {
            var types = Try(() => effect.ActiveSkill?.SkillTypes, null);
            bool attack = types != null && types.Any(t => t.ToString() == "Attack");
            bool spell = types != null && types.Any(t => t.ToString() == "Spell");
            int spellCrit = Try(() => mem.Read<int>(row + 0x18), 0), attackCrit = Try(() => mem.Read<int>(row + 0x1C), 0);
            int multiplier = Try(() => mem.Read<int>(row + 0x20), 0), effectiveness = Try(() => mem.Read<int>(row + 0x24), 0);
            string Pct(int v) => (100 + v / 100.0).ToString("0.#") + "%";
            if (attack && multiplier > -10000 && multiplier < 100000) { string pm = Pct(multiplier); headLines.Add(T($"Attack damage: {pm} of base", $"Daño de ataque: {pm} del base")); }
            int crit = spell ? spellCrit : attackCrit;
            if (crit > 0 && crit <= 10000) { string cs = (crit / 100.0).ToString("0.##"); headLines.Add(T($"Critical strike chance: {cs}%", $"Probabilidad de golpe crítico: {cs}%")); }
            if (!attack && (spell || effectiveness != 0) && effectiveness > -10000 && effectiveness < 100000)
            {
                string pe = Pct(effectiveness);
                headLines.Add(T($"Effectiveness of added damage: {pe}", $"Efectividad del daño añadido: {pe}"));
            }
            stats.AddRange(ListPairs(row, 0x68, 0x28));
            stats.AddRange(ListPairs(row, 0x88, 0x38));
            foreach (string flag in StatList(row, 0x58)) stats.Add((flag, 1));
        }

        // "Deals 3 to 5 cold damage": a minimum and a maximum together.
        var seen = new HashSet<string>();
        foreach (var (key, value) in stats)
        {
            if (seen.Contains(key) || HiddenGemStat(key)) continue;
            var m = System.Text.RegularExpressions.Regex.Match(key, @"^(.*?)minimum_(.+)$");
            if (m.Success)
            {
                string maxKey = m.Groups[1].Value + "maximum_" + m.Groups[2].Value;
                var max = stats.FirstOrDefault(s => s.Key == maxKey);
                if (max.Key != null)
                {
                    seen.Add(key); seen.Add(maxKey);
                    if (value == 0 && max.Value == 0) continue;
                    string what = m.Groups[2].Value;
                    if (what.StartsWith("base_")) what = what.Substring(5);
                    string whatWords = what.Replace("_", " ");
                    int maxValue = max.Value;
                    lines.Add(T($"Deals {value} to {maxValue} {whatWords}", $"Inflige de {value} a {maxValue} de {whatWords}"));
                    continue;
                }
            }
            seen.Add(key);
            if (value == 0) continue;
            string words = GemStatWords(key, value);
            if (words.Length > 0) lines.Add(words);
        }
        return lines;
    }

    // Stats at +statsOff (a list of Stats rows) with their values at +valuesOff (as many ints).
    private List<(string Key, int Value)> ListPairs(long row, int statsOff, int valuesOff)
    {
        var result = new List<(string, int)>();
        var mem = Mem;
        var keys = StatList(row, statsOff);
        long n = Try(() => mem.Read<long>(row + valuesOff), 0L), p = Try(() => mem.Read<long>(row + valuesOff + 8), 0L);
        if (keys.Count == 0 || n != keys.Count || !IsPtr(p)) return result;
        for (int i = 0; i < n; i++) result.Add((keys[i], Try(() => mem.Read<int>(p + i * 4), 0)));
        return result;
    }

    // The stat ids in a list of Stats rows at +off; empty if any entry is not a stat.
    private List<string> StatList(long row, int off)
    {
        var keys = new List<string>();
        var mem = Mem;
        var statsFile = Try(() => GameController.Files.Stats, null);
        long n = Try(() => mem.Read<long>(row + off), 0L), p = Try(() => mem.Read<long>(row + off + 8), 0L);
        if (statsFile == null || n < 1 || n > 40 || !IsPtr(p)) return keys;
        for (int i = 0; i < n; i++)
        {
            long statRow = Try(() => mem.Read<long>(p + i * 16), 0L);
            string key = Try(() => statsFile.GetStatByAddress(statRow)?.Key, null);
            if (string.IsNullOrEmpty(key)) return new List<string>();
            keys.Add(key);
        }
        return keys;
    }

    private static string Seconds(int ms)
    {
        string s = (ms / 1000.0).ToString("0.##");
        return T($"{s} seconds", $"{s} segundos");
    }

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
        if (m.Success)
        {
            string src = Words(m.Groups[1].Value), dst = Words(m.Groups[2].Value);
            return T($"{value}% of {src} damage converted to {dst} damage", $"{value}% del daño de {src} convertido en daño de {dst}");
        }
        m = System.Text.RegularExpressions.Regex.Match(key, @"^(?:skill_|base_)?(\w+?)_damage_%_to_add_as_(\w+)$");
        if (m.Success)
        {
            string src = Words(m.Groups[1].Value), dst = Words(m.Groups[2].Value);
            return T($"Gain {value}% of {src} damage as extra {dst} damage", $"Ganas {value}% del daño de {src} como daño adicional de {dst}");
        }
        if (key == "base_number_of_projectiles" || key == "number_of_additional_projectiles")
            return key.StartsWith("base_")
                ? (value == 1 ? T("Fires 1 projectile", "Dispara 1 proyectil") : T($"Fires {value} projectiles", $"Dispara {value} proyectiles"))
                : (value == 1 ? T("1 additional projectile", "1 proyectil adicional") : T($"{value} additional projectiles", $"{value} proyectiles adicionales"));
        m = System.Text.RegularExpressions.Regex.Match(key, @"^(\w+?)_%_chance$");
        if (m.Success)
        {
            string what = Words(m.Groups[1].Value);
            return T($"{value}% chance to {what}", $"{value}% de probabilidad de {what}");
        }
        m = System.Text.RegularExpressions.Regex.Match(key, @"^(\w+?)_%_chance_(\w+)$");
        if (m.Success)
        {
            string cond = Words(m.Groups[2].Value), what = Words(m.Groups[1].Value);
            return T($"{value}% chance {cond} to {what}", $"{value}% de probabilidad {cond} de {what}");
        }
        string k = key.StartsWith("active_skill_") ? key.Substring("active_skill_".Length) : key;
        if (k.EndsWith("_ms")) return Words(k[..^3]) + ": " + Seconds(value);
        return StatWords(k, value);
    }
}
