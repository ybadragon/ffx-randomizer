using FfxRandomizer.Core.GameData;

namespace FfxRandomizer.Core.Randomization.Features;

/// <summary>
/// The items needed to add abilities to gear (kaizou.bin: 8-byte records of target, ability, item at 0x04, quantity at 0x06).
/// Follows FFXRando's randomizeCustomizeItems.
/// </summary>
public sealed class CustomizationFeature : IRandomizerFeature
{
    public const string Section = "customizations";

    public bool IsEnabled(RandomizerSettings s) => s.IsOn("custom");

    public void Apply(RandomizerContext context)
    {
        bool sane = context.Settings.IsOn("sane");
        var rng = context.RngFor("items.customizations");
        var table = new KernelTable(context.Edit(KernelFiles.Customizations));
        for (int i = 0; i < table.RecordCount; i++)
        {
            var record = table.Record(i);
            var was = new { Item = context.Names.Item(KernelFiles.U16(record, 4)), Id = KernelFiles.U16(record, 4), Quantity = record[6] };
            ushort item = rng.Pick(context.Items.Items);
            KernelFiles.U16(record, 4, item);
            record[6] = (byte)(sane ? rng.Normal(20, 10, 1, 99) : rng.Next(1, 99));
            context.Spoil(Section, new
            {
                Ability = context.Names.Ability(KernelFiles.U16(record, 2)),
                Was = was,
                Now = new { Item = context.Names.Item(item), Id = item, Quantity = record[6] },
            });
        }
    }

    public IEnumerable<string> Validate(RandomizerContext context)
    {
        var original = new KernelTable(context.Original.ReadFile(KernelFiles.Customizations));
        var table = new KernelTable(context.ChangedFiles[KernelFiles.Customizations]);
        if (table.Bytes.Length != original.Bytes.Length) yield return "kaizou.bin: size changed.";
        for (int i = 0; i < table.RecordCount; i++)
        {
            byte[] before = original.Record(i).ToArray();
            byte[] after = table.Record(i).ToArray();
            ushort item = KernelFiles.U16(after, 4);
            if (!context.Items.Contains(item) || ItemPool.IsKeyItem(item)) yield return $"customization {i}: 0x{item:X4} isn't a normal item.";
            if (after[6] is < 1 or > 99) yield return $"customization {i}: quantity {after[6]}.";
            if (!after.AsSpan(0, 4).SequenceEqual(before.AsSpan(0, 4)) || after[7] != before[7]) yield return $"customization {i}: changed more than its item and quantity.";
        }
    }
}
