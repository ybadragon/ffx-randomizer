namespace FfxRandomizer.Core.Randomization;

/// <summary>Whether an option reaches a game already in progress, or only one started after randomizing.</summary>
public enum SaveImpact
{
    /// <summary>The game reads this data live, so loading an existing save picks it up.</summary>
    ExistingSaves,
    /// <summary>Some of it is copied into the save (e.g. gear you already own), the rest applies right away.</summary>
    Partly,
    /// <summary>Copied into the save at New Game, so only a new game sees it.</summary>
    NewGameOnly,
}

public sealed record SaveImpactInfo(SaveImpact Impact, string Note)
{
    public string Label => Impact switch
    {
        SaveImpact.ExistingSaves => "Works on existing saves",
        SaveImpact.Partly => "Partly needs a new game",
        _ => "New game only",
    };
}

/// <summary>Which options need a new game. One table, so it is easy to correct as in-game testing confirms each entry.</summary>
public static class SaveImpacts
{
    private static readonly Dictionary<string, SaveImpactInfo> ByGroup = new()
    {
        ["tidus"] = new(SaveImpact.NewGameOnly, "Party stats are copied into your save when you start a new game."),
        ["pstats"] = new(SaveImpact.NewGameOnly, "Party stats are copied into your save when you start a new game."),
        ["od"] = new(SaveImpact.NewGameOnly, "Overdrive modes are stored in your save."),
        ["poison"] = new(SaveImpact.NewGameOnly, "Poison damage is part of each character's data in your save."),
        ["rewards"] = new(SaveImpact.ExistingSaves, "Enemy data is read at the start of every battle."),
        ["elements"] = new(SaveImpact.ExistingSaves, "Enemy data is read at the start of every battle."),
        ["estats"] = new(SaveImpact.ExistingSaves, "Enemy data is read at the start of every battle."),
        ["encounters"] = new(SaveImpact.ExistingSaves, "Battles are read when they start."),
        ["field"] = new(SaveImpact.ExistingSaves, "Chests you haven't opened yet give the new items. Opened chests stay opened."),
        ["shops"] = new(SaveImpact.ExistingSaves, "Shops are read when you open them."),
        ["custom"] = new(SaveImpact.ExistingSaves, "Customization recipes are read when you open the menu."),
        ["gearab"] = new(SaveImpact.Partly, "Gear you already own keeps its abilities, and starting gear needs a new game. Gear from shops and enemies changes right away."),
        ["wshops"] = new(SaveImpact.ExistingSaves, "Shops are read when you open them."),
        ["wstats"] = new(SaveImpact.Partly, "Gear you already own keeps its stats, and starting gear needs a new game. Gear from shops and enemies changes right away."),
        ["abase"] = new(SaveImpact.ExistingSaves, "Aeon stats are worked out from these tables as Yuna's stats change."),
        ["ascale"] = new(SaveImpact.ExistingSaves, "Aeon stats are worked out from these tables as Yuna's stats change."),
        ["aitems"] = new(SaveImpact.ExistingSaves, "Costs are read when you open the aeon menu. Boosts already bought stay."),
        ["gridmode"] = new(SaveImpact.NewGameOnly, "The whole Sphere Grid is stored in your save."),
        ["gridfill"] = new(SaveImpact.NewGameOnly, "The whole Sphere Grid is stored in your save."),
        ["nodes"] = new(SaveImpact.NewGameOnly, "The whole Sphere Grid is stored in your save."),
        ["locks"] = new(SaveImpact.NewGameOnly, "The whole Sphere Grid is stored in your save."),
        ["blitz"] = new(SaveImpact.ExistingSaves, "Your save keeps each player's level; their stats come from these curves."),
    };

    /// <summary>Null for options that change nothing in the game (like the spoiler log).</summary>
    public static SaveImpactInfo? For(string groupId) => ByGroup.GetValueOrDefault(groupId);

    /// <summary>Titles of the options switched on in <paramref name="settings"/> that need a new game, fully or partly.</summary>
    public static IReadOnlyList<string> NeedingNewGame(RandomizerSettings settings) =>
        OptionCatalog.AllGroups
            .Where(g => g.Implemented && settings.IsActive(g) && For(g.Id) is { Impact: not SaveImpact.ExistingSaves })
            .Select(g => g.Title)
            .ToList();
}
