using FfxRandomizer.Core.Game;
using FfxRandomizer.Core.Patching;
using FfxRandomizer.Core.Randomization;
using FfxRandomizer.Core.Storage;

namespace FfxRandomizer.Core;

public enum RunStep { CheckingGame, ReadingGameData, Randomizing, WritingAndChecking, Saving }

public sealed record RunOutcome(HistoryEntry Entry, RunResult Result);

/// <summary>One full run: randomize from the original data, write it into the game, then record it in history.</summary>
public sealed class RandomizerService(GameInstall game, AppPaths paths, Func<Vbf.VbfArchive, GameData.IGameNames>? names = null)
{
    public GameInstall Game { get; } = game;
    public PatchManager Patcher { get; } = new(game.DataArchivePath, paths);
    public PresetStore Presets { get; } = new(paths);
    public HistoryStore History { get; } = new(paths);

    public RunOutcome Run(RandomizerSettings settings, Seed seed, Preset? preset, IProgress<RunStep>? progress = null)
    {
        progress?.Report(RunStep.CheckingGame);
        if (GameInstall.IsGameRunning()) throw new GameRunningException();

        progress?.Report(RunStep.ReadingGameData);
        var original = Patcher.OpenOriginal();

        progress?.Report(RunStep.Randomizing);
        var result = RandomizerEngine.Run(original, settings, seed, (names ?? (a => GameData.GameText.Load(a)))(original));
        if (result.Problems.Count > 0) throw new ValidationFailedException(result.Problems);
        if (result.Files.Count == 0)
            throw new InvalidOperationException("None of the selected options change the game yet. Turn on an option that's available.");

        progress?.Report(RunStep.WritingAndChecking);
        string fingerprint = Patcher.Apply(result.Files);

        progress?.Report(RunStep.Saving);
        var entry = new HistoryEntry
        {
            Seed = seed.Text,
            Settings = settings.Clone(),
            PresetId = preset?.Id,
            PresetName = preset?.Name,
            FileCount = result.Files.Count,
            PatchedFingerprint = fingerprint,
        };
        if (settings.IsOn("spoiler")) entry.SpoilerPath = SpoilerLog.Write(paths, result, entry);
        History.Add(entry);
        if (preset is not null) Presets.MarkUsed(preset.Id);
        return new RunOutcome(entry, result);
    }

    /// <summary>The history entry whose run is in the game right now, if any.</summary>
    public HistoryEntry? AppliedEntry()
    {
        if (Patcher.GetStatus() != GameDataStatus.Randomized) return null;
        string fingerprint = Patcher.CurrentFingerprint();
        return History.Entries.FirstOrDefault(e => e.PatchedFingerprint == fingerprint);
    }
}
