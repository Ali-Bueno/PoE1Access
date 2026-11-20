using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore.PoEMemory.MemoryObjects;
using ModsComp = ExileCore.PoEMemory.Components.Mods;
using QualityComp = ExileCore.PoEMemory.Components.Quality;
using WeaponComp = ExileCore.PoEMemory.Components.Weapon;
using ArmourComp = ExileCore.PoEMemory.Components.Armour;
using ShieldComp = ExileCore.PoEMemory.Components.Shield;
using ReqComp = ExileCore.PoEMemory.Components.AttributeRequirements;

namespace PoE1Access;

// The properties an item's tooltip shows above its mods: a weapon's damage, critical strike
// chance, attacks per second and range; armour, evasion, energy shield and block; and the level
// and attributes it requires. The game keeps the base values (Weapon, Armour, Shield,
// AttributeRequirements components; for armour, the value this item rolled); the tooltip adds
// the item's own local mods and its quality, the way the game does:
//   damage  = (base + added) * (1 + (increased physical + quality) / 100), each end rounded;
//   attacks = 1000 / attack time (ms) * (1 + increased attack speed / 100);
//   crit    = base (hundredths of a percent) * (1 + increased crit / 100);
//   defence = (base + added) * (1 + (increased + quality) / 100);
//   level   = the higher of the base type's level and 80% of its highest mod's level.
// Checked 20 Nov 2026 on a Rusted Sword (4 to 9, 5%, 1.55 attacks, range 1.1 metres).
public partial class PoE1AccessCore
{
    private List<string> ItemProperties(Entity item)
    {
        var lines = new List<string>();
        var mods = GetComp<ModsComp>(item);
        int quality = Try(() => GetComp<QualityComp>(item)?.ItemQuality ?? 0, 0);

        // The item's own stats, summed over its mods: "local_physical_damage_+%" -> 30.
        var local = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int highestModLevel = 0;
        var itemMods = mods != null ? Try(() => mods.ItemMods, null) : null;
        if (itemMods != null)
            foreach (var m in itemMods)
            {
                var names = Try(() => m.ModRecord?.StatNames, null);
                var values = Try(() => m.Values, null);
                highestModLevel = Math.Max(highestModLevel, Try(() => m.ModRecord?.MinLevel ?? 0, 0));
                if (names == null || values == null) continue;
                for (int i = 0; i < names.Length && i < values.Count; i++)
                {
                    string key = names[i]?.Key;
                    if (string.IsNullOrEmpty(key)) continue;
                    local[key] = (local.TryGetValue(key, out int v) ? v : 0) + values[i];
                }
            }
        int L(string key) => local.TryGetValue(key, out int v) ? v : 0;
        static int Round(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);

        var weapon = GetComp<WeaponComp>(item);
        if (weapon != null && Try(() => weapon.DamageMax, 0) > 0)
        {
            double phys = 1 + (L("local_physical_damage_+%") + quality) / 100.0;
            int min = Round((Try(() => weapon.DamageMin, 0) + L("local_minimum_added_physical_damage")) * phys);
            int max = Round((Try(() => weapon.DamageMax, 0) + L("local_maximum_added_physical_damage")) * phys);
            if (max > 0) lines.Add("Physical damage: " + min + " to " + max);
            foreach (string element in new[] { "fire", "cold", "lightning", "chaos" })
            {
                int emin = L("local_minimum_added_" + element + "_damage"), emax = L("local_maximum_added_" + element + "_damage");
                if (emax > 0) lines.Add(char.ToUpper(element[0]) + element.Substring(1) + " damage: " + emin + " to " + emax);
            }
            int crit = Try(() => weapon.CritChance, 0);
            if (crit > 0) lines.Add("Critical strike chance: " + (crit / 100.0 * (1 + L("local_critical_strike_chance_+%") / 100.0)).ToString("0.00") + "%");
            int attackTime = Try(() => weapon.AttackTime, 0);
            if (attackTime > 0) lines.Add("Attacks per second: " + (1000.0 / attackTime * (1 + L("local_attack_speed_+%") / 100.0)).ToString("0.00"));
            int range = Try(() => weapon.WeaponRange, 0) + L("local_weapon_range_+");
            if (range > 0) lines.Add("Weapon range: " + (range / 10.0).ToString("0.#") + " metres");
        }

        var armour = GetComp<ArmourComp>(item);
        if (armour != null)
        {
            int Defence(int baseValue, string added, string increased, params string[] hybrids)
            {
                int flat = baseValue + L(added);
                if (flat <= 0) return 0;
                int inc = L(increased) + quality + hybrids.Sum(L);
                return Round(flat * (1 + inc / 100.0));
            }
            int ar = Defence(Try(() => armour.ArmourScore, 0), "local_base_physical_damage_reduction_rating", "local_physical_damage_reduction_rating_+%",
                "local_armour_and_evasion_+%", "local_armour_and_energy_shield_+%", "local_armour_and_evasion_and_energy_shield_+%");
            int ev = Defence(Try(() => armour.EvasionScore, 0), "local_base_evasion_rating", "local_evasion_rating_+%",
                "local_armour_and_evasion_+%", "local_evasion_and_energy_shield_+%", "local_armour_and_evasion_and_energy_shield_+%");
            int es = Defence(Try(() => armour.EnergyShieldScore, 0), "local_energy_shield", "local_energy_shield_+%",
                "local_armour_and_energy_shield_+%", "local_evasion_and_energy_shield_+%", "local_armour_and_evasion_and_energy_shield_+%");
            if (ar > 0) lines.Add("Armour: " + ar);
            if (ev > 0) lines.Add("Evasion rating: " + ev);
            if (es > 0) lines.Add("Energy shield: " + es);
        }
        var shield = GetComp<ShieldComp>(item);
        if (shield != null)
        {
            int block = Try(() => shield.ChanceToBlock, 0) + L("local_additional_block_chance_%");
            if (block > 0) lines.Add("Chance to block: " + block + "%");
        }

        // Requirements: only gear and flasks have them (not currency, maps or jewels).
        string path = Try(() => item.Path, "") ?? "";
        bool gear = GetComp<ReqComp>(item) != null || weapon != null || armour != null ||
                    new[] { "/Flasks/", "/Rings/", "/Amulets/", "/Belts/", "/Quivers/" }.Any(k => path.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
        if (!gear) return lines;
        var req = new List<string>();
        int baseLevel = Try(() => GameController.Files.BaseItemTypes.Translate(item.Path)?.DropLevel ?? 0, 0);
        int level = Math.Max(baseLevel, (int)Math.Floor(highestModLevel * 0.8));
        if (level > 1) req.Add("level " + level);
        var attrs = GetComp<ReqComp>(item);
        if (attrs != null)
        {
            int str = Try(() => attrs.strength, 0), dex = Try(() => attrs.dexterity, 0), intel = Try(() => attrs.intelligence, 0);
            if (str > 0) req.Add(str + " Str");
            if (dex > 0) req.Add(dex + " Dex");
            if (intel > 0) req.Add(intel + " Int");
        }
        if (req.Count > 0) lines.Add("Requires " + string.Join(", ", req));
        return lines;
    }
}
