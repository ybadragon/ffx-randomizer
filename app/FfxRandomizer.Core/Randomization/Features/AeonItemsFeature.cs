using FfxRandomizer.Core.GameData;

namespace FfxRandomizer.Core.Randomization.Features;

/// <summary>
/// The items that teach aeons abilities or raise their stats (sum_grow.bin: 8-byte records of target, ability,
/// item at 0x04, quantity at 0x06, base at 0x07). Follows FFXRando's randomizeSumGrow for abilities. For the stat boosts
/// (ability 0-9) byte 0x06 is how much the stat rises, not a cost, so only the item changes there; FFXRando set it to 1,
/// which turned "HP +100" into "HP +1".
/// </summary>
public sealed class AeonItemsFeature : IRandomizerFeature
{
    public const string Section = "aeonItems";
    private const ushort LastStat = 9;

    public bool IsEnabled(RandomizerSettings s) => s.IsOn("aitems");

    public static string StatName(ushort ability) =>
        ability switch { 0 => "HP", 1 => "MP", 2 => "Strength", 3 => "Defense", 4 => "Magic", 5 => "Magic Defense", 6 => "Agility", 7 => "Luck", 8 => "Evasion", 9 => "Accuracy", _ => "?" };

    public void Apply(RandomizerContext context)
    {
        bool sane = context.Settings.IsOn("sane");
        var rng = context.RngFor("aeons.items");
        var table = new KernelTable(context.Edit(AeonFiles.SumGrow));
        for (int i = 0; i < table.RecordCount; i++)
        {
            var record = table.Record(i);
            ushort ability = KernelFiles.U16(record, 2), wasItem = KernelFiles.U16(record, 4);
            bool stat = ability <= LastStat;
            int wasQuantity = stat ? record[7] : record[6];
            ushort item = rng.Pick(context.Items.Items);
            KernelFiles.U16(record, 4, item);
            if (!stat) record[6] = (byte)(sane ? rng.Normal(16, 10, 1, 99) : rng.Next(1, 99));
            int quantity = stat ? record[7] : record[6];
            context.Spoil(Section, new
            {
                Index = i,
                Teaches = stat ? $"{StatName(ability)} +{record[6]}" : context.Names.Command(ability),
                Was = new { Item = context.Names.Item(wasItem), Id = wasItem, Quantity = wasQuantity },
                Now = new { Item = context.Names.Item(item), Id = item, Quantity = quantity },
            });
        }
    }

    public IEnumerable<string> Validate(RandomizerContext context)
    {
        var original = new KernelTable(context.Original.ReadFile(AeonFiles.SumGrow));
        var table = new KernelTable(context.ChangedFiles[AeonFiles.SumGrow]);
        if (table.Bytes.Length != original.Bytes.Length) yield return "sum_grow.bin: size changed.";
        for (int i = 0; i < table.RecordCount; i++)
        {
            byte[] before = original.Record(i).ToArray(), after = table.Record(i).ToArray();
            ushort item = KernelFiles.U16(after, 4);
            bool stat = KernelFiles.U16(before, 2) <= LastStat;
            if (!context.Items.Contains(item) || ItemPool.IsKeyItem(item)) yield return $"aeon item {i}: 0x{item:X4} isn't a normal item.";
            if (after[6] is < 1 or > 99 && !stat) yield return $"aeon item {i}: quantity {after[6]}.";
            if (!after.AsSpan(0, 4).SequenceEqual(before.AsSpan(0, 4)) || after[7] != before[7] || stat && after[6] != before[6])
                yield return $"aeon item {i}: changed more than its item and quantity.";
        }
    }
}
