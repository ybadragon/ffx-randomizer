using FfxRandomizer.Core.Randomization;

namespace FfxRandomizer.Core.Storage;

/// <summary>One run: everything needed to make the exact same game again.</summary>
public sealed class HistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public string Seed { get; set; } = "";
    public RandomizerSettings Settings { get; set; } = new();
    public Guid? PresetId { get; set; }
    public string? PresetName { get; set; }
    public string AppVersion { get; set; } = AppInfo.Version;
    public int FileCount { get; set; }
    public string? SpoilerPath { get; set; }

    /// <summary>The archive fingerprint right after this run was applied; matching it means this run is in the game now.</summary>
    public string? PatchedFingerprint { get; set; }
}

public sealed class HistoryStore(AppPaths paths)
{
    private List<HistoryEntry>? _entries;

    /// <summary>Newest first.</summary>
    public IReadOnlyList<HistoryEntry> Entries => _entries ??= JsonFile.Load(paths.HistoryPath, () => new List<HistoryEntry>());

    public void Add(HistoryEntry entry)
    {
        ((List<HistoryEntry>)Entries).Insert(0, entry);
        Save();
    }

    public void Delete(Guid id)
    {
        ((List<HistoryEntry>)Entries).RemoveAll(e => e.Id == id);
        Save();
    }

    private void Save() => JsonFile.Save(paths.HistoryPath, Entries);
}
