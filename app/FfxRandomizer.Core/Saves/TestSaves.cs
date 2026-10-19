using System.Security.Cryptography;
using FfxRandomizer.Core.Game;
using FfxRandomizer.Core.Storage;

namespace FfxRandomizer.Core.Saves;

/// <summary>A save this tool wrote into the game's folder, so it can be told apart from the player's own saves.</summary>
public sealed record TestSaveEntry(int Slot, string Sha256, string Purpose, DateTimeOffset CreatedAt);

/// <summary>
/// Writes test saves into the game's save folder and deletes only those, never a player's save. Every slot written is
/// recorded with its hash; a slot is deleted only if it is in that record and its contents are unchanged.
/// </summary>
public sealed class TestSaves(SaveFolder folder, string manifestPath, Func<bool>? isGameRunning = null)
{
    private readonly Func<bool> _isGameRunning = isGameRunning ?? GameInstall.IsGameRunning;

    public static TestSaves Default => new(SaveFolder.Default, AppPaths.Default.TestSavesPath);

    public IReadOnlyList<TestSaveEntry> Entries => Load();

    /// <summary>
    /// Writes <paramref name="save"/> to a free slot and records it. The file's modified time is set one second after the
    /// newest save in the folder, because the load menu shows that time and sorts by it: saves written in the same second
    /// otherwise come out in an arbitrary order.
    /// </summary>
    public string Write(SaveFile save, int slot, string purpose)
    {
        RefuseWhileGameRuns();
        if (!folder.Exists) throw new DirectoryNotFoundException($"The game's save folder wasn't found at {folder.Path}.");
        string path = folder.SlotPath(slot);
        var newest = folder.UsedSlots().Select(s => File.GetLastWriteTimeUtc(folder.SlotPath(s))).DefaultIfEmpty(DateTime.MinValue).Max();
        var stamp = DateTime.UtcNow > newest ? DateTime.UtcNow : newest.AddSeconds(1);
        save.Save(path);
        File.SetLastWriteTimeUtc(path, stamp);

        var entries = Load().Where(e => e.Slot != slot).Append(new TestSaveEntry(slot, Hash(path), purpose, DateTimeOffset.Now)).OrderBy(e => e.Slot).ToList();
        JsonFile.Save(manifestPath, entries);
        return path;
    }

    /// <summary>Deletes a slot this tool wrote. Refuses any other slot, and one whose file changed since (the player may have saved over it).</summary>
    public void Delete(int slot)
    {
        RefuseWhileGameRuns();
        var entries = Load();
        var entry = entries.FirstOrDefault(e => e.Slot == slot)
            ?? throw new InvalidOperationException($"Slot {slot} wasn't written by this tool; not deleting it.");
        string path = folder.SlotPath(slot);
        if (File.Exists(path))
        {
            if (Hash(path) != entry.Sha256)
                throw new InvalidOperationException($"Slot {slot} has changed since this tool wrote it (the game may have saved over it); not deleting it.");
            File.Delete(path);
        }
        JsonFile.Save(manifestPath, entries.Where(e => e.Slot != slot).ToList());
    }

    private void RefuseWhileGameRuns()
    {
        if (_isGameRunning()) throw new InvalidOperationException("FINAL FANTASY X is running. Close it before changing its saves.");
    }

    private List<TestSaveEntry> Load() => JsonFile.Load<List<TestSaveEntry>>(manifestPath, () => []);

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
