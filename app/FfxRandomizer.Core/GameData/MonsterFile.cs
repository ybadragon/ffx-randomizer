using System.Buffers.Binary;
using FfxRandomizer.Core.Vbf;

namespace FfxRandomizer.Core.GameData;

/// <summary>
/// A monster file (jppc/battle/mon/_mNNN/mNNN.bin): a table of chunk offsets (count at 0x00, offsets from 0x04),
/// with the stat block in chunk 2 (0x80 bytes) and the loot block in chunk 4 (0x118 bytes).
/// </summary>
public sealed class MonsterFile
{
    public const int StatsLength = 0x80;
    public const int LootLength = 0x118;

    public static string PathFor(int index) => $"{GamePaths.Master}jppc/battle/mon/_m{index:D3}/m{index:D3}.bin";

    /// <summary>Indices of every monster file in the archive, ascending.</summary>
    public static IReadOnlyList<int> AllIndices(VbfArchive archive) =>
        archive.Names
            .Select(n => n.StartsWith(GamePaths.Master + "jppc/battle/mon/_m", StringComparison.Ordinal) && n.EndsWith(".bin", StringComparison.Ordinal)
                ? n[(n.LastIndexOf("/m", StringComparison.Ordinal) + 2)..^4] : null)
            .Where(s => s is { Length: 3 } && s.All(char.IsAsciiDigit))
            .Select(int.Parse!)
            .Order()
            .ToList();

    public MonsterFile(int index, byte[] bytes)
    {
        Index = index;
        Bytes = bytes;
        StatsOffset = ChunkOffset(2, StatsLength);
        LootOffset = ChunkOffset(4, LootLength);
    }

    public int Index { get; }
    public byte[] Bytes { get; }

    /// <summary>-1 when the file has no stat block (placeholder monsters).</summary>
    public int StatsOffset { get; }

    /// <summary>-1 when the file has no loot block.</summary>
    public int LootOffset { get; }

    public bool HasStats => StatsOffset >= 0;
    public bool HasLoot => LootOffset >= 0;

    public MonsterLoot Loot => new(Bytes.AsMemory(LootOffset, LootLength));
    public MonsterStats Stats => new(Bytes.AsMemory(StatsOffset, StatsLength));

    private int ChunkOffset(int chunk, int requiredLength)
    {
        if (Bytes.Length < 8) return -1;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(Bytes);
        if (chunk >= count || 4 + chunk * 4 + 4 > Bytes.Length) return -1;
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(4 + chunk * 4));
        return offset == 0 || offset == uint.MaxValue || offset + requiredLength > Bytes.Length ? -1 : (int)offset;
    }
}

public readonly record struct ItemStack(ushort Item, byte Quantity);

