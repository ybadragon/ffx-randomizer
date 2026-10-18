using System.Text.Json.Nodes;
using FfxRandomizer.Core.GameData;

namespace FfxRandomizer.Core.Randomization.Features;

/// <summary>
/// Abilities and slot counts on every weapon and armor in weapon.bin, shop_arms.bin and buki_get.bin.
/// Follows FFXRando's randomizeGearAbilities, but draws weapon and armor abilities from separate pools
/// and never puts the same ability on one piece twice.
/// </summary>
public sealed class GearAbilitiesFeature : IRandomizerFeature
{
    /// <summary>Spoiler section with one entry per changed piece of gear, shared with <see cref="WeaponStatsFeature"/>.</summary>
    public const string Section = GearChecks.Section;

    public bool IsEnabled(RandomizerSettings s) => s.IsOn("gearab");

    public void Apply(RandomizerContext context)
    {
        var pool = GearAbilityPool.Build(context.Original);
        var rng = context.RngFor("gear.abilities");
        foreach (var (table, index, gear) in GearFiles.ReadAll(context.Edit))
        {
            if (GearChecks.Skipped(gear, context.Settings)) continue;
            var before = gear.Abilities;
            byte slotsBefore = gear.Slots;

            // FFXRando's weights: no slots 40%, one 30%, two 20%, three 10%; then 0 up to that many abilities.
            double roll = rng.NextDouble();
            int slots = roll < 0.4 ? 0 : roll < 0.7 ? 1 : roll < 0.9 ? 2 : 3;
            int count = rng.Next(0, slots);
            var options = pool.For(gear.IsArmor);
            var picked = new List<ushort>();
            while (picked.Count < count)
            {
                ushort ability = rng.Pick(options);
                if (!picked.Contains(ability)) picked.Add(ability);
            }
            gear.Slots = (byte)slots;
            for (int slot = 0; slot < GearRecord.MaxSlots; slot++)
                gear.SetAbility(slot, slot < picked.Count ? picked[slot] : GearRecord.EmptyAbility);

            var entry = GearChecks.Entry(context, table, index, gear);
            entry["slots"] = RandomizerContext.ToNode(new { From = slotsBefore, To = gear.Slots });
            entry["abilities"] = new JsonObject
            {
                ["from"] = GearChecks.Names(context, before),
                ["to"] = GearChecks.Names(context, gear.Abilities),
            };
        }
    }

    public IEnumerable<string> Validate(RandomizerContext context)
    {
        var pool = GearAbilityPool.Build(context.Original);
        foreach (var (table, index, gear) in GearFiles.ReadAll(path => context.ChangedFiles[path]))
        {
            if (GearChecks.Skipped(gear, context.Settings)) continue;
            string who = $"{table} #{index}";
            var abilities = gear.Abilities;
            var filled = abilities.TakeWhile(a => a != GearRecord.EmptyAbility).ToList();
            if (gear.Slots > 3) yield return $"{who}: {gear.Slots} slots.";
            if (filled.Count > gear.Slots) yield return $"{who}: {filled.Count} abilities in {gear.Slots} slots.";
            if (abilities.Skip(filled.Count).Any(a => a != GearRecord.EmptyAbility)) yield return $"{who}: gap in its abilities.";
            if (filled.Distinct().Count() != filled.Count) yield return $"{who}: the same ability twice.";
            foreach (ushort ability in filled.Where(a => !pool.For(gear.IsArmor).Contains(a)))
                yield return $"{who}: 0x{ability:X4} isn't a {gear.Kind.ToLowerInvariant()} ability in the game.";
        }
        foreach (string problem in GearChecks.Integrity(context)) yield return problem;
    }
}

/// <summary>Critical hit chance, attack power and damage formula of weapons. Follows FFXRando's randomizeWeapon* functions.</summary>
public sealed class WeaponStatsFeature : IRandomizerFeature
{
    public bool IsEnabled(RandomizerSettings s) => s.IsOn("wstats") && (s.IsOn("crit") || s.IsOn("atk") || s.IsOn("formula"));

    public void Apply(RandomizerContext context)
    {
        var s = context.Settings;
        bool sane = s.IsOn("sane");
        var critRng = context.RngFor("gear.crit");
        var attackRng = context.RngFor("gear.attack");
        var formulaRng = context.RngFor("gear.formula");
        foreach (var (table, index, gear) in GearFiles.ReadAll(context.Edit))
        {
            if (gear.IsArmor || GearChecks.Skipped(gear, s)) continue;
            var entry = GearChecks.Entry(context, table, index, gear);
            if (s.IsOn("crit"))
            {
                byte before = gear.Crit;
                gear.Crit = (byte)(sane ? critRng.Normal(before, 1, 1, 8) : critRng.Next(0, 100));
                entry["crit"] = RandomizerContext.ToNode(new { From = before, To = gear.Crit });
            }
            if (s.IsOn("atk"))
            {
                byte before = gear.Power;
                gear.Power = (byte)(sane ? attackRng.Normal(16, 3, 10, 24) : attackRng.Next(1, 100));
                entry["power"] = RandomizerContext.ToNode(new { From = before, To = gear.Power });
            }
            if (s.IsOn("formula"))
            {
                byte before = gear.Formula;
                gear.Formula = formulaRng.Pick(sane ? WeaponFormulas.Sane : WeaponFormulas.All);
                entry["formula"] = new JsonObject { ["from"] = GearChecks.Formula(before), ["to"] = GearChecks.Formula(gear.Formula) };
            }
        }
    }

