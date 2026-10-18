namespace FfxRandomizer.Core.GameData;

/// <summary>Paths of game files inside FFX_Data.vbf.</summary>
public static class GamePaths
{
    public const string Master = "ffx_ps2/ffx/master/";

    /// <summary>The per-language folders whose battle kernels the game reads for party data.</summary>
    public static readonly IReadOnlyList<string> LanguageFolders =
        ["new_chpc", "new_depc", "new_frpc", "new_itpc", "new_jppc", "new_krpc", "new_sppc", "new_uspc"];

    public static string LanguageKernel(string language, string file) => $"{Master}{language}/battle/kernel/{file}";
}

/// <summary>
/// Byte offsets inside one ply_save.bin record (148 bytes per character; records 0-6 are the party, 8-17 the aeons).
/// Every language has its own copy; they differ only in the name offset at 0x00.
/// </summary>
public static class PlySave
{
    public const string FileName = "ply_save.bin";
    public const int Tidus = 0, Yuna = 1, Auron = 2, Kimahri = 3, Wakka = 4, Lulu = 5, Rikku = 6;
    public const int PartySize = 7;

    public const int BaseHp = 0x04;
    public const int BaseMp = 0x08;
    /// <summary>Base Strength, Defense, Magic, Magic Defense, Agility, Luck, Evasion, Accuracy: one byte each.</summary>
    public const int BaseStr = 0x0C, BaseDef = 0x0D, BaseMag = 0x0E, BaseMdef = 0x0F, BaseAgi = 0x10, BaseLuck = 0x11, BaseEva = 0x12, BaseAcc = 0x13;
    public const int CurrentAp = 0x18;
    public const int CurrentHp = 0x1C;
    public const int CurrentMp = 0x20;
    public const int MaxHp = 0x24;
    public const int MaxMp = 0x28;
    /// <summary>Stat bonuses on top of the base stats, one byte each, in the same order as the base stats.</summary>
    public const int Str = 0x2F, Def = 0x30, Mag = 0x31, Mdef = 0x32, Agi = 0x33, Luck = 0x34, Eva = 0x35, Acc = 0x36;
    /// <summary>Percent of max HP that Poison takes each turn.</summary>
    public const int PoisonDamage = 0x37;
    public const int OverdriveMode = 0x38;
    /// <summary>20 u16 counters: how much of each mode's action is needed to learn it (0xFFFF: can't be learned).</summary>
    public const int OverdriveCounters = 0x60;
    /// <summary>u32 bit field of the Overdrive modes already learned, bit n for mode n.</summary>
    public const int OverdriveLearned = 0x88;
}

/// <summary>
/// ply_rom.bin: 44-byte records in the same order as ply_save.bin. Records 8-17 hold each aeon's stat growth: pairs of
/// coefficients from 0x18 to 0x29 (HP, MP, STR, DEF, MAG, MDEF, AGI, EVA, ACC), where a stat grows with
/// "Yuna's total stats / A + Yuna's stat * B". Per-language copies differ only in the text offsets before 0x10.
/// </summary>
public static class PlyRom
{
    public const string FileName = "ply_rom.bin";
    public const int FirstAeon = 8;
    public const int Coefficients = 0x18, CoefficientsEnd = 0x2A;
    public static readonly string[] StatNames = ["hp", "mp", "strength", "defense", "magic", "magicDefense", "agility", "evasion", "accuracy"];
}

/// <summary>
/// Aeon tables that only exist once, under jppc/battle/kernel. sum_assure.bin holds 20 progression tiers of 10 aeons:
/// the least each aeon's stats can be at that point (12-byte records). sum_grow.bin lists what each aeon ability or
/// stat boost costs to teach (8-byte records like kaizou.bin).
/// </summary>
public static class AeonFiles
{
    public const string SumAssure = KernelFiles.Folder + "sum_assure.bin";
    public const string SumGrow = KernelFiles.Folder + "sum_grow.bin";

    public static readonly string[] Names = ["Valefor", "Ifrit", "Ixion", "Shiva", "Bahamut", "Anima", "Yojimbo", "Cindy", "Sandy", "Mindy"];

    // sum_assure.bin record layout.
    public const int Hp = 0x00, Mp = 0x02, Str = 0x04, Def = 0x05, Mag = 0x06, Mdef = 0x07, Acc = 0x08, Eva = 0x09, Agi = 0x0A, Luck = 0x0B;

    public static readonly string[] OverdriveModes =
    [
        "Warrior", "Comrade", "Stoic", "Healer", "Tactician", "Victim", "Dancer", "Avenger", "Slayer",
        "Hero", "Rook", "Victor", "Coward", "Ally", "Sufferer", "Daredevil", "Loner",
    ];
}
