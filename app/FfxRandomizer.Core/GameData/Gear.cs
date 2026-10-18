using FfxRandomizer.Core.Vbf;

namespace FfxRandomizer.Core.GameData;

/// <summary>
/// The gear tables under jppc/battle/kernel. weapon.bin (starting and story gear) and shop_arms.bin (gear sold in shops)
/// use 22-byte records; buki_get.bin (gear found in chests) uses 16-byte records. arms_shop.bin lists shop_arms.bin
/// indexes per shop, and arms_rate.bin holds one u32 gil price per auto-ability. Layouts follow FFXRando's gear_data_t.
/// </summary>
public static class GearFiles
{
    public const string Weapons = KernelFiles.Folder + "weapon.bin";
    public const string ShopGear = KernelFiles.Folder + "shop_arms.bin";
    public const string TreasureGear = KernelFiles.TreasureGear;
    public const string Shops = KernelFiles.Folder + "arms_shop.bin";
    public const string AbilityPrices = KernelFiles.Folder + "arms_rate.bin";

    /// <summary>The three tables that hold gear records, with the short names used in the spoiler log.</summary>
    public static readonly IReadOnlyList<(string Name, string Path)> Tables =
        [("weapon", Weapons), ("shop_arms", ShopGear), ("buki_get", TreasureGear)];

    public static IEnumerable<(string Table, int Index, GearRecord Gear)> ReadAll(Func<string, byte[]> file)
    {
        foreach (var (name, path) in Tables)
        {
            var table = new KernelTable(file(path));
            for (int i = 0; i < table.RecordCount; i++) yield return (name, i, new GearRecord(table.Bytes.AsMemory(table.DataOffset + i * table.RecordSize, table.RecordSize)));
        }
    }
}

/// <summary>
/// One piece of gear. 22-byte records: flags 0x03, character 0x04, armor 0x05, formula 0x08, power 0x09, crit 0x0A,
/// slots 0x0B, abilities from 0x0E. 16-byte buki_get records: flags 0x00, character 0x01, armor 0x02, then formula,
/// power, crit and slots from 0x04 and abilities from 0x08.
/// </summary>
public readonly struct GearRecord(Memory<byte> record)
{
    public const ushort EmptyAbility = 0x00FF;
    public const int MaxSlots = 4;

    private Span<byte> B => record.Span;
    private bool Short => record.Length == 16;
    private int Head => Short ? 0x00 : 0x03;
    private int Stats => Short ? 0x04 : 0x08;
    private int AbilityOffset => Short ? 0x08 : 0x0E;

    public byte Flags => B[Head];
    public byte Character => B[Head + 1];
    public bool IsArmor => B[Head + 2] != 0;
    public bool IsCelestial => (Flags & 0x04) != 0;
    public bool IsBrotherhood => (Flags & 0x08) != 0;

    /// <summary>Gear for Tidus to Rikku (0-6); weapon.bin also has aeon gear (8 and up).</summary>
    public bool IsPartyGear => Character < KernelFiles.CharacterNames.Length;

    public byte Formula { get => B[Stats]; set => B[Stats] = value; }
    public byte Power { get => B[Stats + 1]; set => B[Stats + 1] = value; }
    public byte Crit { get => B[Stats + 2]; set => B[Stats + 2] = value; }
    public byte Slots { get => B[Stats + 3]; set => B[Stats + 3] = value; }

    public ushort Ability(int slot) => KernelFiles.U16(B, AbilityOffset + slot * 2);
    public void SetAbility(int slot, ushort ability) => KernelFiles.U16(B, AbilityOffset + slot * 2, ability);

    /// <summary>The four ability slots in order, empty ones included.</summary>
    public ushort[] Abilities => Enumerable.Range(0, MaxSlots).Select(Ability).ToArray();

    /// <summary>Byte offsets the randomizer may change: formula, power, crit, slot count and the four abilities.</summary>
    public IEnumerable<int> EditableOffsets =>
        Enumerable.Range(Stats, 4).Concat(Enumerable.Range(AbilityOffset, MaxSlots * 2));

    public string Who => Character < KernelFiles.CharacterNames.Length ? KernelFiles.CharacterNames[Character] : $"Character {Character}";
    public string Kind => IsArmor ? "Armor" : "Weapon";
}

