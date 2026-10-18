using FfxRandomizer.Core.Vbf;

namespace FfxRandomizer.Core.GameData;

/// <summary>
/// Every item the game hands out somewhere (treasure, shops, monster loot), the pool random rewards are drawn from.
/// Built the same way as FFXRando's item lists.
/// </summary>
public sealed class ItemPool
{
    private const string Kernel = GamePaths.Master + "jppc/battle/kernel/";
    public const string TreasureFile = Kernel + "takara.bin";
    public const string ItemShopFile = Kernel + "item_shop.bin";

    public IReadOnlyList<ushort> Items { get; }
    public IReadOnlyList<ushort> KeyItems { get; }

    private readonly HashSet<ushort> _all;

    private ItemPool(SortedSet<ushort> items, SortedSet<ushort> keyItems)
    {
        Items = items.ToList();
        KeyItems = keyItems.ToList();
        _all = [.. items, .. keyItems];
    }

    public bool Contains(ushort item) => _all.Contains(item);

    public static bool IsKeyItem(ushort item) => item > 9000;

    public static ItemPool Build(VbfArchive original)
    {
        var items = new SortedSet<ushort>();
        var keyItems = new SortedSet<ushort>();
        void Add(ushort item, bool key)
        {
            if (item == 0) return;
            (key || IsKeyItem(item) ? keyItems : items).Add(item);
        }

        // Treasure: 4-byte records of flag, quantity, item. Flag 2 is an item, 10 a key item; 177 and 203 aren't items.
        var treasure = new KernelTable(original.ReadFile(TreasureFile));
        for (int i = 0; i < treasure.RecordCount; i++)
        {
            var r = treasure.Record(i);
            byte flag = r[0];
            ushort item = (ushort)(r[2] | r[3] << 8);
            if ((flag == 2 || flag == 10) && item != 203 && item != 177) Add(item, flag == 10);
        }

        // Item shops: 34-byte records holding 16 item ids from 0x02.
        var shops = new KernelTable(original.ReadFile(ItemShopFile));
        for (int i = 0; i < shops.RecordCount; i++)
        {
            var r = shops.Record(i);
            for (int slot = 0; slot < 16; slot++) Add((ushort)(r[2 + slot * 2] | r[3 + slot * 2] << 8), false);
        }

        foreach (int index in MonsterFile.AllIndices(original))
        {
            var monster = new MonsterFile(index, original.ReadFile(MonsterFile.PathFor(index)));
            if (!monster.HasLoot) continue;
            var loot = monster.Loot;
            for (int slot = 0; slot < MonsterLoot.DropSlots.Count; slot++) Add(loot.Drop(slot).Item, false);
            Add(loot.Steal.Item, false);
            Add(loot.StealRare.Item, false);
            Add(loot.Bribe.Item, false);
        }

        return new ItemPool(items, keyItems);
    }
}
