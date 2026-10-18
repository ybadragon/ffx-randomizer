using FfxRandomizer.Core.GameData;

namespace FfxRandomizer.Core.Randomization.Features;

/// <summary>
/// Changes every enemy's stats, following FFXRando's EnemyDataRandomizer:
/// "random" re-rolls stats around their own values; "shuffle" also hands each enemy another enemy's defense,
/// magic defense and evasion; "shufflenorm" draws those from other enemies too and evens out HP, gil and AP to match.
/// </summary>
public sealed class EnemyStatsFeature : IRandomizerFeature
{
    public const string Original = "orig", Shuffle = "shuffle", ShuffleNormalized = "shufflenorm", Random = "random";

    // The battle treasure chests (MON_TREASURE_CHEST_01 and _TUTORIAL) are never changed.
    private static readonly HashSet<int> Skipped = [211, 237];

    public bool IsEnabled(RandomizerSettings s) => Mode(s) != Original;

    public static string Mode(RandomizerSettings s) => s.ChoiceOf(OptionCatalog.AllGroups.First(g => g.Id == "estats"));

    public void Apply(RandomizerContext context)
    {
        var rng = context.RngFor("enemies.stats");
        string mode = Mode(context.Settings);
        bool sane = context.Settings.IsOn("sane");

        // Enemies to change, as they stand after earlier features (an encounter stat swap comes first, as in FFXRando).
        var enemies = MonsterFile.AllIndices(context.Original)
            .Select(i => StatChanges.Current(context, i))
            .Where(m => m.HasStats && m.Stats.Hp > 1)
            .ToList();
        var defenses = enemies.Select(m => (m.Stats.Defense, m.Stats.MagicDefense, m.Stats.Evasion)).ToList();

        foreach (var enemy in enemies)
        {
            if (Skipped.Contains(enemy.Index)) continue;
            var file = new MonsterFile(enemy.Index, context.Edit(MonsterFile.PathFor(enemy.Index)));
            var s = file.Stats;
            switch (mode)
            {
                case Random: RollAroundOwn(rng, s, sane); break;
                case ShuffleNormalized: ShuffleNormalizingDefenses(rng, file, defenses); break;
                case Shuffle: ShuffleDefenses(rng, s, defenses); break;
            }
            StatChanges.Record(context, enemy.Index);
        }
    }

    // randomizeEnemyStatsNormal
    private static void RollAroundOwn(Rng rng, MonsterStats s, bool sane)
    {
        s.Hp = Math.Max(50u, N(rng, s.Hp, s.Hp / 2, (long)Math.Floor(s.Hp * 0.5), (long)s.Hp * 3));
        s.Mp = N(rng, s.Mp, s.Mp / 2, 1, 999);
        s.OverkillThreshold = N(rng, s.OverkillThreshold / 2, s.OverkillThreshold, 1, 99999);
        if (sane)
        {
            s.Strength = Sane(rng, s.Strength);
            s.Defense = Sane(rng, s.Defense);
            s.Magic = Sane(rng, s.Magic);
            s.MagicDefense = Sane(rng, s.MagicDefense);
            s.Agility = Sane(rng, s.Agility);
            s.Accuracy = Sane(rng, s.Accuracy);
        }
        else
        {
            s.Strength = Wide(rng, s.Strength);
            s.Defense = Wide(rng, s.Defense);
            s.Magic = Wide(rng, s.Magic);
            s.MagicDefense = Wide(rng, s.MagicDefense);
            s.Agility = Wide(rng, s.Agility);
            s.Accuracy = Wide(rng, s.Accuracy);
        }
        s.Armored = rng.Next(0, 3) == 0;
    }

