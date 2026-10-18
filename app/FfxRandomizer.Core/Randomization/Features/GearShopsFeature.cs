using System.Text.Json.Nodes;
using FfxRandomizer.Core.GameData;

namespace FfxRandomizer.Core.Randomization.Features;

/// <summary>
/// Weapon and armor shops (arms_shop.bin: 34-byte records, 16 shop_arms.bin indexes from 0x02, 0 ends the list) and gear
/// prices (arms_rate.bin: one u32 per auto-ability; a piece of gear costs the sum of its abilities' prices).
/// Follows FFXRando's randomizeGearShops and randomizeArmsPrices, except that a shop never lists the same gear twice and
/// never lists shop_arms.bin's empty first record.
/// </summary>
public sealed class GearShopsFeature : IRandomizerFeature
{
    public const string ShopSection = "gearShops";
    public const string PriceSection = "gearPrices";
    private const int Slots = 16;

    public bool IsEnabled(RandomizerSettings s) => s.IsOn("wshops");

    public void Apply(RandomizerContext context)
    {
        RandomizeStock(context);
        if (context.Settings.IsOn("wprices")) RandomizePrices(context, context.Settings.IsOn("sane"));
    }

    /// <summary>shop_arms.bin indexes a shop may sell: everything but empty placeholder records.</summary>
    private static List<ushort> Sellable(byte[] shopGear)
    {
        var table = new KernelTable(shopGear);
        return Enumerable.Range(1, table.RecordCount - 1)
            .Where(i => new GearRecord(table.Record(i).ToArray()) is var g && !(g.Formula == WeaponFormulas.None && g.Power == 0))
            .Select(i => (ushort)i).ToList();
    }

    private static byte[] Current(RandomizerContext context, string path) =>
        context.ChangedFiles.TryGetValue(path, out byte[]? bytes) ? bytes : context.Original.ReadFile(path);

    private static void RandomizeStock(RandomizerContext context)
    {
        var rng = context.RngFor("gear.shops");
        var sellable = Sellable(context.Original.ReadFile(GearFiles.ShopGear));
        var table = new KernelTable(context.Edit(GearFiles.Shops));
        var gear = new KernelTable(Current(context, GearFiles.ShopGear));
        for (int shop = 0; shop < table.RecordCount; shop++)
        {
            var record = table.Record(shop);
            int count = rng.Next(1, Slots);
            var stock = new List<ushort>();
            while (stock.Count < count)
            {
                ushort index = rng.Pick(sellable);
                if (!stock.Contains(index)) stock.Add(index);
            }
            for (int slot = 0; slot < Slots; slot++)
                KernelFiles.U16(record, 2 + slot * 2, slot < stock.Count ? stock[slot] : (ushort)0);
            context.Spoil(ShopSection, new { Shop = shop, Gear = stock.Select(i => Describe(context, gear, i)).ToList() });
        }
    }

    internal static object Describe(RandomizerContext context, KernelTable shopGear, int index)
    {
        var g = new GearRecord(shopGear.Record(index).ToArray());
        return new
        {
            Index = index,
            For = g.Who,
            Type = g.Kind,
            Abilities = g.Abilities.Where(a => a != GearRecord.EmptyAbility && a != 0).Select(a => context.Names.Ability(a)).ToList(),
        };
    }

    private static void RandomizePrices(RandomizerContext context, bool sane)
    {
        var rng = context.RngFor("gear.prices");
        var table = new KernelTable(context.Edit(GearFiles.AbilityPrices));
        for (int i = 0; i < table.RecordCount; i++)
        {
            uint before = table.ReadUInt32(i, 0);
            uint after = (uint)(sane && before > 1 ? rng.Normal(before, before, 1, ushort.MaxValue) : rng.Next(1, ushort.MaxValue));
            table.WriteUInt32(i, 0, after);
            context.Spoil(PriceSection, new { Ability = context.Names.Ability(GearAbilityPool.First + i), From = before, To = after });
        }
    }

    public IEnumerable<string> Validate(RandomizerContext context)
    {
        var sellable = Sellable(context.Original.ReadFile(GearFiles.ShopGear)).ToHashSet();
        var table = new KernelTable(context.ChangedFiles[GearFiles.Shops]);
        var original = new KernelTable(context.Original.ReadFile(GearFiles.Shops));
        var gear = new KernelTable(Current(context, GearFiles.ShopGear));
        var spoiler = context.Spoiler[ShopSection] as JsonArray;
        if (table.Bytes.Length != original.Bytes.Length) yield return "arms_shop.bin: size changed.";
        for (int shop = 0; shop < table.RecordCount; shop++)
        {
            byte[] record = table.Record(shop).ToArray();
            var stock = Enumerable.Range(0, Slots).Select(slot => KernelFiles.U16(record, 2 + slot * 2)).ToList();
            var filled = stock.TakeWhile(i => i != 0).ToList();
            if (filled.Count == 0) yield return $"gear shop {shop}: sells nothing.";
            if (stock.Skip(filled.Count).Any(i => i != 0)) yield return $"gear shop {shop}: gap in its list.";
            if (filled.Distinct().Count() != filled.Count) yield return $"gear shop {shop}: sells the same gear twice.";
            foreach (ushort index in filled.Where(i => !sellable.Contains(i))) yield return $"gear shop {shop}: shop_arms #{index} isn't sellable gear.";
            if (KernelFiles.U16(record, 0) != KernelFiles.U16(original.Record(shop), 0)) yield return $"gear shop {shop}: header changed.";
            var shown = spoiler?[shop]?["gear"] as JsonArray;
            if (shown is null || shown.Count != filled.Count
                || filled.Where((index, slot) => RandomizerContext.ToNode(Describe(context, gear, index))!.ToJsonString() != shown[slot]!.ToJsonString()).Any())
                yield return $"gear shop {shop}: spoiler doesn't match the file.";
        }

        if (context.Settings.IsOn("wprices"))
        {
            var prices = new KernelTable(context.ChangedFiles[GearFiles.AbilityPrices]);
            for (int i = 0; i < prices.RecordCount; i++)
            {
                uint price = prices.ReadUInt32(i, 0);
                if (price is < 1 or > ushort.MaxValue) yield return $"price of {context.Names.Ability(GearAbilityPool.First + i)} is {price}.";
            }
        }
    }
}
