using System.Text.Json.Nodes;
using FfxRandomizer.Core.GameData;

namespace FfxRandomizer.Core.Randomization.Features;

/// <summary>Randomizes what enemies give: gil, AP, item drops, steals and bribes. Logic follows FFXRando's EnemyDataRandomizer.</summary>
public sealed class EnemyRewardsFeature : IRandomizerFeature
{
    public const string Section = "enemies";

    public bool IsEnabled(RandomizerSettings s) => s.IsOn("rewards") && (s.IsOn("drops") || s.IsOn("steals") || s.IsOn("bribes"));

    public void Apply(RandomizerContext context)
    {
        var s = context.Settings;
        bool sane = s.IsOn("sane");
        var dropsRng = context.RngFor("enemies.drops");
        var stealsRng = context.RngFor("enemies.steals");
        var bribesRng = context.RngFor("enemies.bribes");
        var pool = context.Items.Items;

        ItemStack RandomStack(Rng rng) => new(rng.Pick(pool), (byte)(sane ? rng.Next(1, 4) : rng.Next(1, 99)));
        JsonNode? Show(ItemStack stack) => RandomizerContext.ToNode(new { Item = context.Names.Item(stack.Item), Id = stack.Item, stack.Quantity });

        foreach (int index in MonsterFile.AllIndices(context.Original))
        {
            string path = MonsterFile.PathFor(index);
            if (!new MonsterFile(index, context.Original.ReadFile(path)).HasLoot) continue;
            var loot = new MonsterFile(index, context.Edit(path)).Loot;
            var entry = EnemyEntry(context, index);

            if (s.IsOn("drops"))
            {
                var gil = (From: loot.Gil, To: loot.Gil = Scale(dropsRng, loot.Gil, sane));
                var ap = (From: loot.Ap, To: loot.Ap = Scale(dropsRng, loot.Ap, sane));
                var apOverkill = (From: loot.ApOverkill, To: loot.ApOverkill = Scale(dropsRng, loot.ApOverkill, sane));
                var drops = new JsonObject();
                for (int slot = 0; slot < MonsterLoot.DropSlots.Count; slot++)
                {
                    var stack = RandomStack(dropsRng);
                    loot.SetDrop(slot, stack);
                    drops[MonsterLoot.DropSlots[slot].Name] = Show(stack);
                }
                loot.PrimaryDropChance = Chance(dropsRng, loot.PrimaryDropChance, sane);
                loot.SecondaryDropChance = Chance(dropsRng, loot.SecondaryDropChance, sane);
                drops["chance"] = RandomizerContext.ToNode(new { Primary = loot.PrimaryDropChance, Secondary = loot.SecondaryDropChance });

                entry["gil"] = RandomizerContext.ToNode(new { gil.From, gil.To });
                entry["ap"] = RandomizerContext.ToNode(new { ap.From, ap.To });
                entry["apOverkill"] = RandomizerContext.ToNode(new { apOverkill.From, apOverkill.To });
                entry["drops"] = drops;
            }

            if (s.IsOn("steals"))
            {
                loot.StealChance = 255;
                loot.Steal = RandomStack(stealsRng);
                loot.StealRare = RandomStack(stealsRng);
                if (loot.GilSteal > 0)
                    loot.GilSteal = (byte)(sane ? stealsRng.Normal(loot.GilSteal, loot.GilSteal / 2.0, 1, 255) : stealsRng.Next(1, 255));
                entry["steal"] = new JsonObject
                {
                    ["normal"] = Show(loot.Steal),
                    ["rare"] = Show(loot.StealRare),
                    ["gilMultiplier"] = loot.GilSteal,
                };
            }

            if (s.IsOn("bribes"))
            {
                loot.Bribe = RandomStack(bribesRng);
                entry["bribe"] = Show(loot.Bribe);
            }
        }
    }

    internal static JsonObject EnemyEntry(RandomizerContext context, int index) =>
        context.SpoilerEntry(Section, $"m{index:D3}", new { Monster = context.Names.Monster(index), Index = index });

    private static ushort Scale(Rng rng, ushort value, bool sane)
    {
        if (!sane) return (ushort)rng.Next(1, ushort.MaxValue);
        if (value == 0) return 0;
        return (ushort)rng.Normal(value, value / 2.0, (int)(value * 0.05), Math.Min(value * 3, ushort.MaxValue));
    }

    private static byte Chance(Rng rng, byte value, bool sane) =>
        (byte)(sane && value > 0 ? rng.Normal(value, value / 2.0, 0, 255) : rng.Next(0, 255));

