using System.Text.Json;
using System.Text.Json.Nodes;
using FfxRandomizer.Core.GameData;
using FfxRandomizer.Core.Vbf;

namespace FfxRandomizer.Core.Randomization;

/// <summary>What a feature works with: the original game files, the settings, its own random stream and the spoiler log.</summary>
public sealed class RandomizerContext(VbfArchive original, RandomizerSettings settings, Seed seed, IGameNames? names = null)
{
    private static readonly JsonSerializerOptions SpoilerJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Dictionary<string, byte[]> _changed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string, string), JsonObject> _entries = [];
    private ItemPool? _items;

    public VbfArchive Original { get; } = original;
    public RandomizerSettings Settings { get; } = settings;
    public Seed Seed { get; } = seed;
    public IGameNames Names { get; } = names ?? NoGameNames.Instance;

    /// <summary>Items found anywhere in the original game; built on first use.</summary>
    public ItemPool Items => _items ??= ItemPool.Build(Original);

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
            bytes = Original.ReadFile(archivePath);
            _changed[archivePath] = bytes;
        }
        return bytes;
    }

    /// <summary>Adds one entry (any serializable object, camelCased) to a spoiler section.</summary>
    public void Spoil(string section, object entry) => SectionList(section).Add(ToNode(entry));

    /// <summary>
    /// The spoiler entry for one thing (e.g. one monster), shared by every feature that changes it, so each adds its own
    /// properties to the same object. <paramref name="identity"/> seeds a new entry's first properties.
    /// </summary>
    public JsonObject SpoilerEntry(string section, string key, object identity)
    {
        if (_entries.TryGetValue((section, key), out var entry)) return entry;
        entry = (JsonObject)ToNode(identity)!;
        SectionList(section).Add(entry);
        _entries[(section, key)] = entry;
        return entry;
    }

    public static JsonNode? ToNode(object value) => JsonSerializer.SerializeToNode(value, SpoilerJson);

    private JsonArray SectionList(string section)
    {
        if (Spoiler[section] is not JsonArray list) Spoiler[section] = list = [];
        return list;
    }
}

public interface IRandomizerFeature
{
    bool IsEnabled(RandomizerSettings settings);
    void Apply(RandomizerContext context);

    /// <summary>
    /// Checks the finished run for rule breaks this feature must never produce (bad item ids, broken files...).
    /// Any message returned stops the run before the game is touched.
    /// </summary>
    IEnumerable<string> Validate(RandomizerContext context) => [];
}
