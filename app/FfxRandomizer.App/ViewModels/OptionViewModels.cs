using CommunityToolkit.Mvvm.ComponentModel;
using FfxRandomizer.Core.Randomization;

namespace FfxRandomizer.App.ViewModels;

public sealed partial class ChoiceViewModel(Choice choice, GroupViewModel owner) : ObservableObject
{
    public string Id => choice.Id;
    public string Label => choice.Label;
    public string GroupName => owner.Id;

    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) owner.Select(this);
    }
}

public sealed partial class SubOptionViewModel(SubOption option, Action changed) : ObservableObject
{
    public string Id => option.Id;
    public string Label => option.Label;
    public string Description => option.Description;
    public string? Warning => option.Warning;
    public bool HasWarning => option.Warning is not null;
    public bool Implemented => option.Implemented;
    public bool ComingSoon => !option.Implemented;

    [ObservableProperty] private bool _isOn;

    partial void OnIsOnChanged(bool value) => changed();
}

/// <summary>One feature box in an options tab.</summary>
public sealed partial class GroupViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _loading;

    public GroupViewModel(OptionGroup group, Action changed)
    {
        Group = group;
        _changed = changed;
        Choices = group.Choices.Select(c => new ChoiceViewModel(c, this)).ToList();
        SubOptions = group.SubOptions.Select(s => new SubOptionViewModel(s, Changed)).ToList();
        SaveImpact = SaveImpacts.For(group.Id);
    }

    public OptionGroup Group { get; }
    public string Id => Group.Id;
    public string Title => Group.Title;
    public string Description => Group.Description;
    public bool HasToggle => Group.HasToggle;
    public bool Implemented => Group.Implemented;
    public string? Badge => Group.Implemented ? Group.Badge : "Coming soon";
    public bool HasBadge => Badge is not null;
    public bool BadgeIsComingSoon => !Group.Implemented;

    /// <summary>Whether this option reaches a game already in progress; null for options that don't change the game.</summary>
    public SaveImpactInfo? SaveImpact { get; }
    public bool ShowsOnExistingSaves => Group.Implemented && SaveImpact?.Impact == Core.Randomization.SaveImpact.ExistingSaves;
    public bool ShowsPartly => Group.Implemented && SaveImpact?.Impact == Core.Randomization.SaveImpact.Partly;
    public bool ShowsNewGameOnly => Group.Implemented && SaveImpact?.Impact == Core.Randomization.SaveImpact.NewGameOnly;
    public IReadOnlyList<ChoiceViewModel> Choices { get; }
    public IReadOnlyList<SubOptionViewModel> SubOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBody), nameof(ShowChoices), nameof(ShowSubOptions), nameof(ShowWarning))]
    private bool _isOn;

    public bool ShowBody => !HasToggle || IsOn;
    public bool ShowChoices => ShowBody && Choices.Count > 0;
    public bool ShowSubOptions => ShowBody && SubOptions.Count > 0;
    public bool ShowWarning => ShowBody && Group.Warning is not null;
    public string? Warning => Group.Warning;

    partial void OnIsOnChanged(bool value) => Changed();

    internal void Select(ChoiceViewModel selected)
    {
        foreach (var choice in Choices.Where(c => c != selected)) choice.IsSelected = false;
        Changed();
    }

    public void Load(RandomizerSettings settings)
    {
        _loading = true;
        IsOn = settings.IsOn(Id);
        if (Choices.Count > 0)
        {
            string picked = settings.ChoiceOf(Group);
            foreach (var choice in Choices) choice.IsSelected = choice.Id == picked;
        }
        foreach (var sub in SubOptions) sub.IsOn = settings.IsOn(sub.Id);
        _loading = false;
    }

    public void Save(RandomizerSettings settings)
    {
        if (HasToggle) settings.Toggles[Id] = IsOn;
        if (Choices.FirstOrDefault(c => c.IsSelected) is { } picked) settings.Choices[Id] = picked.Id;
        foreach (var sub in SubOptions) settings.Toggles[sub.Id] = sub.IsOn;
    }

    private void Changed()
    {
        if (!_loading) _changed();
    }
}

public sealed partial class TabViewModel(string id, string label, string blurb, IReadOnlyList<GroupViewModel> groups, TabKind kind,
    Action<TabViewModel> selected) : ObservableObject
{
    partial void OnIsSelectedChanged(bool value)
    {
        if (value) selected(this);
    }

    public string Id => id;
    public string Label => label;
    public string Blurb => blurb;
    public IReadOnlyList<GroupViewModel> Groups => groups;
    public TabKind Kind => kind;
    public bool StartsSection => kind == TabKind.Presets;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string? _countLabel;
}

public enum TabKind { Options, Presets, History }
