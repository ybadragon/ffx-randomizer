using System.Text.Json.Nodes;
using FfxRandomizer.Core.GameData;

namespace FfxRandomizer.Core.Randomization.Features;

/// <summary>
/// Randomizes what chests and field pickups give (takara.bin), following FFXRando's randomizeFieldItems:
/// 50% item, 30% gear, 20% gil, or 40/30/20/10 with key items included.
/// </summary>
public sealed class TreasureFeature : IRandomizerFeature
{
    public const string Section = "treasure";

    /// <summary>Records that aren't really treasure (FFXRando's 177 and 203).</summary>
    private static readonly int[] NotTreasure = [177, 203];
    private static readonly int[] Celestial = [5, 93, 99, 113, 114, 188, 214];
    private static readonly int[] Brotherhood = [207, 208];

    /// <summary>The Withered Bouquet and Flint in Baaj Temple: both are needed to light the fire and move on.</summary>
    private static readonly int[] StoryCritical = [2, 3];

    public bool IsEnabled(RandomizerSettings s) => s.IsOn("field");

    public static HashSet<int> Protected(RandomizerSettings s)
    {
        var keep = new HashSet<int>(NotTreasure.Concat(StoryCritical));
        if (!s.IsOn("celestial")) keep.UnionWith(Celestial);
        if (!s.IsOn("brotherhood")) keep.UnionWith(Brotherhood);
        return keep;
    }

    public void Apply(RandomizerContext context)
    {
        var s = context.Settings;
        bool sane = s.IsOn("sane"), keyItems = s.IsOn("keyitems");
        var rng = context.RngFor("items.treasure");
        var original = new KernelTable(context.Original.ReadFile(KernelFiles.Treasure));
        var table = new KernelTable(context.Edit(KernelFiles.Treasure));
        int gearCount = new KernelTable(context.Original.ReadFile(KernelFiles.TreasureGear)).RecordCount;
        var keep = Protected(s);
        var unplacedKeyItems = new List<ushort>(context.Items.KeyItems);

        for (int i = 0; i < table.RecordCount; i++)
        {
            if (keep.Contains(i)) continue;
            var before = Treasure.Read(original.Record(i));
            if (before.Kind == Treasure.KindKeyItem && !keyItems) continue;

            int roll = rng.Next(0, 99);
            Treasure after;
            if (roll < (keyItems ? 40 : 50)) after = RandomItem(context, rng, sane);
            else if (roll < (keyItems ? 70 : 80)) after = new Treasure(Treasure.KindGear, 1, (ushort)rng.Next(0, gearCount - 1));
            else if (roll < (keyItems ? 90 : 100)) after = new Treasure(Treasure.KindGil, (byte)rng.Normal(15, 5, 1, 255), 0);
            else if (unplacedKeyItems.Count > 0) after = TakeKeyItem(rng, unplacedKeyItems);
            else after = RandomItem(context, rng, sane);
            after.Write(table.Record(i));
        }

        // With key items included, every key item must still be somewhere in the game.
        if (keyItems)
        {
            var candidates = Enumerable.Range(0, table.RecordCount)
                .Where(i => !keep.Contains(i) && Treasure.Read(table.Record(i)).Kind != Treasure.KindKeyItem).ToList();
            while (unplacedKeyItems.Count > 0 && candidates.Count > 0)
            {
                int pick = rng.Next(0, candidates.Count - 1);
                TakeKeyItem(rng, unplacedKeyItems).Write(table.Record(candidates[pick]));
                candidates.RemoveAt(pick);
            }
        }

        for (int i = 0; i < table.RecordCount; i++)
        {
            var before = Treasure.Read(original.Record(i));
            var after = Treasure.Read(table.Record(i));
            if (before != after)
                context.Spoil(Section, new { Index = i, Was = Describe(context, before), Now = Describe(context, after) });
        }
    }

    private static Treasure RandomItem(RandomizerContext context, Rng rng, bool sane)
    {
        ushort item = rng.Pick(context.Items.Items);
        return new Treasure(Treasure.KindItem, (byte)context.Items.RandomQuantity(rng, item, sane), item);
    }

