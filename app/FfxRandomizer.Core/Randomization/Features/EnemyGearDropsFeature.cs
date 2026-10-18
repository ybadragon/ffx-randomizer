using System.Text.Json.Nodes;
using FfxRandomizer.Core.GameData;

namespace FfxRandomizer.Core.Randomization.Features;

/// <summary>
/// Weapons and armor enemies drop: drop chance, slot and ability roll counts, the abilities each character's gear can
/// roll, and (when the Weapon stats options are on) power, crit and formula. Follows FFXRando's randomizeEnemyGearDrops,
/// with three fixes: every roll slot gets its own ability (FFXRando wrote one ability into all eight), the forced ability
/// in slot 0 (Auron's and Kimahri's Piercing) stays, and with Keep things sane on, enemies that never dropped gear still
/// don't (FFXRando gave them a 0-255 chance of dropping gear whose stats are all zero).
/// </summary>
public sealed class EnemyGearDropsFeature : IRandomizerFeature
{
    public bool IsEnabled(RandomizerSettings s) => s.IsOn("rewards") && s.IsOn("geardrops");

    public void Apply(RandomizerContext context)
    {
        var s = context.Settings;
        bool sane = s.IsOn("sane");
        bool stats = s.IsOn("wstats");
        var pool = GearAbilityPool.Build(context.Original);
        var rng = context.RngFor("enemies.gear");

        foreach (int index in MonsterFile.AllIndices(context.Original))
        {
            string path = MonsterFile.PathFor(index);
            var original = new MonsterFile(index, context.Original.ReadFile(path));
            if (!original.HasLoot || sane && original.Loot.GearDropChance == 0) continue;
            var loot = new MonsterFile(index, context.Edit(path)).Loot;
            byte chance = loot.GearDropChance, power = loot.GearPower, crit = loot.GearCrit, formula = loot.GearFormula;

            loot.GearSlotsByte = (byte)rng.Next(0, sane ? 16 : 20);
            loot.GearAbilityRollsByte = (byte)rng.Next(0, sane ? 16 : 20);
            loot.GearDropChance = (byte)(sane ? rng.Normal(chance, chance / 2.0, 0, 101) : rng.Next(0, 255));

            // Without sane, enemies that never dropped gear can start to; give their empty gear the usual baseline.
            if (loot.GearPower == 0) loot.GearPower = 16;
            if (loot.GearFormula == WeaponFormulas.None) loot.GearFormula = WeaponFormulas.StrVsDef;
            if (stats && s.IsOn("atk") && power > 0)
                loot.GearPower = (byte)(sane ? rng.Normal(power, power / 2.0, 1, 24) : rng.Next(1, 100));
            if (stats && s.IsOn("crit") && crit > 0)
                loot.GearCrit = (byte)(sane ? rng.Normal(crit, crit / 2.0, 0, 100) : rng.Next(1, 100));
            if (stats && s.IsOn("formula") && formula > 0)
                loot.GearFormula = rng.Pick(sane ? WeaponFormulas.Sane : WeaponFormulas.All);

            var weapons = new JsonObject();
            var armor = new JsonObject();
            for (int chr = 0; chr < MonsterLoot.GearCharacters; chr++)
            {
                for (int slot = 1; slot < MonsterLoot.GearAbilitySlots; slot++)
                {
                    loot.SetWeaponAbility(chr, slot, rng.Pick(pool.Weapon));
                    loot.SetArmorAbility(chr, slot, rng.Pick(pool.Armor));
                }
                weapons[KernelFiles.CharacterNames[chr]] = Show(context, loot, chr, armor: false);
                armor[KernelFiles.CharacterNames[chr]] = Show(context, loot, chr, armor: true);
            }

            EnemyRewardsFeature.EnemyEntry(context, index)["gear"] = new JsonObject
            {
                ["chance"] = RandomizerContext.ToNode(new { From = chance, To = loot.GearDropChance }),
                ["slotsByte"] = (int)loot.GearSlotsByte,
                ["abilityRollsByte"] = (int)loot.GearAbilityRollsByte,
                ["power"] = (int)loot.GearPower,
                ["crit"] = (int)loot.GearCrit,
                ["formula"] = GearChecks.Formula(loot.GearFormula),
                ["weapon"] = weapons,
                ["armor"] = armor,
            };
        }
    }

