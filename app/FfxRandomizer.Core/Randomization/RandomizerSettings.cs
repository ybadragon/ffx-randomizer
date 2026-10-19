namespace FfxRandomizer.Core.Randomization;

/// <summary>The player's choices, keyed by the ids in <see cref="OptionCatalog"/>. Saved in presets and history.</summary>
public sealed class RandomizerSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public SortedDictionary<string, bool> Toggles { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, string> Choices { get; set; } = new(StringComparer.Ordinal);

    public static RandomizerSettings Defaults()
    {
        var settings = new RandomizerSettings();
        settings.Toggles["sane"] = true;
        settings.Toggles["spoiler"] = true;
        return settings;
    }

    public bool IsOn(string id) => Toggles.TryGetValue(id, out bool on) && on;

    public string ChoiceOf(OptionGroup group) =>
        Choices.TryGetValue(group.Id, out string? id) && group.Choices.Any(c => c.Id == id) ? id : group.Choices[0].Id;

    public string ChoiceOf(OptionDropdown dropdown) =>
        Choices.TryGetValue(dropdown.Id, out string? id) && dropdown.Choices.Any(c => c.Id == id) ? id : dropdown.Choices[0].Id;

    /// <summary>Whether a box's dropdown applies: the box is in use with one of the choices the dropdown belongs to.</summary>
    public bool DropdownApplies(OptionGroup group) =>
        group.Dropdown is { } d && IsActive(group) && (group.Choices.Count == 0 || d.ShownFor.Contains(ChoiceOf(group)));

    /// <summary>Whether a feature box changes anything: switched on, or a choice other than its first.</summary>
    public bool IsActive(OptionGroup group) =>
        group.HasToggle ? IsOn(group.Id) : group.Choices.Count > 0 && ChoiceOf(group) != group.Choices[0].Id;

    public RandomizerSettings Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        Toggles = new SortedDictionary<string, bool>(Toggles, StringComparer.Ordinal),
        Choices = new SortedDictionary<string, string>(Choices, StringComparer.Ordinal),
    };

    /// <summary>Compares what the settings do, ignoring switched-off entries and default choices.</summary>
    public bool SameAs(RandomizerSettings other) => Normalized().SequenceEqual(other.Normalized());

    private IEnumerable<string> Normalized()
    {
        foreach (var group in OptionCatalog.AllGroups)
        {
            if (group.HasToggle) yield return $"{group.Id}={IsOn(group.Id)}";
            if (group.Choices.Count > 0) yield return $"{group.Id}={ChoiceOf(group)}";
            if (group.Dropdown is { } d) yield return $"{d.Id}={ChoiceOf(d)}";
            foreach (var sub in group.SubOptions) yield return $"{sub.Id}={IsOn(sub.Id)}";
        }
    }

    public int ActiveCount => OptionCatalog.AllGroups.Count(IsActive);
}
