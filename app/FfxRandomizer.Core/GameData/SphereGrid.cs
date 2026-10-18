using System.Buffers.Binary;

namespace FfxRandomizer.Core.GameData;

/// <summary>
/// One of the three Sphere Grids in jppc/menu/abmap. The layout file (dat01-03) holds a 16-byte header (cluster count at
/// 0x02, node count at 0x04), 16-byte clusters, 12-byte nodes and links; the contents file (dat09-11) holds an 8-byte header
/// followed by one node type byte per node, in layout order. The game reads node types from the contents file only, and
/// copies them into a save when a new game starts.
/// </summary>
public sealed record SphereGridFile(string Name, string LayoutPath, string ContentsPath)
{
    private const string Folder = GamePaths.Master + "jppc/menu/abmap/";
    public const int ContentsHeader = 8;

    /// <summary>The PS2 grid (unused by the HD menus), the Standard grid and the Expert grid.</summary>
    public static readonly IReadOnlyList<SphereGridFile> All =
    [
        new("Original", Folder + "dat01.dat", Folder + "dat09.dat"),
        new("Standard", Folder + "dat02.dat", Folder + "dat10.dat"),
        new("Expert", Folder + "dat03.dat", Folder + "dat11.dat"),
    ];

    public static int NodeCount(byte[] layout) => BinaryPrimitives.ReadUInt16LittleEndian(layout.AsSpan(0x04));
}

/// <summary>Sphere Grid node types (rows of battle/kernel/panel.bin) and the starting-ability bits in ply_save.bin.</summary>
public static class SphereNodes
{
    public const byte Lock3 = 0x00;
    public const byte Empty = 0x01;
    public const byte FirstStat = 0x02;
    public const byte LastStat = 0x26;
    public const byte Hp200 = 0x22, Hp300 = 0x23, Mp40 = 0x24, Mp20 = 0x25, Mp10 = 0x26;
    public const byte Lock1 = 0x27, Lock2 = 0x28, Lock4 = 0x29;
    public const byte FirstAbility = 0x2A;
    public const byte LastAbility = 0x7E;
    public const byte Steal = 0x3A, Use = 0x3B;

    public static bool IsLock(byte node) => node is Lock3 or Lock1 or Lock2 or Lock4;
    public static bool IsStat(byte node) => node is >= FirstStat and <= LastStat;
    public static bool IsAbility(byte node) => node is >= FirstAbility and <= LastAbility;

    /// <summary>
    /// Whether the randomizer may change a node. Nulls (0xFF) and the two Expert-grid nodes typed 0x7F and 0x81, which
    /// have no row in panel.bin, are left alone.
    /// </summary>
    public static bool IsRegular(byte node) => node <= LastAbility;

    /// <summary>The strongest node of the same stat: +4 for the eight +1 to +4 stats, HP +300 and MP +40.</summary>
    public static byte Upgrade(byte node) => node switch
    {
        >= 0x02 and <= 0x21 => (byte)(node - (node - 0x02) % 4 + 3),
        Hp200 => Hp300,
        Mp20 or Mp10 => Mp40,
        _ => node,
    };

    /// <summary>The weakest node of the same stat: +1 for the eight +1 to +4 stats, HP +200 and MP +10.</summary>
    public static byte Downgrade(byte node) => node switch
    {
        >= 0x02 and <= 0x21 => (byte)(node - (node - 0x02) % 4),
        Hp300 => Hp200,
        Mp40 or Mp20 => Mp10,
        _ => node,
    };

    /// <summary>English names, matching panel.bin.</summary>
    public static readonly IReadOnlyList<string> Names =
    [
        "Lv. 3 Lock", "Empty Node",
        "Strength +1", "Strength +2", "Strength +3", "Strength +4",
        "Defense +1", "Defense +2", "Defense +3", "Defense +4",
        "Magic +1", "Magic +2", "Magic +3", "Magic +4",
        "Magic Defense +1", "Magic Defense +2", "Magic Defense +3", "Magic Defense +4",
        "Agility +1", "Agility +2", "Agility +3", "Agility +4",
        "Luck +1", "Luck +2", "Luck +3", "Luck +4",
        "Evasion +1", "Evasion +2", "Evasion +3", "Evasion +4",
        "Accuracy +1", "Accuracy +2", "Accuracy +3", "Accuracy +4",
        "HP +200", "HP +300", "MP +40", "MP +20", "MP +10",
        "Lv. 1 Lock", "Lv. 2 Lock", "Lv. 4 Lock",
        "Delay Attack", "Delay Buster", "Sleep Attack", "Silence Attack", "Dark Attack", "Zombie Attack",
        "Sleep Buster", "Silence Buster", "Dark Buster", "Triple Foul",
        "Power Break", "Magic Break", "Armor Break", "Mental Break", "Mug", "Quick Hit", "Steal", "Use",
        "Flee", "Pray", "Cheer", "Focus", "Reflex", "Aim", "Luck", "Jinx", "Lancet", "Guard", "Sentinel",
        "Spare Change", "Threaten", "Provoke", "Entrust", "Copycat", "Doublecast", "Bribe",
        "Cure", "Cura", "Curaga", "NulFrost", "NulBlaze", "NulShock", "NulTide", "Scan", "Esuna", "Life", "Full-Life",
        "Haste", "Hastega", "Slow", "Slowga", "Shell", "Protect", "Reflect", "Dispel", "Regen", "Holy", "Auto-Life",
        "Blizzard", "Fire", "Thunder", "Water", "Fira", "Blizzara", "Thundara", "Watera",
        "Firaga", "Blizzaga", "Thundaga", "Waterga", "Bio", "Demi", "Death", "Drain", "Osmose", "Flare", "Ultima",
        "Pilfer Gil", "Full Break", "Extract Power", "Extract Mana", "Extract Speed", "Extract Ability", "Nab Gil", "Quick Pockets",
    ];

    public static string Name(byte node) => node < Names.Count ? Names[node] : $"Node 0x{node:X2}";

    /// <summary>
    /// Learned battle commands in a ply_save.bin record: three u32 bit fields from 0x3E, bit n meaning command 0x3000 + n.
    /// </summary>
    public const int LearnedCommands = 0x3E;

    public const int DarkAttackCommand = 0x300A, StealCommand = 0x3016, UseCommand = 0x3017, LancetCommand = 0x3020, ThunderCommand = 0x3043;

    public static bool Knows(ReadOnlySpan<byte> record, int command) =>
        (record[LearnedCommands + (command - 0x3000) / 8] & (1 << ((command - 0x3000) % 8))) != 0;

    public static void Learn(Span<byte> record, int command) =>
        record[LearnedCommands + (command - 0x3000) / 8] |= (byte)(1 << ((command - 0x3000) % 8));
}
