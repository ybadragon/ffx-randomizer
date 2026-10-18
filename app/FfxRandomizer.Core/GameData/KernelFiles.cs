using System.Buffers.Binary;

namespace FfxRandomizer.Core.GameData;

/// <summary>Battle kernel files under jppc/battle/kernel, the copies the game reads for items, shops and gear.</summary>
public static class KernelFiles
{
    public const string Folder = GamePaths.Master + "jppc/battle/kernel/";
    public const string Treasure = Folder + "takara.bin";
    public const string ItemShops = Folder + "item_shop.bin";
    public const string ItemPrices = Folder + "item_rate.bin";
    public const string Customizations = Folder + "kaizou.bin";
    public const string TreasureGear = Folder + "buki_get.bin";

    public static readonly string[] CharacterNames = ["Tidus", "Yuna", "Auron", "Kimahri", "Wakka", "Lulu", "Rikku"];

    public static ushort U16(ReadOnlySpan<byte> record, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(record[offset..]);
    public static void U16(Span<byte> record, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(record[offset..], value);
}

/// <summary>What one treasure record (takara.bin, 4 bytes: kind, quantity, item) hands out.</summary>
public readonly record struct Treasure(byte Kind, byte Quantity, ushort Item)
{
    public const byte KindGil = 0, KindItem = 2, KindGear = 5, KindKeyItem = 10;

    public static Treasure Read(ReadOnlySpan<byte> record) => new(record[0], record[1], KernelFiles.U16(record, 2));

    public void Write(Span<byte> record)
    {
        record[0] = Kind;
        record[1] = Quantity;
        KernelFiles.U16(record, 2, Item);
    }
}

public static class ItemIds
{
    public const ushort First = 0x2000;
    public const ushort PowerSphere = 0x2046, ManaSphere = 0x2047, SpeedSphere = 0x2048, AbilitySphere = 0x2049;
}
