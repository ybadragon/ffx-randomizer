using FfxRandomizer.Core.Storage;
using FfxRandomizer.Core.Vbf;

namespace FfxRandomizer.Core.Patching;

public enum GameDataStatus
{
    /// <summary>Matches the backup taken before the last patch.</summary>
    Original,
    /// <summary>Carries the randomization this app (or the .bat) last applied.</summary>
    Randomized,
    /// <summary>Never seen before, or replaced since (a Steam update or file verification). Treated as original.</summary>
    Unrecognized,
}

public sealed class GameRunningException() : Exception("FINAL FANTASY X is running. Close the game and its launcher first.");

public sealed class PatchFailedException(string message, Exception inner) : Exception(message, inner);

/// <summary>Applies changed game files to FFX_Data.vbf and restores the original, keeping a header backup.</summary>
public sealed class PatchManager(string archivePath, AppPaths paths, Func<bool>? isGameRunning = null)
{
    private readonly Func<bool> _isGameRunning = isGameRunning ?? Game.GameInstall.IsGameRunning;

    public string ArchivePath { get; } = archivePath;

    public GameDataStatus GetStatus()
    {
        var state = PatchState.Load(paths.StatePath);
        string fingerprint = VbfArchive.Fingerprint(ArchivePath);
        if (state is { Patched: true } && fingerprint == state.PatchedFingerprint) return GameDataStatus.Randomized;
        if (state is not null && fingerprint == state.OriginalFingerprint) return GameDataStatus.Original;
        return GameDataStatus.Unrecognized;
    }

    /// <summary>The fingerprint the archive has right now; history entries use it to mark the run that is applied.</summary>
    public string CurrentFingerprint() => VbfArchive.Fingerprint(ArchivePath);

    /// <summary>Opens the original game data, even while the archive is randomized.</summary>
    public VbfArchive OpenOriginal()
    {
        if (GetStatus() == GameDataStatus.Randomized)
            return VbfArchive.Open(ArchivePath, File.ReadAllBytes(paths.HeaderBackupPath));

        var archive = VbfArchive.Open(ArchivePath);
        if (!archive.HeaderChecksumValid)
            throw new InvalidDataException("FFX_Data.vbf looks damaged or was changed by another tool. Verify the game files in Steam, then try again.");
        return archive;
    }

    /// <summary>Removes any previous randomization, writes <paramref name="files"/>, and checks every file reads back exactly.</summary>
    public string Apply(IReadOnlyDictionary<string, byte[]> files)
    {
        if (_isGameRunning()) throw new GameRunningException();
        var state = EnsureOriginal();

        try
        {
            VbfArchive.Open(ArchivePath).Patch(files, state.OriginalLength);
            Verify(files);
        }
        catch (Exception e)
        {
            // Leave the game exactly as it was before this attempt.
            RestoreFromBackup(state);
            throw new PatchFailedException("Writing to the game didn't finish, so the original game data was put back.", e);
        }

        state.Patched = true;
        state.PatchedFingerprint = VbfArchive.Fingerprint(ArchivePath);
        state.Save(paths.StatePath);
        return state.PatchedFingerprint;
    }

    /// <summary>Puts the original game data back. Returns false when there was nothing to restore.</summary>
    public bool Restore()
    {
        if (GetStatus() != GameDataStatus.Randomized) return false;
        if (_isGameRunning()) throw new GameRunningException();
        var state = PatchState.Load(paths.StatePath)!;
        RestoreFromBackup(state);
        state.Patched = false;
        state.PatchedFingerprint = null;
        state.Save(paths.StatePath);
        return true;
    }

    private PatchState EnsureOriginal()
    {
        var status = GetStatus();
        var state = PatchState.Load(paths.StatePath);
        if (status == GameDataStatus.Randomized)
        {
            RestoreFromBackup(state!);
            state!.Patched = false;
            state.PatchedFingerprint = null;
            state.Save(paths.StatePath);
            return state;
        }
        if (status == GameDataStatus.Original) return state!;

        // First run, or Steam replaced the archive since: back up what is there now.
        if (!VbfArchive.Open(ArchivePath).HeaderChecksumValid)
            throw new InvalidDataException("FFX_Data.vbf looks damaged or was changed by another tool. Verify the game files in Steam, then try again.");
        Directory.CreateDirectory(Path.GetDirectoryName(paths.HeaderBackupPath)!);
        File.WriteAllBytes(paths.HeaderBackupPath, VbfArchive.ReadHeaderBytes(ArchivePath));
        state = new PatchState
        {
            OriginalFingerprint = VbfArchive.Fingerprint(ArchivePath),
            OriginalLength = new FileInfo(ArchivePath).Length,
            InputFingerprint = state?.InputFingerprint,
        };
        state.Save(paths.StatePath);
        return state;
    }

    private void RestoreFromBackup(PatchState state)
    {
        VbfArchive.Restore(ArchivePath, File.ReadAllBytes(paths.HeaderBackupPath), state.OriginalLength);
        if (VbfArchive.Fingerprint(ArchivePath) != state.OriginalFingerprint)
            throw new InvalidDataException("Restoring the original game data didn't verify. Use Steam's \"Verify integrity of game files\" to repair it.");
    }

    private void Verify(IReadOnlyDictionary<string, byte[]> files)
    {
        var archive = VbfArchive.Open(ArchivePath);
        if (!archive.HeaderChecksumValid) throw new InvalidDataException("Header checksum mismatch after patching.");
        using var stream = VbfArchive.OpenRead(ArchivePath);
        foreach (var (name, expected) in files)
        {
            if (!archive.ReadFile(stream, archive.IndexOf(name)).AsSpan().SequenceEqual(expected))
                throw new InvalidDataException($"{name} didn't read back correctly after patching.");
        }
    }
}