    public IEnumerable<string> Validate(RandomizerContext context)
    {
        var s = context.Settings;
        bool sane = s.IsOn("sane");
        foreach (var (table, index, gear) in GearFiles.ReadAll(path => context.ChangedFiles[path]))
        {
            if (gear.IsArmor || GearChecks.Skipped(gear, s)) continue;
            string who = $"{table} #{index}";
            if (s.IsOn("atk") && (gear.Power < 1 || sane && gear.Power is < 10 or > 24)) yield return $"{who}: attack power {gear.Power}.";
            if (s.IsOn("crit") && (gear.Crit > 100 || sane && gear.Crit is < 1 or > 8)) yield return $"{who}: crit {gear.Crit}%.";
            if (s.IsOn("formula") && !(sane ? WeaponFormulas.Sane : WeaponFormulas.All).Contains(gear.Formula))
                yield return $"{who}: formula 0x{gear.Formula:X2} isn't allowed.";
        }
        foreach (string problem in GearChecks.Integrity(context)) yield return problem;
    }
}

/// <summary>Spoiler entries and checks shared by the gear features.</summary>
internal static class GearChecks
{
    public const string Section = "gear";

    /// <summary>
    /// Gear the features leave alone: Celestial weapons and Brotherhood unless included (the Items tab options, as in
    /// FFXRando), the empty placeholder record at the start of shop_arms.bin, and gear that isn't a party member's.
    /// weapon.bin also holds the aeons' gear (characters 8 and up), which carries their status immunities; FFXRando
    /// randomized it along with Celestial weapons.
    /// </summary>
    public static bool Skipped(GearRecord gear, RandomizerSettings s) =>
        !gear.IsPartyGear
        || gear.IsCelestial && !s.IsOn("celestial")
        || gear.IsBrotherhood && !s.IsOn("brotherhood")
        || gear.Formula == WeaponFormulas.None && gear.Power == 0;

    public static JsonObject Entry(RandomizerContext context, string table, int index, GearRecord gear) =>
        context.SpoilerEntry(Section, $"{table}#{index}", new { Table = table, Index = index, For = gear.Who, Type = gear.Kind });

    public static JsonArray Names(RandomizerContext context, IEnumerable<ushort> abilities) =>
        [.. abilities.Where(a => a != GearRecord.EmptyAbility && a != 0).Select(a => (JsonNode)context.Names.Ability(a))];

    public static JsonObject Formula(byte formula) => new() { ["name"] = WeaponFormulas.Name(formula), ["id"] = (int)formula };

    /// <summary>Gear tables keep their size; only formula, power, crit, slots and abilities change; skipped gear doesn't change at all.</summary>
    public static IEnumerable<string> Integrity(RandomizerContext context)
    {
        foreach (var (name, path) in GearFiles.Tables)
        {
            if (!context.ChangedFiles.TryGetValue(path, out byte[]? changed)) continue;
            var before = new KernelTable(context.Original.ReadFile(path));
            var after = new KernelTable(changed);
            if (changed.Length != before.Bytes.Length)
            {
                yield return $"{name}: size changed.";
                continue;
            }
            if (!changed.AsSpan(0, after.DataOffset).SequenceEqual(before.Bytes.AsSpan(0, before.DataOffset))) yield return $"{name}: header changed.";
            for (int i = 0; i < before.RecordCount; i++)
            {
                byte[] was = before.Record(i).ToArray(), now = after.Record(i).ToArray();
                var original = new GearRecord(was);
                HashSet<int> editable = Skipped(original, context.Settings) ? [] : [.. original.EditableOffsets];
                for (int b = 0; b < was.Length; b++)
                    if (was[b] != now[b] && !editable.Contains(b))
                    {
                        yield return $"{name} #{i}: byte 0x{b:X2} changed.";
                        break;
                    }
            }
        }

        if (context.Spoiler[Section] is not JsonArray entries) yield break;
        foreach (var node in entries)
        {
            var entry = (JsonObject)node!;
            string table = (string)entry["table"]!;
            int index = (int)entry["index"]!;
            var gear = new GearRecord(new KernelTable(context.ChangedFiles[GearFiles.Tables.First(t => t.Name == table).Path]).Record(index).ToArray());
            string who = $"{table} #{index}";
            if (entry["abilities"]?["to"] is JsonArray abilities && !abilities.Select(n => (string?)n).SequenceEqual(Names(context, gear.Abilities).Select(n => (string?)n)))
                yield return $"{who}: spoiler abilities don't match the file.";
            if (entry["slots"] is { } slots && (int)slots["to"]! != gear.Slots) yield return $"{who}: spoiler slots don't match the file.";
            if (entry["power"] is { } power && (int)power["to"]! != gear.Power) yield return $"{who}: spoiler power doesn't match the file.";
            if (entry["crit"] is { } crit && (int)crit["to"]! != gear.Crit) yield return $"{who}: spoiler crit doesn't match the file.";
            if (entry["formula"] is { } formula && (int)formula["to"]!["id"]! != gear.Formula) yield return $"{who}: spoiler formula doesn't match the file.";
        }
    }
}
