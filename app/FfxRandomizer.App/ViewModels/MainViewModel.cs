using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FfxRandomizer.Core;
using FfxRandomizer.Core.Game;
using FfxRandomizer.Core.Patching;
using FfxRandomizer.Core.Randomization;
using FfxRandomizer.Core.Storage;
using Microsoft.Win32;

namespace FfxRandomizer.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppPaths _paths = AppPaths.Default;
    private readonly AppSettings _appSettings;
    private readonly PresetStore _presets;
    private readonly HistoryStore _history;
    private RandomizerService? _service;
    private RandomizerSettings _baseline;

    public MainViewModel()
    {
        _appSettings = AppSettings.Load(_paths);
        _presets = new PresetStore(_paths);
        _history = new HistoryStore(_paths);

        var groups = new List<GroupViewModel>();
        Tabs = [];
        foreach (var tab in OptionCatalog.Tabs)
        {
            var tabGroups = tab.Groups.Select(g => new GroupViewModel(g, OnSettingsChanged)).ToList();
            groups.AddRange(tabGroups);
            Tabs.Add(new TabViewModel(tab.Id, tab.Label, tab.Blurb, tabGroups, TabKind.Options, SelectTab));
        }
        Tabs.Add(new TabViewModel("presets", "Presets",
            "Load, rename and organize saved settings. Presets keep settings only, so each run gets a new seed unless you set one.", [], TabKind.Presets, SelectTab));
        Tabs.Add(new TabViewModel("history", "History",
            "Every run is saved here with its exact seed and settings, so any game you have played can be made again.", [], TabKind.History, SelectTab));
        _groups = groups;

        LoadedPreset = _appSettings.LastPresetId is { } id ? _presets.Find(id) : null;
        ApplyToControls(_appSettings.LastSettings ?? LoadedPreset?.Settings ?? RandomizerSettings.Defaults());
        _baseline = _appSettings.LastPresetBaseline ?? LoadedPreset?.Settings ?? CurrentSettings();
        SelectTab(Tabs[0]);
        ConnectGame(_appSettings.GameFolder is { } folder ? GameInstall.FromFolder(folder) : GameInstall.FindSteamInstall());
        RefreshLists();
    }

    private readonly IReadOnlyList<GroupViewModel> _groups;

    public string Version { get; } = $"v{AppInfo.Version}";

    public ObservableCollection<TabViewModel> Tabs { get; }
    public ObservableCollection<PresetItemViewModel> MyPresets { get; } = [];
    public ObservableCollection<PresetItemViewModel> BuiltInPresets { get; } = [];
    public ObservableCollection<HistoryItemViewModel> History { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOptionsTab), nameof(IsPresetsTab), nameof(IsHistoryTab))]
    private TabViewModel? _selectedTab;

    public bool IsOptionsTab => SelectedTab?.Kind == TabKind.Options;
    public bool IsPresetsTab => SelectedTab?.Kind == TabKind.Presets;
    public bool IsHistoryTab => SelectedTab?.Kind == TabKind.History;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PresetLabel))]
    private Preset? _loadedPreset;

    [ObservableProperty] private bool _isModified;
    public string PresetLabel => LoadedPreset?.Name ?? "None";

    [ObservableProperty] private bool _useFixedSeed;
    [ObservableProperty] private string _seedText = "";

    [ObservableProperty] private string _gameTitle = "FINAL FANTASY X HD";
    [ObservableProperty] private string _gameDetail = "Looking for your Steam copy";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _statusIsRandomized;
    [ObservableProperty] private bool _gameFound;

    /// <summary>The run screen, a problem message or a name prompt shown over the window; null when none.</summary>
    [ObservableProperty] private object? _overlay;
    public bool HasOverlay => Overlay is not null;
    partial void OnOverlayChanged(object? value) => OnPropertyChanged(nameof(HasOverlay));

    // ---------- tabs and settings ----------

    private void SelectTab(TabViewModel tab)
    {
        if (SelectedTab == tab) return;
        SelectedTab = tab;
        foreach (var t in Tabs) t.IsSelected = t == tab;
    }

    public RandomizerSettings CurrentSettings()
    {
        var settings = new RandomizerSettings();
        foreach (var group in _groups) group.Save(settings);
        return settings;
    }

    private void ApplyToControls(RandomizerSettings settings)
    {
        foreach (var group in _groups) group.Load(settings);
        OnSettingsChanged();
    }

    private void OnSettingsChanged()
    {
        if (Tabs is null || _groups is null) return;
        var settings = CurrentSettings();
        foreach (var tab in Tabs.Where(t => t.Kind == TabKind.Options))
        {
            int count = tab.Groups.Count(g => settings.IsActive(g.Group));
            tab.CountLabel = count > 0 ? $"{count} on" : null;
        }
        IsModified = LoadedPreset is not null && _baseline is not null && !settings.SameAs(_baseline);
        _appSettings.LastSettings = settings;
        SaveAppSettings();
    }

    private void SaveAppSettings()
    {
        _appSettings.LastPresetId = LoadedPreset?.Id;
        _appSettings.LastPresetBaseline = LoadedPreset is null ? null : _baseline;
        try { _appSettings.Save(_paths); } catch (IOException) { }
    }

    // ---------- presets ----------

    public void LoadPreset(Preset preset)
    {
        LoadedPreset = preset;
        _baseline = preset.Settings.Clone();
        ApplyToControls(preset.Settings);
        UseFixedSeed = false;
        RefreshLists();
    }

    [RelayCommand]
    public void UpdateLoadedPreset()
    {
        if (LoadedPreset is not { BuiltIn: false } preset) return;
        var settings = CurrentSettings();
        _presets.Update(preset.Id, settings);
        _baseline = settings.Clone();
        OnSettingsChanged();
        RefreshLists();
    }

    /// <summary>Saves the current settings under a new name and loads it. Returns null if the name is unusable.</summary>
    public Preset? SaveNewPreset(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var preset = _presets.Add(name, CurrentSettings());
        LoadedPreset = preset;
        _baseline = preset.Settings.Clone();
        OnSettingsChanged();
        RefreshLists();
        return preset;
    }

    [RelayCommand]
    private void PromptNewPreset()
    {
        Overlay = new NamePromptViewModel("Save current settings as a new preset", "Save preset",
            LoadedPreset is { } p ? $"{p.Name} copy" : "",
            name =>
            {
                if (SaveNewPreset(name) is null) return false;
                CloseOverlay();
                return true;
            },
            CloseOverlay);
    }

    public void DuplicatePreset(Preset preset)
    {
        _presets.Duplicate(preset.Id);
        RefreshLists();
    }

    public bool RenamePreset(Preset preset, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        try
        {
            _presets.Rename(preset.Id, name);
        }
        catch (InvalidOperationException e)
        {
            ShowProblem("Pick another name", e.Message, ProblemTone.Warning, "OK", CloseOverlay);
            return false;
        }
        OnPropertyChanged(nameof(PresetLabel));
        RefreshLists();
        return true;
    }

    public void ConfirmDeletePreset(Preset preset)
    {
        ShowProblem($"Delete \"{preset.Name}\"?", "The preset is removed. Runs made with it stay in History.", ProblemTone.Warning,
            "Delete preset", () =>
            {
                _presets.Delete(preset.Id);
                if (LoadedPreset?.Id == preset.Id) LoadedPreset = null;
                OnSettingsChanged();
                RefreshLists();
                CloseOverlay();
            }, "Keep it", CloseOverlay);
    }

    // ---------- history ----------

    public void UseHistorySettings(HistoryEntry entry, bool replaySeed)
    {
        var preset = entry.PresetId is { } id ? _presets.Find(id) : null;
        LoadedPreset = preset;
        _baseline = preset?.Settings.Clone() ?? entry.Settings.Clone();
        ApplyToControls(entry.Settings);
        UseFixedSeed = replaySeed;
        SeedText = replaySeed ? entry.Seed : "";
        SelectTab(Tabs[0]);
        RefreshLists();
    }

    public void DeleteHistory(HistoryEntry entry)
    {
        _history.Delete(entry.Id);
        RefreshLists();
    }

    private void RefreshLists()
    {
        MyPresets.Clear();
        foreach (var p in _presets.Mine) MyPresets.Add(new PresetItemViewModel(p, this));
        BuiltInPresets.Clear();
        foreach (var p in PresetStore.BuiltIns) BuiltInPresets.Add(new PresetItemViewModel(p, this));

        string? current = _service?.Patcher.GetStatus() == GameDataStatus.Randomized ? _service.Patcher.CurrentFingerprint() : null;
        History.Clear();
        foreach (var e in _history.Entries) History.Add(new HistoryItemViewModel(e, current is not null && e.PatchedFingerprint == current, this));

        Tabs.First(t => t.Kind == TabKind.Presets).CountLabel = MyPresets.Count > 0 ? MyPresets.Count.ToString() : null;
        Tabs.First(t => t.Kind == TabKind.History).CountLabel = History.Count > 0 ? History.Count.ToString() : null;
        SaveAppSettings();
    }

    // ---------- the game ----------

    private void ConnectGame(GameInstall? game)
    {
        _service = game is null ? null : new RandomizerService(game, _paths);
        GameFound = game is not null;
        RefreshStatus();
        if (game is null) ShowGameNotFound();
    }

    private void RefreshStatus()
    {
        if (_service is null)
        {
            GameDetail = "Not found";
            StatusText = "";
            return;
        }
        GameDetail = _appSettings.GameFolder is null ? "Steam copy found automatically" : _service.Game.Directory;
        try
        {
            var status = _service.Patcher.GetStatus();
            StatusIsRandomized = status == GameDataStatus.Randomized;
            var applied = StatusIsRandomized ? _service.AppliedEntry() : null;
            StatusText = status switch
            {
                GameDataStatus.Randomized => "Randomized",
                _ => "Original game",
            };
            if (applied is not null) StatusText = $"Randomized · seed {applied.Seed}";
        }
        catch (IOException)
        {
            StatusText = "Game files busy";
        }
        RefreshListsSafe();
    }

    private void RefreshListsSafe()
    {
        if (MyPresets is not null) RefreshLists();
    }

    private void ShowGameNotFound() =>
        ShowProblem("Couldn't find FINAL FANTASY X",
            "The Steam copy of FINAL FANTASY X/X-2 HD Remaster wasn't found. Choose the game's folder, the one that contains FFX.exe.",
            ProblemTone.Warning, "Choose game folder", ChooseGameFolder);

    private void ChooseGameFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose the FINAL FANTASY X/X-2 HD Remaster folder (contains FFX.exe)" };
        if (dialog.ShowDialog() != true) return;
        var game = GameInstall.FromFolder(dialog.FolderName);
        if (game is null)
        {
            ShowProblem("That folder isn't the game", "Choose the folder that contains FFX.exe and a data folder with FFX_Data.vbf.",
                ProblemTone.Warning, "Choose game folder", ChooseGameFolder);
            return;
        }
        _appSettings.GameFolder = dialog.FolderName;
        SaveAppSettings();
        CloseOverlay();
        ConnectGame(game);
    }

    private bool ReadyToChangeGame()
    {
        if (_service is null)
        {
            ShowGameNotFound();
            return false;
        }
        if (GameInstall.IsGameRunning())
        {
            ShowGameRunning();
            return false;
        }
        if (!_service.Game.CanWriteArchive())
        {
            ShowProblem("Windows needs your permission",
                "Your game is installed in a protected folder. Restart FFX Randomizer with permission to change it, then click Randomize again. The game itself never needs to run as administrator.",
                ProblemTone.Info, "Restart with permission", RestartElevated, "Cancel", CloseOverlay);
            return false;
        }
        return true;
    }

    private void ShowGameRunning() =>
        ShowProblem("FINAL FANTASY X is running",
            "The game files can't be changed while the game or its launcher is open. Nothing has been changed yet.",
            ProblemTone.Warning, "OK", CloseOverlay);

    private static void RestartElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            System.Windows.Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The person clicked No on the Windows prompt; stay as we are.
        }
    }

    // ---------- randomize and restore ----------

    [RelayCommand]
    private async Task RandomizeAsync()
    {
        if (!ReadyToChangeGame()) return;
        var settings = CurrentSettings();
        var seed = UseFixedSeed && !string.IsNullOrWhiteSpace(SeedText) ? new Seed(SeedText.Trim()) : Seed.NewRandom();
        var preset = LoadedPreset;
        bool modified = IsModified;
        // A run only counts as "from" a preset if it used that preset's exact settings.
        var presetForHistory = preset is not null && !modified ? preset : null;

        var run = new RunViewModel(this, seed.Text);
        Overlay = run;
        var progress = new Progress<RunStep>(run.Report);
        try
        {
            var outcome = await Task.Run(() => _service!.Run(settings, seed, presetForHistory, progress));
            run.Finish(outcome, preset, modified);
        }
        catch (GameRunningException)
        {
            ShowGameRunning();
        }
        catch (PatchFailedException e)
        {
            ShowFailure(e.Message, e);
        }
        catch (ValidationFailedException e)
        {
            ShowFailure(e.Message, new Exception(string.Join(Environment.NewLine, e.Problems), e));
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            ShowFailure(e.Message, e);
        }
        RefreshStatus();
    }

    private void ShowFailure(string message, Exception e) =>
        ShowProblem("Randomizing didn't finish", message, ProblemTone.Error, "OK", CloseOverlay,
            "Copy error details", () => System.Windows.Clipboard.SetText($"FFX Randomizer {AppInfo.Version}\n{e}"));

    [RelayCommand]
    private void Restore()
    {
        if (_service is null)
        {
            ShowGameNotFound();
            return;
        }
        if (_service.Patcher.GetStatus() != GameDataStatus.Randomized)
        {
            ShowProblem("Your game is already original", "There's no randomization to remove.", ProblemTone.Info, "OK", CloseOverlay);
            return;
        }
        ShowProblem("Restore original game?",
            "This removes the randomizer and puts every game file back exactly as Steam installed it. Your saves and your History aren't touched.",
            ProblemTone.Info, "Restore original", async () =>
            {
                if (!ReadyToChangeGame()) return;
                try
                {
                    await Task.Run(() => _service.Patcher.Restore());
                    CloseOverlay();
                }
                catch (Exception e) when (e is GameRunningException or IOException or InvalidDataException)
                {
                    ShowFailure(e.Message, e);
                }
                RefreshStatus();
            }, "Keep randomized", CloseOverlay);
    }

    public void LaunchGame()
    {
        Process.Start(new ProcessStartInfo($"steam://rungameid/{GameInstall.SteamAppId}") { UseShellExecute = true });
        CloseOverlay();
    }

    public void OpenFile(string? path)
    {
        if (path is null || !File.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No program is set up to open .json files on this PC.
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
    }

    public void CloseOverlay() => Overlay = null;

    private void ShowProblem(string title, string message, ProblemTone tone, string action, Action onAction,
        string? secondary = null, Action? onSecondary = null) =>
        Overlay = new ProblemViewModel(title, message, tone, action, onAction, secondary, onSecondary);
}
