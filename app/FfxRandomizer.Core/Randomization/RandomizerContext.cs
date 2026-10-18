using System.Text.Json;
using System.Text.Json.Nodes;
using FfxRandomizer.Core.Vbf;

namespace FfxRandomizer.Core.Randomization;

/// <summary>What a feature works with: the original game files, the settings, its own random stream and the spoiler log.</summary>
public sealed class RandomizerContext(VbfArchive original, RandomizerSettings settings, Seed seed)
{
    private static readonly JsonSerializerOptions SpoilerJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Dictionary<string, byte[]> _changed = new(StringComparer.OrdinalIgnoreCase);

    public RandomizerSettings Settings { get; } = settings;
    public Seed Seed { get; } = seed;

    public IReadOnlyDictionary<string, byte[]> ChangedFiles => _changed;

    /// <summary>Structured spoiler data by section ("party", "enemies", "shops", ...); each section is a list of entries.</summary>
    public JsonObject Spoiler { get; } = new();

    public Rng RngFor(string section) => Rng.For(Seed, section);

    /// <summary>
    /// The working copy of a game file. The first call reads the original from the archive; later calls
    /// (from any feature) return the same copy, so edits accumulate. Every file fetched here is written to the game.
    /// </summary>
    public byte[] Edit(string archivePath)
    {
        if (!_changed.TryGetValue(archivePath, out byte[]? bytes))
        {
            bytes = original.ReadFile(archivePath);
            _changed[archivePath] = bytes;
        }
        return bytes;
    }

    /// <summary>Adds one entry (any serializable object, camelCased) to a spoiler section.</summary>
    public void Spoil(string section, object entry)
    {
        if (Spoiler[section] is not JsonArray list) Spoiler[section] = list = [];
        list.Add(JsonSerializer.SerializeToNode(entry, SpoilerJson));
    }
}

public interface IRandomizerFeature
{
    /// <summary>Spoiler log heading for this feature.</summary>
    string Name { get; }
    bool IsEnabled(RandomizerSettings settings);
    void Apply(RandomizerContext context);
}
