namespace FfxRandomizer.Core.Storage;

/// <summary>
/// Where the app keeps its files. The state file and header backup are shared with FFX-Randomizer.bat,
/// so both tools agree on whether the game is currently randomized.
/// </summary>
public sealed class AppPaths(string root)
{
    public static AppPaths Default { get; } =
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FFX Randomizer"));

    public string Root { get; } = root;
    public string StatePath => Path.Combine(Root, "state.json");
    public string HeaderBackupPath => Path.Combine(Root, "backup", "FFX_Data.header.bin");
    public string PresetsPath => Path.Combine(Root, "presets.json");
    public string HistoryPath => Path.Combine(Root, "history.json");
    public string SpoilerDirectory => Path.Combine(Root, "spoilers");
}