    private static Treasure TakeKeyItem(Rng rng, List<ushort> unplaced)
    {
        int pick = rng.Next(0, unplaced.Count - 1);
        ushort item = unplaced[pick];
        unplaced.RemoveAt(pick);
        return new Treasure(Treasure.KindKeyItem, 1, item);
    }

    internal static object Describe(RandomizerContext context, Treasure t) => t.Kind switch
    {
        Treasure.KindGil => new { Gil = t.Quantity * 100 },
        Treasure.KindItem => new { Item = context.Names.Item(t.Item), Id = t.Item, t.Quantity },
        Treasure.KindKeyItem => new { KeyItem = context.Names.Item(t.Item), Id = t.Item },
        Treasure.KindGear => DescribeGear(context, t.Item),
        _ => new { Kind = t.Kind, t.Quantity, Id = t.Item },
    };

    private static object DescribeGear(RandomizerContext context, int index)
    {
        var record = new KernelTable(context.Original.ReadFile(KernelFiles.TreasureGear)).Record(index);
        string who = record[1] < KernelFiles.CharacterNames.Length ? KernelFiles.CharacterNames[record[1]] : $"Character {record[1]}";
        return new { Gear = index, For = who, Type = record[2] == 1 ? "Armor" : "Weapon" };
    }

    public IEnumerable<string> Validate(RandomizerContext context)
    {
        var s = context.Settings;
        var original = new KernelTable(context.Original.ReadFile(KernelFiles.Treasure));
        var table = new KernelTable(context.ChangedFiles[KernelFiles.Treasure]);
        int gearCount = new KernelTable(context.Original.ReadFile(KernelFiles.TreasureGear)).RecordCount;
        if (table.Bytes.Length != original.Bytes.Length || !table.Bytes.AsSpan(0, table.DataOffset).SequenceEqual(original.Bytes.AsSpan(0, original.DataOffset)))
            yield return "takara.bin: header or size changed.";
        var keep = Protected(s);

        for (int i = 0; i < table.RecordCount; i++)
        {
            var before = Treasure.Read(original.Record(i));
            var after = Treasure.Read(table.Record(i));
            if (keep.Contains(i) && before != after) yield return $"treasure {i}: protected treasure was changed.";
            if (!s.IsOn("keyitems") && before.Kind == Treasure.KindKeyItem && before != after) yield return $"treasure {i}: key item changed with key items excluded.";
            if (before == after) continue;
            switch (after.Kind)
            {
                case Treasure.KindItem when !context.Items.Contains(after.Item) || ItemPool.IsKeyItem(after.Item) || after.Quantity is < 1 or > 99:
                    yield return $"treasure {i}: bad item {after.Quantity}x 0x{after.Item:X4}.";
                    break;
                case Treasure.KindKeyItem when !context.Items.KeyItems.Contains(after.Item):
                    yield return $"treasure {i}: 0x{after.Item:X4} isn't a key item.";
                    break;
                case Treasure.KindGear when after.Item >= gearCount:
                    yield return $"treasure {i}: gear #{after.Item} doesn't exist.";
                    break;
                case Treasure.KindGil when after.Quantity < 1:
                    yield return $"treasure {i}: no gil.";
                    break;
                case not (Treasure.KindItem or Treasure.KindKeyItem or Treasure.KindGear or Treasure.KindGil):
                    yield return $"treasure {i}: unknown kind {after.Kind}.";
                    break;
            }
        }

        if (s.IsOn("keyitems"))
        {
            var placed = Enumerable.Range(0, table.RecordCount).Select(i => Treasure.Read(table.Record(i)))
                .Where(t => t.Kind == Treasure.KindKeyItem).Select(t => t.Item).ToHashSet();
            foreach (ushort key in context.Items.KeyItems.Where(k => !placed.Contains(k)))
                yield return $"key item 0x{key:X4} ({context.Names.Item(key)}) isn't anywhere in the game any more.";
        }

        // The spoiler must describe exactly what was written.
        if (context.Spoiler[Section] is JsonArray entries)
            foreach (var entry in entries)
            {
                int i = (int)entry!["index"]!;
                string shown = entry["now"]!.ToJsonString();
                string actual = RandomizerContext.ToNode(Describe(context, Treasure.Read(table.Record(i))))!.ToJsonString();
                if (shown != actual) yield return $"treasure {i}: spoiler says {shown} but the file has {actual}.";
            }
    }
}