/// <summary>The loot block. Offsets follow FFXRando's enemy_loot_data_t.</summary>
public readonly struct MonsterLoot(Memory<byte> block)
{
    private Span<byte> B => block.Span;

    public ushort Gil { get => U16(0x00); set => U16(0x00, value); }
    public ushort Ap { get => U16(0x02); set => U16(0x02, value); }
    public ushort ApOverkill { get => U16(0x04); set => U16(0x04, value); }
    public byte PrimaryDropChance { get => B[0x08]; set => B[0x08] = value; }
    public byte SecondaryDropChance { get => B[0x09]; set => B[0x09] = value; }
    public byte StealChance { get => B[0x0A]; set => B[0x0A] = value; }
    public byte GearDropChance { get => B[0x0B]; set => B[0x0B] = value; }

    /// <summary>The eight drop slots: normal then overkill; within each, primary, primary rare, secondary, secondary rare.</summary>
    public static readonly IReadOnlyList<(string Name, int ItemOffset, int QuantityOffset)> DropSlots =
    [
        ("primary", 0x0C, 0x14), ("primaryRare", 0x0E, 0x15), ("secondary", 0x10, 0x16), ("secondaryRare", 0x12, 0x17),
        ("overkillPrimary", 0x18, 0x20), ("overkillPrimaryRare", 0x1A, 0x21), ("overkillSecondary", 0x1C, 0x22), ("overkillSecondaryRare", 0x1E, 0x23),
    ];

    public ItemStack Drop(int slot) => new(U16(DropSlots[slot].ItemOffset), B[DropSlots[slot].QuantityOffset]);

    public void SetDrop(int slot, ItemStack stack)
    {
        U16(DropSlots[slot].ItemOffset, stack.Item);
        B[DropSlots[slot].QuantityOffset] = stack.Quantity;
    }

    public ItemStack Steal { get => new(U16(0x24), B[0x28]); set { U16(0x24, value.Item); B[0x28] = value.Quantity; } }
    public ItemStack StealRare { get => new(U16(0x26), B[0x29]); set { U16(0x26, value.Item); B[0x29] = value.Quantity; } }
    public ItemStack Bribe { get => new(U16(0x2A), B[0x2C]); set { U16(0x2A, value.Item); B[0x2C] = value.Quantity; } }
    public byte GilSteal { get => B[0x113]; set => B[0x113] = value; }

    // Equipment drops. The slot and ability bytes are roll counts: about byte/4 slots (1 to 4) and byte/8 ability rolls.
    public const int GearCharacters = 7, GearAbilitySlots = 8;
    public byte GearSlotsByte { get => B[0x2D]; set => B[0x2D] = value; }
    public byte GearFormula { get => B[0x2E]; set => B[0x2E] = value; }
    public byte GearCrit { get => B[0x2F]; set => B[0x2F] = value; }
    public byte GearPower { get => B[0x30]; set => B[0x30] = value; }
    public byte GearAbilityRollsByte { get => B[0x31]; set => B[0x31] = value; }

    /// <summary>
    /// Per character (0x32 + character * 0x20): eight weapon abilities, then eight armor abilities. Slot 0 is always put on
    /// the dropped gear when it isn't 0 (Auron's and Kimahri's Piercing); slots 1-7 are what the ability rolls pick from.
    /// </summary>
    public ushort WeaponAbility(int character, int slot) => U16(0x32 + character * 0x20 + slot * 2);
    public void SetWeaponAbility(int character, int slot, ushort ability) => U16(0x32 + character * 0x20 + slot * 2, ability);
    public ushort ArmorAbility(int character, int slot) => U16(0x42 + character * 0x20 + slot * 2);
    public void SetArmorAbility(int character, int slot, ushort ability) => U16(0x42 + character * 0x20 + slot * 2, ability);

    private ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(B[offset..]);
    private void U16(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(B[offset..], value);
}

[Flags]
public enum Elements : byte { None = 0, Fire = 1, Ice = 2, Lightning = 4, Water = 8, Holy = 16 }

/// <summary>The stat block. Offsets follow FFXRando's enemy_stat_data_t.</summary>
public readonly struct MonsterStats(Memory<byte> block)
{
    public const int AbsorbOffset = 0x2B, ImmuneOffset = 0x2C, ResistOffset = 0x2D, WeaknessOffset = 0x2E;
    public const byte ElementMask = 0x1F;

    private Span<byte> B => block.Span;

    public uint Hp => BinaryPrimitives.ReadUInt32LittleEndian(B[0x14..]);

    public Elements Absorb { get => (Elements)(B[AbsorbOffset] & ElementMask); set => Set(AbsorbOffset, value); }
    public Elements Immune { get => (Elements)(B[ImmuneOffset] & ElementMask); set => Set(ImmuneOffset, value); }
    public Elements Resist { get => (Elements)(B[ResistOffset] & ElementMask); set => Set(ResistOffset, value); }
    public Elements Weakness { get => (Elements)(B[WeaknessOffset] & ElementMask); set => Set(WeaknessOffset, value); }

    // The top three bits of each element byte are not elements; keep them as they were.
    private void Set(int offset, Elements value) => B[offset] = (byte)((B[offset] & ~ElementMask) | ((byte)value & ElementMask));
}
