using System.Text.Json.Nodes;
using FfxRandomizer.Core.GameData;
using FfxRandomizer.Core.Randomization.Features;
using FfxRandomizer.Core.Vbf;

namespace FfxRandomizer.Core.Randomization;

public sealed record RunResult(
    Seed Seed,
    RandomizerSettings Settings,
    IReadOnlyDictionary<string, byte[]> Files,
    JsonObject Spoiler,
    IReadOnlyList<string> Problems);

public sealed class ValidationFailedException(IReadOnlyList<string> problems)
    : Exception($"The randomizer produced something that failed its checks, so nothing was written to the game. First problem: {problems[0]}")
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

public static class RandomizerEngine
{
    /// <summary>Features in the order they run. Order is part of what a seed reproduces, so only ever append.</summary>
    public static IReadOnlyList<IRandomizerFeature> Features { get; } =
    [
        new TidusHpFeature(),
        new EnemyRewardsFeature(),
        new EnemyElementsFeature(),
        new TreasureFeature(),
        new ItemShopsFeature(),
        new CustomizationFeature(),
        new GearAbilitiesFeature(),
        new WeaponStatsFeature(),
        new GearShopsFeature(),
        new EnemyGearDropsFeature(),
        new PartyStatsFeature(),
        new OverdriveModeFeature(),
        new DeadlyPoisonFeature(),
        new AeonStatsFeature(),
        new AeonGrowthFeature(),
        new AeonItemsFeature(),
        new SphereGridFeature(),
        // Encounters before enemy stats: FFXRando swaps stats along with enemies first, then re-rolls on top.
        new EnemyEncountersFeature(),
        new EnemyStatsFeature(),
        new BlitzballStatsFeature(),
    ];

    public static RunResult Run(VbfArchive original, RandomizerSettings settings, Seed seed, IGameNames? names = null)
    {
        var context = new RandomizerContext(original, settings, seed, names);
        var enabled = Features.Where(f => f.IsEnabled(settings)).ToList();
        foreach (var feature in enabled) feature.Apply(context);
        var problems = enabled.SelectMany(f => f.Validate(context)).Distinct().ToList();
        return new RunResult(seed, settings.Clone(), context.ChangedFiles, context.Spoiler, problems);
    }
}