    /// <summary>{ forced: name or null, rolls: [7 names] } for one character's weapon or armor.</summary>
    internal static JsonObject Show(RandomizerContext context, MonsterLoot loot, int chr, bool armor)
    {
        ushort Get(int slot) => armor ? loot.ArmorAbility(chr, slot) : loot.WeaponAbility(chr, slot);
        return new JsonObject
        {
            ["forced"] = Get(0) == 0 ? null : context.Names.Ability(Get(0)),
            ["rolls"] = new JsonArray([.. Enumerable.Range(1, MonsterLoot.GearAbilitySlots - 1).Select(i => (JsonNode)context.Names.Ability(Get(i)))]),
        };
    }

    public IEnumerable<string> Validate(RandomizerContext context)
    {
        var s = context.Settings;
        bool sane = s.IsOn("sane");
        var pool = GearAbilityPool.Build(context.Original);
        var entries = (context.Spoiler[EnemyRewardsFeature.Section] as JsonArray)?.OfType<JsonObject>()
            .ToDictionary(e => (int)e["index"]!) ?? [];
        foreach (int index in MonsterFile.AllIndices(context.Original))
        {
            var original = new MonsterFile(index, context.Original.ReadFile(MonsterFile.PathFor(index)));
            if (!original.HasLoot) continue;
            bool expected = !(sane && original.Loot.GearDropChance == 0);
            var entry = entries.GetValueOrDefault(index)?["gear"] as JsonObject;
            if (!expected)
            {
                if (entry is not null) yield return $"m{index:D3}: gear drops changed for an enemy that drops none.";
                continue;
            }
            if (entry is null || !context.ChangedFiles.TryGetValue(MonsterFile.PathFor(index), out byte[]? bytes))
            {
                yield return $"m{index:D3}: gear drops weren't randomized.";
                continue;
            }
            var loot = new MonsterFile(index, bytes).Loot;
            var was = original.Loot;
            if (loot.GearPower < 1) yield return $"m{index:D3}: gear power is 0.";
            if (loot.GearFormula == WeaponFormulas.None) yield return $"m{index:D3}: gear has no damage formula.";
            if (sane && (loot.GearDropChance > 101 || loot.GearSlotsByte > 16 || loot.GearAbilityRollsByte > 16 || loot.GearPower > 24))
                yield return $"m{index:D3}: gear values out of the sane range.";
            if (sane && s.IsOn("wstats") && s.IsOn("formula") && was.GearFormula > 0 && !WeaponFormulas.Sane.Contains(loot.GearFormula))
                yield return $"m{index:D3}: gear formula 0x{loot.GearFormula:X2} isn't allowed.";
            for (int chr = 0; chr < MonsterLoot.GearCharacters; chr++)
            {
                if (loot.WeaponAbility(chr, 0) != was.WeaponAbility(chr, 0) || loot.ArmorAbility(chr, 0) != was.ArmorAbility(chr, 0))
                    yield return $"m{index:D3}: forced gear ability changed.";
                for (int slot = 1; slot < MonsterLoot.GearAbilitySlots; slot++)
                    if (!pool.Weapon.Contains(loot.WeaponAbility(chr, slot)) || !pool.Armor.Contains(loot.ArmorAbility(chr, slot)))
                        yield return $"m{index:D3}: gear ability for {KernelFiles.CharacterNames[chr]} isn't one the game uses there.";
            }

            if ((int)entry["chance"]!["to"]! != loot.GearDropChance || (int)entry["slotsByte"]! != loot.GearSlotsByte
                || (int)entry["abilityRollsByte"]! != loot.GearAbilityRollsByte || (int)entry["power"]! != loot.GearPower
                || (int)entry["crit"]! != loot.GearCrit || (int)entry["formula"]!["id"]! != loot.GearFormula)
                yield return $"m{index:D3}: spoiler gear stats don't match the file.";
            for (int chr = 0; chr < MonsterLoot.GearCharacters; chr++)
            {
                string name = KernelFiles.CharacterNames[chr];
                if (entry["weapon"]![name]!.ToJsonString() != Show(context, loot, chr, armor: false).ToJsonString()
                    || entry["armor"]![name]!.ToJsonString() != Show(context, loot, chr, armor: true).ToJsonString())
                    yield return $"m{index:D3}: spoiler gear abilities for {name} don't match the file.";
            }
        }
        foreach (string problem in EnemyChecks.Integrity(context)) yield return problem;
    }
}
