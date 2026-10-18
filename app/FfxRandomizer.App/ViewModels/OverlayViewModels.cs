using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FfxRandomizer.Core;
using FfxRandomizer.Core.Storage;

namespace FfxRandomizer.App.ViewModels;

public enum StepState { Pending, Current, Done }

public sealed partial class RunStepViewModel(RunStep step, string label, string detail) : ObservableObject
{
    public RunStep Step => step;
    public string Label => label;

    [ObservableProperty] private string _detail = detail;
    [ObservableProperty] private StepState _state;
}

/// <summary>The Randomize run: progress steps, then the finished screen.</summary>
public sealed partial class RunViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public RunViewModel(MainViewModel main, string seed)
    {
        _main = main;
        Steps =
        [
            new(RunStep.CheckingGame, "Checking your game", "Making sure the game is closed"),
            new(RunStep.ReadingGameData, "Reading game data", "Straight from the game files, nothing extra to download"),
            new(RunStep.Randomizing, "Randomizing", $"Seed {seed}"),
            new(RunStep.WritingAndChecking, "Writing changes and checking them", "Every changed file must read back exactly"),
            new(RunStep.Saving, "Saving to History", "So you can replay this game later"),
        ];
    }

    public ObservableCollection<RunStepViewModel> Steps { get; }

    [ObservableProperty] private double _percent;
    [ObservableProperty] private bool _isFinished;
    [ObservableProperty] private HistoryEntry? _entry;
    [ObservableProperty] private IReadOnlyList<string> _changes = [];

    // Preset prompt on the finished screen.
    [ObservableProperty] private string? _presetName;
    [ObservableProperty] private bool _presetModified;
    [ObservableProperty] private bool _noPreset;
    [ObservableProperty] private bool _presetMatches;
    [ObservableProperty] private string _newPresetName = "";
    [ObservableProperty] private string? _presetMessage;

    public string SeedLine => Entry is null ? "" : $"Seed {Entry.Seed} · {Entry.FileCount} game files changed and checked";
    public bool HasSpoiler => Entry?.SpoilerPath is not null;

    public void Report(RunStep step)
    {
        foreach (var s in Steps)
            s.State = s.Step < step ? StepState.Done : s.Step == step ? StepState.Current : StepState.Pending;
        Percent = 100.0 * (int)step / Steps.Count;
    }

    public void Finish(RunOutcome outcome, Preset? loaded, bool modified)
    {
        foreach (var s in Steps) s.State = StepState.Done;
        Percent = 100;
        Entry = outcome.Entry;
        Changes = SpoilerLog.Describe(outcome.Result.Settings).ToList();
        PresetName = loaded?.Name;
        NoPreset = loaded is null;
        PresetModified = loaded is not null && modified;
        PresetMatches = loaded is not null && !modified;
        OnPropertyChanged(nameof(SeedLine));
        OnPropertyChanged(nameof(HasSpoiler));
        IsFinished = true;
    }

    [RelayCommand] private void LaunchGame() => _main.LaunchGame();
    [RelayCommand] private void OpenSpoiler() => _main.OpenFile(Entry?.SpoilerPath);
    [RelayCommand] private void Close() => _main.CloseOverlay();

    [RelayCommand]
    private void UpdatePreset()
    {
        _main.UpdateLoadedPreset();
        PresetModified = false;
        PresetMatches = true;
        PresetMessage = $"Saved changes to \"{PresetName}\".";
    }

    [RelayCommand]
    private void SaveAsNewPreset()
    {
        if (_main.SaveNewPreset(NewPresetName) is not { } preset) return;
        PresetName = preset.Name;
        PresetModified = false;
        NoPreset = false;
        PresetMatches = true;
        PresetMessage = $"Saved as \"{preset.Name}\".";
    }
}

public enum ProblemTone { Warning, Info, Error }

/// <summary>A message that needs the player's attention, with one main action.</summary>
public sealed partial class ProblemViewModel(string title, string message, ProblemTone tone, string actionLabel, Action action,
    string? secondaryLabel = null, Action? secondary = null) : ObservableObject
{
    public string Title => title;
    public string Message => message;
    public ProblemTone Tone => tone;
    public string ActionLabel => actionLabel;
    public string? SecondaryLabel => secondaryLabel;
    public bool HasSecondary => secondaryLabel is not null;

    [RelayCommand] private void Act() => action();
    [RelayCommand] private void Secondary() => secondary?.Invoke();
}

/// <summary>Asks for a name, e.g. for a new preset.</summary>
public sealed partial class NamePromptViewModel(string title, string confirmLabel, string initial, Func<string, bool> confirm, Action cancel) : ObservableObject
{
    public string Title => title;
    public string ConfirmLabel => confirmLabel;

    [ObservableProperty] private string _name = initial;

    [RelayCommand] private void Confirm() => confirm(Name);
    [RelayCommand] private void Cancel() => cancel();
}
