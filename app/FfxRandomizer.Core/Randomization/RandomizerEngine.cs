using System.Text.Json.Nodes;
using FfxRandomizer.Core.Randomization.Features;
using FfxRandomizer.Core.Vbf;

namespace FfxRandomizer.Core.Randomization;

public sealed record RunResult(
    Seed Seed,
    RandomizerSettings Settings,
    IReadOnlyDictionary<string, byte[]> Files,
    JsonObject Spoiler);

public static class RandomizerEngine
{
    /// <summary>Features in the order they run. Order is part of what a seed reproduces, so only ever append.</summary>
    public static IReadOnlyList<IRandomizerFeature> Features { get; } =
    [
        new TidusHpFeature(),
    ];

    public static RunResult Run(VbfArchive original, RandomizerSettings settings, Seed seed)
    {
        var context = new RandomizerContext(original, settings, seed);
        foreach (var feature in Features)
        {
            if (feature.IsEnabled(settings)) feature.Apply(context);
        }
        return new RunResult(seed, settings.Clone(), context.ChangedFiles, context.Spoiler);
    }
}
