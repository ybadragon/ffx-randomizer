using System.Text.Json.Serialization;
using FfxRandomizer.Core.Randomization;

namespace FfxRandomizer.Core.Storage;

/// <summary>A named set of settings. Presets never store a seed, so each run from one is a new game.</summary>
public sealed class Preset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public RandomizerSettings Settings { get; set; } = RandomizerSettings.Defaults();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastUsedAt { get; set; }

    [JsonIgnore]
    public bool BuiltIn { get; init; }
}

public sealed class PresetStore(AppPaths paths)
{
    private List<Preset>? _mine;

    public static IReadOnlyList<Preset> BuiltIns { get; } =
    [
        BuiltIn("Light shuffle", ["sane", "spoiler", "rewards", "drops", "steals", "field", "shops", "spheres"]),
        BuiltIn("Classic randomizer", ["sane", "spoiler", "rewards", "drops", "steals", "bribes", "geardrops", "field", "shops", "spheres", "gearab", "wshops", "custom"],
            ("gridmode", "shuffle")),
        BuiltIn("Chaos", ["sane", "spoiler", "rewards", "drops", "steals", "bribes", "geardrops", "elements", "encounters", "field", "keyitems",
                "shops", "shopprices", "gearab", "wshops", "wprices", "wstats", "crit", "atk", "custom", "aitems", "od", "locks"],
            ("pstats", "random"), ("estats", "random"), ("abase", "random"), ("ascale", "random"), ("gridmode", "random")),
    ];

    public IReadOnlyList<Preset> Mine => _mine ??= JsonFile.Load(paths.PresetsPath, () => new List<Preset>());

    public IEnumerable<Preset> All => Mine.Concat(BuiltIns);

    public Preset? Find(Guid id) => All.FirstOrDefault(p => p.Id == id);

    public bool NameTaken(string name, Guid? except = null) =>
        All.Any(p => p.Id != except && string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    public Preset Add(string name, RandomizerSettings settings)
    {
        var preset = new Preset { Name = UniqueName(name), Settings = settings.Clone() };
        List().Add(preset);
        Save();
        return preset;
    }

    public void Update(Guid id, RandomizerSettings settings)
    {
        var preset = MineOrThrow(id);
        preset.Settings = settings.Clone();
        Save();
    }

    public void Rename(Guid id, string name)
    {
        var preset = MineOrThrow(id);
        if (NameTaken(name, id)) throw new InvalidOperationException($"A preset named \"{name.Trim()}\" already exists.");
        preset.Name = name.Trim();
        Save();
    }

    public Preset Duplicate(Guid id)
    {
        var source = Find(id) ?? throw new KeyNotFoundException();
        return Add($"{source.Name} copy", source.Settings);
    }

    public void Delete(Guid id)
    {
        List().Remove(MineOrThrow(id));
        Save();
    }

    public void MarkUsed(Guid id)
    {
        if (Mine.FirstOrDefault(p => p.Id == id) is not { } preset) return;
        preset.LastUsedAt = DateTimeOffset.Now;
        Save();
    }

    private string UniqueName(string name)
    {
        string baseName = string.IsNullOrWhiteSpace(name) ? "My preset" : name.Trim();
        string candidate = baseName;
        for (int n = 2; NameTaken(candidate); n++) candidate = $"{baseName} {n}";
        return candidate;
    }

    private Preset MineOrThrow(Guid id) =>
        Mine.FirstOrDefault(p => p.Id == id) ?? throw new InvalidOperationException("Built-in presets can't be changed.");

    private List<Preset> List() => (List<Preset>)Mine;

    private void Save() => JsonFile.Save(paths.PresetsPath, Mine);

    private static Preset BuiltIn(string name, string[] toggles, params (string Group, string Choice)[] choices)
    {
        var settings = new RandomizerSettings();
        foreach (string id in toggles) settings.Toggles[id] = true;
        foreach (var (group, choice) in choices) settings.Choices[group] = choice;
        // Stable ids so history entries keep pointing at the same built-in preset.
        return new Preset { Id = new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(name))), Name = name, Settings = settings, BuiltIn = true };
    }
}