    // randomizeEnemyStatsDefensiveNormalization
    private static void ShuffleNormalizingDefenses(Rng rng, MonsterFile file, List<(byte Def, byte Mdef, byte Eva)> pool)
    {
        var s = file.Stats;
        if (s.Mp > 0) s.Mp = N(rng, s.Mp, s.Mp / 2, 1, 999);
        s.OverkillThreshold = N(rng, s.OverkillThreshold, s.OverkillThreshold / 2, 1, uint.MaxValue);

        s.Defense = rng.Pick(pool).Def;
        s.MagicDefense = rng.Pick(pool).Mdef;
        s.Evasion = rng.Pick(pool).Eva;
        static double Factor(byte value) => value == 0 ? 1 : 1.0 - value / 255.0 / 3;
        double defensiveFactor = (Factor(s.Defense) + Factor(s.MagicDefense) + Factor(s.Evasion)) / 3;
        s.Armored = defensiveFactor >= 0.33 && rng.Next(0, 3) == 0;

        uint baseHp = (uint)(s.Hp * defensiveFactor);
        if (baseHp <= 1) baseHp = 50;
        s.Hp = Math.Max(50u, N(rng, baseHp, baseHp / 2, 50, uint.MaxValue));
        s.Strength = Sane(rng, s.Strength);
        s.Magic = Sane(rng, s.Magic);
        s.Agility = Sane(rng, s.Agility);
        s.Accuracy = Sane(rng, s.Accuracy);

        if (file.HasLoot)
        {
            var loot = file.Loot;
            loot.Gil = (ushort)Math.Min(ushort.MaxValue, loot.Gil / defensiveFactor);
            loot.Ap = (ushort)Math.Min(ushort.MaxValue, loot.Ap / defensiveFactor);
        }
    }

    // shuffleEnemyStats
    private static void ShuffleDefenses(Rng rng, MonsterStats s, List<(byte Def, byte Mdef, byte Eva)> pool)
    {
        var (def, mdef, eva) = rng.Pick(pool);
        s.Hp = Math.Max(50u, N(rng, s.Hp, s.Hp / 2, 50, uint.MaxValue));
        s.Mp = N(rng, s.Mp, s.Mp / 2, 1, 9999);
        s.OverkillThreshold = N(rng, s.OverkillThreshold / 2, s.OverkillThreshold, 1, uint.MaxValue);
        s.Strength = Wide(rng, s.Strength);
        s.Defense = def;
        s.MagicDefense = mdef;
        s.Evasion = eva;
        s.Armored = rng.Next(0, 3) == 0;
        s.Agility = Wide(rng, s.Agility);
        s.Luck = Wide(rng, s.Luck);
        s.Accuracy = Wide(rng, s.Accuracy);
    }

    /// <summary>Within a third either way, capped at 5/3 of the original.</summary>
    private static byte Sane(Rng rng, byte value) =>
        (byte)N(rng, value, value / 3u, value / 3u, Math.Clamp((long)Math.Round(value * 5.0 / 3), 0, 255));

    private static byte Wide(Rng rng, byte value) => (byte)N(rng, value, value / 2u, 0, 255);

    /// <summary>FFXRando's normal&lt;T&gt;: 0 stays 0 and a zero spread keeps the mean; otherwise a redrawn normal sample.</summary>
    private static uint N(Rng rng, uint mean, uint spread, long min, long max)
    {
        if (mean == 0) return 0;
        if (spread == 0) return mean;
        return (uint)rng.Normal(mean, spread, (int)Math.Clamp(min, 0, int.MaxValue), (int)Math.Clamp(max, 0, int.MaxValue));
    }

    public IEnumerable<string> Validate(RandomizerContext context)
    {
        foreach (int index in Skipped)
        {
            string path = MonsterFile.PathFor(index);
            if (!context.ChangedFiles.TryGetValue(path, out byte[]? bytes)) continue;
            var original = new MonsterFile(index, context.Original.ReadFile(path));
            var now = new MonsterFile(index, bytes);
            if (original.HasStats && !bytes.AsSpan(now.StatsOffset + MonsterStats.FirstStatOffset, MonsterStats.LastStatOffset - MonsterStats.FirstStatOffset + 1)
                    .SequenceEqual(original.Bytes.AsSpan(original.StatsOffset + MonsterStats.FirstStatOffset, MonsterStats.LastStatOffset - MonsterStats.FirstStatOffset + 1)))
                yield return $"m{index:D3}: the battle treasure chest's stats changed.";
        }
        foreach (string problem in StatChanges.Validate(context)) yield return problem;
        foreach (string problem in EnemyChecks.Integrity(context)) yield return problem;
        foreach (string problem in EnemyChecks.SpoilerMatchesFiles(context)) yield return problem;
    }
}
