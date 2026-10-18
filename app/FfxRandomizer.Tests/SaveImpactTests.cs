using FfxRandomizer.Core.Randomization;

namespace FfxRandomizer.Tests;

public class SaveImpactTests
{
    // Options that only steer other options or write files outside the game.
    private static readonly HashSet<string> NoGameEffect = ["sane", "spoiler"];

    [Fact]
    public void Every_option_that_changes_the_game_says_whether_it_needs_a_new_game()
    {
        foreach (var group in OptionCatalog.AllGroups.Where(g => g.Implemented && !NoGameEffect.Contains(g.Id)))
            Assert.True(SaveImpacts.For(group.Id) is not null, $"{group.Id} has no save-impact entry");
        foreach (string id in NoGameEffect) Assert.Null(SaveImpacts.For(id));
    }

    [Fact]
    public void Lists_only_the_switched_on_options_that_need_a_new_game()
    {
        var settings = RandomizerSettings.Defaults();
        settings.Toggles["rewards"] = true;
        settings.Toggles["drops"] = true;
        Assert.Empty(SaveImpacts.NeedingNewGame(settings));

        settings.Choices["gridmode"] = "shuffle";
        settings.Toggles["gearab"] = true;
        Assert.Equal(["Gear abilities", "Grid contents"], SaveImpacts.NeedingNewGame(settings));
    }
}