/// <summary>Damage formulas a weapon can use (FFXRando's weapon_formula_e).</summary>
public static class WeaponFormulas
{
    public const byte None = 0x00, StrVsDef = 0x01;

    public static readonly IReadOnlyDictionary<byte, string> Names = new Dictionary<byte, string>
    {
        [0x00] = "None",
        [0x01] = "Strength vs Defense",
        [0x02] = "Strength, ignores Defense",
        [0x03] = "Magic vs Magic Defense",
        [0x04] = "Magic, ignores Magic Defense",
        [0x05] = "Fraction of target's current HP",
        [0x06] = "Fixed, multiple of 50",
        [0x07] = "Heals the target",
        [0x08] = "Fraction of target's max HP",
        [0x09] = "Fixed, multiple of 50 (varies)",
        [0x0A] = "Target's max MP",
        [0x0B] = "Target's max CTB",
        [0x0C] = "Target's current MP",
        [0x0D] = "Target's current CTB",
        [0x0E] = "Strength, ignores Cheer",
        [0x0F] = "Special",
        [0x10] = "Tenth of user's max HP",
        [0x11] = "Celestial, high HP",
        [0x12] = "Celestial, high MP",
        [0x13] = "Celestial, low HP",
        [0x14] = "Magic, ignores Focus",
        [0x15] = "Gil spent",
        [0x16] = "Kills",
        [0x17] = "Fixed 9999",
    };

    /// <summary>Every formula FFXRando picks from (all but None and Gil). Some of these heal, only hit MP or CTB, or do nothing.</summary>
    public static readonly IReadOnlyList<byte> All =
        [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10, 0x11, 0x12, 0x13, 0x14, 0x16, 0x17];

    /// <summary>
    /// Formulas that always deal HP damage to ordinary enemies and bosses alike. Left out: healing, MP and CTB damage,
    /// Special (no damage), Kills (0 early on), percentage damage (bosses ignore it) and fixed 9999.
    /// </summary>
    public static readonly IReadOnlyList<byte> Sane = [0x01, 0x02, 0x03, 0x04, 0x06, 0x09, 0x0E, 0x10, 0x11, 0x12, 0x13, 0x14];

    public static string Name(byte formula) => Names.TryGetValue(formula, out string? name) ? name : $"Formula 0x{formula:X2}";
}

/// <summary>
/// The auto-abilities gear can carry, split by weapon and armor, gathered from party gear in every gear table and from
/// enemy equipment drops in the original game. FFXRando gathers one mixed list that also takes in aeon gear, whose
/// unnamed immunity ability (0x807B) could then land on party gear; keeping weapons and armor apart also stops armor
/// abilities landing on weapons.
/// </summary>
public sealed class GearAbilityPool
{
    public const ushort First = 0x8000;

    public IReadOnlyList<ushort> Weapon { get; }
    public IReadOnlyList<ushort> Armor { get; }

    private GearAbilityPool(SortedSet<ushort> weapon, SortedSet<ushort> armor)
    {
        Weapon = weapon.ToList();
        Armor = armor.ToList();
    }

    public IReadOnlyList<ushort> For(bool armor) => armor ? Armor : Weapon;

    public static bool IsAbility(ushort id) => id >= First && id < First + 0x100;

    public static GearAbilityPool Build(VbfArchive original)
    {
        var weapon = new SortedSet<ushort>();
        var armor = new SortedSet<ushort>();
        foreach (var (_, _, gear) in GearFiles.ReadAll(original.ReadFile).Where(g => g.Gear.IsPartyGear))
            foreach (ushort ability in gear.Abilities.Where(IsAbility))
                (gear.IsArmor ? armor : weapon).Add(ability);
        foreach (int index in MonsterFile.AllIndices(original))
        {
            var monster = new MonsterFile(index, original.ReadFile(MonsterFile.PathFor(index)));
            if (!monster.HasLoot) continue;
            var loot = monster.Loot;
            for (int chr = 0; chr < MonsterLoot.GearCharacters; chr++)
                for (int i = 0; i < MonsterLoot.GearAbilitySlots; i++)
                {
                    if (IsAbility(loot.WeaponAbility(chr, i))) weapon.Add(loot.WeaponAbility(chr, i));
                    if (IsAbility(loot.ArmorAbility(chr, i))) armor.Add(loot.ArmorAbility(chr, i));
                }
        }
        return new GearAbilityPool(weapon, armor);
    }
}
