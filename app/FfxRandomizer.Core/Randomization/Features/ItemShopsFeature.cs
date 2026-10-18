using System.Text.Json.Nodes;
using FfxRandomizer.Core.GameData;

namespace FfxRandomizer.Core.Randomization.Features;

/// <summary>
/// Item shops (item_shop.bin: 34-byte records, 16 item slots from 0x02) and item prices (item_rate.bin: one u32 per item).
/// Follows FFXRando's randomizeItemShops and randomizeItemPrices.
/// </summary>
public sealed class ItemShopsFeature : IRandomizerFeature
{
    public const string ShopSection = "itemShops";
    public const string PriceSection = "itemPrices";
    private const int Slots = 16;
    private static readonly ushort[] Spheres = [ItemIds.PowerSphere, ItemIds.ManaSphere, ItemIds.SpeedSphere, ItemIds.AbilitySphere];

    public bool IsEnabled(RandomizerSettings s) => s.IsOn("shops");

    public void Apply(RandomizerContext context)
    {
        var s = context.Settings;
        RandomizeStock(context, s.IsOn("spheres"));
        if (s.IsOn("shopprices")) RandomizePrices(context, s.IsOn("sane"));
    }

    private static void RandomizeStock(RandomizerContext context, bool spheres)
    {
        var rng = context.RngFor("items.shops");
        var table = new KernelTable(context.Edit(KernelFiles.ItemShops));
        for (int shop = 0; shop < table.RecordCount; shop++)
        {
            var record = table.Record(shop);
            int count = rng.Next(spheres ? Spheres.Length : 1, Slots);
            var stock = new List<ushort>();
            if (spheres) stock.AddRange(Spheres);
            while (stock.Count < count)
            {
                ushort item = rng.Pick(context.Items.Items);
                if (!stock.Contains(item)) stock.Add(item);
            }
            for (int slot = 0; slot < Slots; slot++)
                KernelFiles.U16(record, 2 + slot * 2, slot < stock.Count ? stock[slot] : (ushort)0);
            context.Spoil(ShopSection, new { Shop = shop, Items = stock.Select(i => context.Names.Item(i)).ToList() });
        }
    }

    private static void RandomizePrices(RandomizerContext context, bool sane)
    {
        var rng = context.RngFor("items.prices");
        var table = new KernelTable(context.Edit(KernelFiles.ItemPrices));
        for (int i = 0; i < table.RecordCount; i++)
        {
            uint before = table.ReadUInt32(i, 0);
            uint after = (uint)(sane && before > 1 && before != 2 ? rng.Normal(before, before, 1, ushort.MaxValue)
                // Spheres cost 2 in the data because no shop sells them; give them a real price if one does now.
                : sane && before == 2 ? rng.Normal(1000, 500, 250, ushort.MaxValue)
                : rng.Next(1, ushort.MaxValue));
            table.WriteUInt32(i, 0, after);
            context.Spoil(PriceSection, new { Item = context.Names.Item(ItemIds.First + i), From = before, To = after });
        }
    }

    public IEnumerable<string> Validate(RandomizerContext context)
    {
        var s = context.Settings;
        var table = new KernelTable(context.ChangedFiles[KernelFiles.ItemShops]);
        var spoiler = context.Spoiler[ShopSection] as JsonArray;
        for (int shop = 0; shop < table.RecordCount; shop++)
        {
            byte[] record = table.Record(shop).ToArray();
            var stock = Enumerable.Range(0, Slots).Select(slot => KernelFiles.U16(record, 2 + slot * 2)).ToList();
            var filled = stock.TakeWhile(i => i != 0).ToList();
            if (filled.Count == 0) yield return $"shop {shop}: sells nothing.";
            if (stock.Skip(filled.Count).Any(i => i != 0)) yield return $"shop {shop}: gap in its item list.";
            if (filled.Distinct().Count() != filled.Count) yield return $"shop {shop}: sells the same item twice.";
            foreach (ushort item in filled.Where(i => !context.Items.Contains(i) || ItemPool.IsKeyItem(i)))
                yield return $"shop {shop}: 0x{item:X4} isn't a normal item.";
            if (s.IsOn("spheres") && !filled.Take(Spheres.Length).SequenceEqual(Spheres)) yield return $"shop {shop}: doesn't start with the four spheres.";
            if (spoiler?[shop]?["items"] is JsonArray shown
                && !shown.Select(n => (string?)n).SequenceEqual(filled.Select(i => context.Names.Item(i))))
                yield return $"shop {shop}: spoiler doesn't match the file.";
        }

        if (s.IsOn("shopprices"))
        {
            var prices = new KernelTable(context.ChangedFiles[KernelFiles.ItemPrices]);
            for (int i = 0; i < prices.RecordCount; i++)
            {
                uint price = prices.ReadUInt32(i, 0);
                if (price is < 1 or > ushort.MaxValue) yield return $"price of {context.Names.Item(ItemIds.First + i)} is {price}.";
            }
        }
    }
}