    public IEnumerable<string> Validate(RandomizerContext context)
    {
        var s = context.Settings;
        foreach (int index in MonsterFile.AllIndices(context.Original))
        {
            if (!context.ChangedFiles.TryGetValue(MonsterFile.PathFor(index), out byte[]? bytes)) continue;
            var monster = new MonsterFile(index, bytes);
            if (!monster.HasLoot) continue;
            var loot = monster.Loot;
            var stacks = new List<(string What, ItemStack Stack)>();
            if (s.IsOn("drops"))
                for (int slot = 0; slot < MonsterLoot.DropSlots.Count; slot++) stacks.Add(($"drop {MonsterLoot.DropSlots[slot].Name}", loot.Drop(slot)));
            if (s.IsOn("steals"))
            {
                stacks.Add(("steal", loot.Steal));
                stacks.Add(("rare steal", loot.StealRare));
                if (loot.StealChance != 255) yield return $"m{index:D3}: steal chance is {loot.StealChance}, expected 255.";
            }
            if (s.IsOn("bribes")) stacks.Add(("bribe", loot.Bribe));

            foreach (var (what, stack) in stacks)
            {
                if (!context.Items.Contains(stack.Item) || ItemPool.IsKeyItem(stack.Item))
                    yield return $"m{index:D3}: {what} is item 0x{stack.Item:X4}, which isn't a normal item in the game.";
                if (stack.Quantity is < 1 or > 99)
                    yield return $"m{index:D3}: {what} quantity is {stack.Quantity}.";
            }
        }
        foreach (string problem in EnemyChecks.Integrity(context)) yield return problem;
        foreach (string problem in EnemyChecks.SpoilerMatchesFiles(context)) yield return problem;
    }
}

/// <summary>Checks shared by the enemy features.</summary>
internal static class EnemyChecks
{
    /// <summary>Changed monster files keep their size, and only the loot block and element bytes may differ.</summary>
    public static IEnumerable<string> Integrity(RandomizerContext context)
    {
        foreach (int index in MonsterFile.AllIndices(context.Original))
        {
            string path = MonsterFile.PathFor(index);
            if (!context.ChangedFiles.TryGetValue(path, out byte[]? changed)) continue;
            var original = new MonsterFile(index, context.Original.ReadFile(path));
            if (changed.Length != original.Bytes.Length)
            {
                yield return $"m{index:D3}: file size changed.";
                continue;
            }
            for (int i = 0; i < changed.Length; i++)
            {
                if (changed[i] == original.Bytes[i]) continue;
                bool inLoot = original.HasLoot && i >= original.LootOffset && i < original.LootOffset + MonsterFile.LootLength;
                bool inElements = original.HasStats && i >= original.StatsOffset + MonsterStats.AbsorbOffset && i <= original.StatsOffset + MonsterStats.WeaknessOffset;
                if (!inLoot && !inElements)
                {
                    yield return $"m{index:D3}: byte 0x{i:X} changed outside the loot and element data.";
                    break;
                }
            }
        }
    }

    /// <summary>The spoiler says exactly what the written files contain.</summary>
    public static IEnumerable<string> SpoilerMatchesFiles(RandomizerContext context)
    {
        if (context.Spoiler[EnemyRewardsFeature.Section] is not JsonArray entries) yield break;
        foreach (var node in entries)
        {
            if (node is not JsonObject entry) continue;
            int index = (int)entry["index"]!;
            if (!context.ChangedFiles.TryGetValue(MonsterFile.PathFor(index), out byte[]? bytes))
            {
                yield return $"m{index:D3}: in the spoiler but its file wasn't changed.";
                continue;
            }
            var monster = new MonsterFile(index, bytes);
            if (entry["drops"] is JsonObject drops)
            {
                var loot = monster.Loot;
                for (int slot = 0; slot < MonsterLoot.DropSlots.Count; slot++)
                    if (!Same(drops[MonsterLoot.DropSlots[slot].Name], loot.Drop(slot)))
                        yield return $"m{index:D3}: spoiler drop {MonsterLoot.DropSlots[slot].Name} doesn't match the file.";
                if ((int)entry["gil"]!["to"]! != loot.Gil) yield return $"m{index:D3}: spoiler gil doesn't match the file.";
            }
            if (entry["steal"] is JsonObject steal && (!Same(steal["normal"], monster.Loot.Steal) || !Same(steal["rare"], monster.Loot.StealRare)))
                yield return $"m{index:D3}: spoiler steal doesn't match the file.";
            if (entry["bribe"] is { } bribe && !Same(bribe, monster.Loot.Bribe))
                yield return $"m{index:D3}: spoiler bribe doesn't match the file.";
            if (entry["elements"] is JsonObject elements)
            {
                var stats = monster.Stats;
                if (Flags(elements["weak"]) != stats.Weakness || Flags(elements["resist"]) != stats.Resist
                    || Flags(elements["immune"]) != stats.Immune || Flags(elements["absorb"]) != stats.Absorb)
                    yield return $"m{index:D3}: spoiler elements don't match the file.";
            }
        }
    }

    private static bool Same(JsonNode? shown, ItemStack stack) =>
        shown is not null && (int)shown["id"]! == stack.Item && (int)shown["quantity"]! == stack.Quantity;

    private static Elements Flags(JsonNode? list) =>
        list is JsonArray a ? a.Select(n => Enum.Parse<Elements>((string)n!)).Aggregate(Elements.None, (x, y) => x | y) : Elements.None;
}
