using System.Buffers.Binary;
using System.Text.Json.Nodes;
using FfxRandomizer.Core.GameData;

namespace FfxRandomizer.Core.Randomization.Features;

/// <summary>
/// The seven party members' starting stats in ply_save.bin, randomized or shuffled between characters.
/// Follows FFXRando's randomizePlayerStats / shuffleCharacterStats, including its floors: Tidus and Auron keep at least
/// 14 Strength and Wakka and Lulu enough MP for the opening fights. Characters start at full HP and MP.
/// </summary>
public sealed class PartyStatsFeature : IRandomizerFeature
{
    public const string Section = "party";

    private static readonly OptionGroup Group = OptionCatalog.AllGroups.First(g => g.Id == "pstats");

    /// <summary>The one-byte stats FFXRando changes, in its order; Evasion is left alone.</summary>
    private static readonly (string Name, int Base, int Bonus)[] Stats =
    [
        ("strength", PlySave.BaseStr, PlySave.Str), ("defense", PlySave.BaseDef, PlySave.Def), ("magic", PlySave.BaseMag, PlySave.Mag),
        ("magicDefense", PlySave.BaseMdef, PlySave.Mdef), ("agility", PlySave.BaseAgi, PlySave.Agi), ("accuracy", PlySave.BaseAcc, PlySave.Acc),
        ("luck", PlySave.BaseLuck, PlySave.Luck),
    ];

    public bool IsEnabled(RandomizerSettings s) => s.ChoiceOf(Group) != "orig";

    public static JsonObject SpoilerEntry(RandomizerContext context, int character) =>
        context.SpoilerEntry(Section, KernelFiles.CharacterNames[character], new { Character = KernelFiles.CharacterNames[character] });

    private sealed record Character(int Hp, int Mp, int[] Base, int Ap, int[] Bonus)
    {
        public static Character Read(ReadOnlySpan<byte> r)
        {
            int[] stats = new int[Stats.Length], bonus = new int[Stats.Length];
            for (int k = 0; k < Stats.Length; k++) (stats[k], bonus[k]) = (r[Stats[k].Base], r[Stats[k].Bonus]);
            return new((int)BinaryPrimitives.ReadUInt32LittleEndian(r[PlySave.BaseHp..]), (int)BinaryPrimitives.ReadUInt32LittleEndian(r[PlySave.BaseMp..]),
                stats, (int)BinaryPrimitives.ReadUInt32LittleEndian(r[PlySave.CurrentAp..]), bonus);
        }

        public void Write(Span<byte> r)
        {
            foreach (int offset in new[] { PlySave.BaseHp, PlySave.CurrentHp, PlySave.MaxHp }) BinaryPrimitives.WriteUInt32LittleEndian(r[offset..], (uint)Hp);
            foreach (int offset in new[] { PlySave.BaseMp, PlySave.CurrentMp, PlySave.MaxMp }) BinaryPrimitives.WriteUInt32LittleEndian(r[offset..], (uint)Mp);
            BinaryPrimitives.WriteUInt32LittleEndian(r[PlySave.CurrentAp..], (uint)Ap);
            for (int k = 0; k < Stats.Length; k++)
            {
                r[Stats[k].Base] = (byte)Base[k];
                r[Stats[k].Bonus] = (byte)Bonus[k];
            }
        }
    }

