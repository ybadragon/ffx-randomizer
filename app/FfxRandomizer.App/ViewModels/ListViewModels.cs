using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FfxRandomizer.Core;
using FfxRandomizer.Core.Storage;

namespace FfxRandomizer.App.ViewModels;

public sealed partial class PresetItemViewModel(Preset preset, MainViewModel main) : ObservableObject
{
    public Preset Preset => preset;
    public string Name => preset.Name;
    public bool BuiltIn => preset.BuiltIn;
    public bool Mine => !preset.BuiltIn;
    public string Summary => Format.FeatureCount(preset.Settings.ActiveCount);
    public string Used => preset.BuiltIn ? "Built in" : preset.LastUsedAt is { } used ? $"Last used {Format.When(used)}" : "Not used yet";
    public string Meta => preset.BuiltIn ? Summary : $"{Summary} · {Used}";
    public IReadOnlyList<string> Tags => Format.Tags(preset.Settings);
    public bool IsLoaded => main.LoadedPreset?.Id == preset.Id;
    public bool NotLoaded => !IsLoaded;

    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _editName = "";

    [RelayCommand] private void Load() => main.LoadPreset(preset);
    [RelayCommand] private void Duplicate() => main.DuplicatePreset(preset);
    [RelayCommand] private void Delete() => main.ConfirmDeletePreset(preset);

    [RelayCommand]
    private void StartRename()
    {
        EditName = preset.Name;
        IsRenaming = true;
    }

    [RelayCommand]
    private void FinishRename()
    {
        if (main.RenamePreset(preset, EditName)) IsRenaming = false;
    }

    [RelayCommand] private void CancelRename() => IsRenaming = false;
}

public sealed partial class HistoryItemViewModel(HistoryEntry entry, bool current, MainViewModel main) : ObservableObject
{
    public HistoryEntry Entry => entry;
    public string Title => entry.PresetName ?? "Custom settings";
    public string Kind => entry.PresetName is null ? "No preset" : "Preset";
    public string Seed => entry.Seed;
    public string Meta => $"{Format.When(entry.CreatedAt)} · Seed ";
    public string Summary => $" · {Format.FeatureCount(entry.Settings.ActiveCount)}";
    public IReadOnlyList<string> Tags => Format.Tags(entry.Settings);
    public bool IsCurrent => current;
    public bool HasSpoiler => entry.SpoilerPath is not null && File.Exists(entry.SpoilerPath);
    public bool FromOtherVersion => entry.AppVersion != AppInfo.Version;
    public string VersionNote => $"Made with v{entry.AppVersion}; replaying may differ slightly";

    [RelayCommand] private void UseSettings() => main.UseHistorySettings(entry, replaySeed: false);
    [RelayCommand] private void ReplaySeed() => main.UseHistorySettings(entry, replaySeed: true);
    [RelayCommand] private void OpenSpoiler() => main.OpenFile(entry.SpoilerPath);
    [RelayCommand] private void Delete() => main.DeleteHistory(entry);

    [ObservableProperty] private bool _seedCopied;

    [RelayCommand]
    private async Task CopySeed()
    {
        try
        {
            System.Windows.Clipboard.SetText(entry.Seed);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another app is holding the clipboard; nothing was copied.
            return;
        }
        SeedCopied = true;
        await Task.Delay(TimeSpan.FromSeconds(2));
        SeedCopied = false;
    }
}

internal static class Format
{
    public static string FeatureCount(int count) => count == 1 ? "1 feature on" : $"{count} features on";

    public static string When(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        if (local.Date == DateTime.Today) return $"Today, {local:t}";
        if (local.Date == DateTime.Today.AddDays(-1)) return $"Yesterday, {local:t}";
        return local.Year == DateTime.Today.Year ? local.ToString("MMM d") : local.ToString("MMM d, yyyy");
    }

    public static IReadOnlyList<string> Tags(Core.Randomization.RandomizerSettings settings) =>
        SpoilerLog.Describe(settings).Select(line => line.Split(':')[0]).Where(t => t != "Keep things sane" && t != "Spoiler log").Take(4).ToList();
}
