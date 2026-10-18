using System.Text.Json.Nodes;
using FfxRandomizer.Core.GameData;

namespace FfxRandomizer.Core.Randomization.Features;

/// <summary>
/// Gives each enemy new elemental weaknesses, resistances, immunities and absorptions.
/// Per element, out of 70: 20 weak, 5 resist, 5 immune, 5 absorb, the rest neutral (as FFXRando does).
/// </summary>
public sealed class EnemyElementsFeature : IRandomizerFeature
{
    private static readonly Elements[] Each = [Elements.Fire, Elements.Ice, Elements.Lightning, Elements.Water, Elements.Holy];

    public bool IsEnabled(RandomizerSettings s) => s.IsOn("elements");

    public void Apply(RandomizerContext context)
    {
        var rng = context.RngFor("enemies.elements");
        foreach (int index in MonsterFile.AllIndices(context.Original))
        {
            string path = MonsterFile.PathFor(index);
            var original = new MonsterFile(index, context.Original.ReadFile(path));
            // Placeholder monsters (1 HP or less) are left alone.
            if (!original.HasStats || original.Stats.Hp <= 1) continue;

            var stats = new MonsterFile(index, context.Edit(path)).Stats;
            Elements weak = 0, resist = 0, immune = 0, absorb = 0;
            foreach (var element in Each)
            {
                int roll = rng.Next(0, 69);
                if (roll < 20) weak |= element;
                else if (roll < 25) resist |= element;
                else if (roll < 30) immune |= element;
                else if (roll < 35) absorb |= element;
            }
            stats.Weakness = weak;
            stats.Resist = resist;
            stats.Immune = immune;
            stats.Absorb = absorb;

            EnemyRewardsFeature.EnemyEntry(context, index)["elements"] = new JsonObject
            {
                ["weak"] = List(weak),
                ["resist"] = List(resist),
                ["immune"] = List(immune),
                ["absorb"] = List(absorb),
            };
        }
    }

    private static JsonArray List(Elements flags) => new(Each.Where(e => flags.HasFlag(e)).Select(e => (JsonNode)e.ToString()).ToArray());

    public IEnumerable<string> Validate(RandomizerContext context)
    {
        foreach (int index in MonsterFile.AllIndices(context.Original))
        {
            string path = MonsterFile.PathFor(index);
            if (!context.ChangedFiles.TryGetValue(path, out byte[]? bytes)) continue;
            var monster = new MonsterFile(index, bytes);
            if (!monster.HasStats) continue;
            var s = monster.Stats;
            foreach (var element in Each)
            {
                int categories = new[] { s.Weakness, s.Resist, s.Immune, s.Absorb }.Count(c => c.HasFlag(element));
                if (categories > 1) yield return $"m{index:D3}: {element} is in {categories} categories at once.";
            }
            var original = new MonsterFile(index, context.Original.ReadFile(path));
            for (int offset = MonsterStats.AbsorbOffset; offset <= MonsterStats.WeaknessOffset; offset++)
            {
                if ((bytes[monster.StatsOffset + offset] & ~MonsterStats.ElementMask) != (original.Bytes[original.StatsOffset + offset] & ~MonsterStats.ElementMask))
                    yield return $"m{index:D3}: non-element bits changed at stat offset 0x{offset:X}.";
            }
        }
        if (!new EnemyRewardsFeature().IsEnabled(context.Settings))
        {
            foreach (string problem in EnemyChecks.Integrity(context)) yield return problem;
            foreach (string problem in EnemyChecks.SpoilerMatchesFiles(context)) yield return problem;
        }
    }
}