    public void Apply(RandomizerContext context)
    {
        var rng = context.RngFor("party.stats");
        var tables = LanguageTables.Edit(context, PlySave.FileName);
        var before = Enumerable.Range(0, PlySave.PartySize).Select(i => Character.Read(tables[0].Record(i))).ToList();
        var after = new List<Character>();
        int[] source = Enumerable.Range(0, PlySave.PartySize).ToArray();

        if (context.Settings.ChoiceOf(Group) == "shuffle")
        {
            rng.Shuffle(source);
            after.AddRange(source.Select(i => before[i] with { Base = (int[])before[i].Base.Clone(), Bonus = (int[])before[i].Bonus.Clone() }));
        }
        else
        {
            for (int i = 0; i < PlySave.PartySize; i++)
            {
                var c = before[i];
                int hp = LanguageTables.Spread(rng, c.Hp, 50, 9999);
                int mp = LanguageTables.Spread(rng, c.Mp, 1, 999);
                int[] stats = c.Base.Select(v => LanguageTables.Spread(rng, v, 0, Math.Min(255, v * 3))).ToArray();
                int ap = LanguageTables.Spread(rng, c.Ap, 0, ushort.MaxValue);
                int[] bonus = c.Bonus.Select(v => LanguageTables.Spread(rng, v, 0, Math.Min(255, v * 3))).ToArray();
                after.Add(new Character(hp, mp, stats, ap, bonus));
            }
        }

        for (int i = 0; i < PlySave.PartySize; i++)
        {
            var c = after[i];
            int minMp = i == PlySave.Wakka ? 5 : i == PlySave.Lulu ? 8 : 1;
            int minStr = i is PlySave.Tidus or PlySave.Auron ? 14 : 0;
            c.Base[0] = Math.Max(c.Base[0], minStr);
            after[i] = c with { Mp = Math.Max(c.Mp, minMp) };
        }

        foreach (var table in tables)
        {
            for (int i = 0; i < PlySave.PartySize; i++) after[i].Write(table.Record(i));
            // The test mod wins over random stats.
            if (context.Settings.IsOn("tidus")) TidusHpFeature.Write(table);
        }

        for (int i = 0; i < PlySave.PartySize; i++)
        {
            var now = Character.Read(tables[0].Record(i));
            var stats = new JsonObject
            {
                ["hp"] = RandomizerContext.ToNode(new { From = before[i].Hp, To = now.Hp }),
                ["mp"] = RandomizerContext.ToNode(new { From = before[i].Mp, To = now.Mp }),
            };
            for (int k = 0; k < Stats.Length; k++) stats[Stats[k].Name] = RandomizerContext.ToNode(new { From = before[i].Base[k], To = now.Base[k] });
            if (before[i].Ap != now.Ap) stats["ap"] = RandomizerContext.ToNode(new { From = before[i].Ap, To = now.Ap });
            var entry = SpoilerEntry(context, i);
            if (source[i] != i) entry["statsFrom"] = KernelFiles.CharacterNames[source[i]];
            entry["stats"] = stats;
        }
    }

    public IEnumerable<string> Validate(RandomizerContext context)
    {
        foreach (string p in LanguageTables.CheckInSync(context, PlySave.FileName, 0x02)) yield return p;
        var table = new KernelTable(context.ChangedFiles[GamePaths.LanguageKernel(LanguageTables.Reference, PlySave.FileName)]);
        for (int i = 0; i < PlySave.PartySize; i++)
        {
            var r = table.Record(i).ToArray();
            var c = Character.Read(r);
            string who = KernelFiles.CharacterNames[i];
            uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(offset));
            if (c.Hp is < 1 or > 9999 || U32(PlySave.CurrentHp) != c.Hp || U32(PlySave.MaxHp) != c.Hp) yield return $"{who}: HP {c.Hp}/{U32(PlySave.CurrentHp)}/{U32(PlySave.MaxHp)}.";
            if (c.Mp is < 1 or > 999 || U32(PlySave.CurrentMp) != c.Mp || U32(PlySave.MaxMp) != c.Mp) yield return $"{who}: MP {c.Mp}/{U32(PlySave.CurrentMp)}/{U32(PlySave.MaxMp)}.";
            if (i == PlySave.Wakka && c.Mp < 5 || i == PlySave.Lulu && c.Mp < 8) yield return $"{who}: only {c.Mp} MP for the opening fights.";
            if (i is PlySave.Tidus or PlySave.Auron && c.Base[0] < 14) yield return $"{who}: Strength {c.Base[0]} is below 14.";
            if (c.Ap > ushort.MaxValue) yield return $"{who}: AP {c.Ap}.";
        }
    }
}
