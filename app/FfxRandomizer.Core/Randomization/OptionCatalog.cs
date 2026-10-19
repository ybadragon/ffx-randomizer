using FfxRandomizer.Core.GameData;
using FfxRandomizer.Core.GameData.Events;

namespace FfxRandomizer.Core.Randomization;

public sealed record Choice(string Id, string Label);

public sealed record SubOption(string Id, string Label, string Description, string? Warning = null, bool Implemented = true);

/// <summary>
/// One feature box in the options window. A box with <see cref="HasToggle"/> is switched on by its own id;
/// a box with <see cref="Choices"/> stores the picked choice id under its own id (the first choice is the default).
/// </summary>
public sealed record OptionGroup(
    string Id,
    string Title,
    string Description,
    bool HasToggle,
    IReadOnlyList<Choice> Choices,
    IReadOnlyList<SubOption> SubOptions,
    string? Warning = null,
    string? Badge = null,
    bool Implemented = false)
{
    public OptionDropdown? Dropdown { get; init; }
}

/// <summary>
/// A second setting on a feature box, picked from a list: stored under its own id in <see cref="RandomizerSettings.Choices"/>,
/// shown only while the box's choice is one of <see cref="ShownFor"/>. The first choice is the default. Picking a choice
/// listed in <see cref="Warnings"/> shows that warning.
/// </summary>
public sealed record OptionDropdown(
    string Id,
    string Label,
    IReadOnlyList<Choice> Choices,
    IReadOnlySet<string> ShownFor,
    string? Hint = null,
    IReadOnlyDictionary<string, string>? Warnings = null);

public sealed record OptionTab(string Id, string Label, string Blurb, IReadOnlyList<OptionGroup> Groups);

/// <summary>Every option the randomizer offers, in display order. The window and the spoiler log both read this.</summary>
public static class OptionCatalog
{
    private static OptionGroup Toggle(string id, string title, string description, string? badge = null, bool implemented = false,
        string? warning = null, params SubOption[] subOptions) =>
        new(id, title, description, true, [], subOptions, warning, badge, implemented);

    private static OptionGroup Pick(string id, string title, string description, string? warning, params Choice[] choices) =>
        new(id, title, description, false, choices, [], warning);

    public static IReadOnlyList<OptionTab> Tabs { get; } =
    [
        new("party", "Party", "Starting stats, Overdrives and fun tweaks for your characters.",
        [
            Toggle("tidus", "Tidus starts with 9999 HP",
                "A quick way to check the randomizer is working: Tidus has 9999 HP from the very first fight in Zanarkand.",
                badge: "Test mod", implemented: true),
            Pick("pstats", "Party stats", "Starting stats for every party member. Tidus and Auron keep enough Strength, and Wakka and Lulu enough MP, for the opening fights.", null,
                new("orig", "Original"), new("shuffle", "Shuffle between characters"), new("random", "Randomize")) with { Implemented = true },
            Toggle("od", "Starting Overdrive mode", "Each character except Auron begins with a random Overdrive mode instead of their usual one.", implemented: true),
            Toggle("poison", "Poison is deadly", "Poison takes half of max HP every turn instead of a quarter, for the party and aeons.", implemented: true),
        ]),
        new("enemies", "Enemies", "Who you fight, how tough they are and what they leave behind.",
        [
            Toggle("rewards", "Enemy rewards", "What enemies give you after battle.", implemented: true, subOptions:
            [
                new("drops", "Item drops", "Gil, AP and normal, rare and overkill drops"),
                new("steals", "Steals", "What Steal and Mug can take (always succeeds)"),
                new("bribes", "Bribes", "What Bribe gets you"),
                new("geardrops", "Equipment drops", "Drop chance and the abilities on weapons and armor enemies drop"),
            ]),
            Toggle("elements", "Elemental affinities",
                "Enemy weaknesses, resistances, immunities and absorptions for Fire, Ice, Thunder, Water and Holy are re-rolled.",
                implemented: true),
            Pick("estats", "Enemy stats",
                "How strong individual enemies are. Shuffle hands each enemy another enemy’s defenses; Shuffle, even out defenses also adjusts HP, gil and AP to match; Randomize re-rolls stats around their originals.",
                null,
                new("orig", "Original"), new("shuffle", "Shuffle"), new("shufflenorm", "Shuffle, even out defenses"), new("random", "Randomize"))
                with { Implemented = true },
            Pick("encounters", "Random encounters",
                "Which enemies you meet in each area. Every fiend is swapped with another everywhere it appears. The newcomers can keep their own stats, take the stats of the enemy they replace, or land in between.",
                "One Thunder Plains battle has been reported to crash the game with FFXRando. Keep things sane leaves the likely cause (1-HP placeholder enemies like the battle chest) in place; if it still happens, No Encounters gets you past it.",
                new("orig", "Original"), new("random", "Shuffle, own stats"), new("swap", "Shuffle, take replaced stats"), new("scale", "Shuffle, scale stats"))
                with { Implemented = true },
        ]),
        new("items", "Items & Shops", "Treasure chests, shop stock and customization materials.",
        [
            Toggle("field", "Treasure and field items",
                "Chests and pickups give a random item, piece of gear or gil. The Withered Bouquet and Flint in Baaj Temple always stay put, since the story needs them.",
                implemented: true, subOptions:
            [
                new("keyitems", "Include key items", "Key items found in chests, like Al Bhed Primers, move to other chests",
                    "Untested: may move an item the story needs"),
                new("celestial", "Include Celestial items", "Crests, sigils and Celestial weapons"),
                new("brotherhood", "Include Brotherhood", "Tidus’s starting sword"),
            ]),
            Toggle("shops", "Item shops", "Each shop stocks a random selection of items.", implemented: true, subOptions:
            [
                new("shopprices", "Random prices", "Prices change too"),
                new("spheres", "Always sell spheres", "Every shop keeps Power, Mana, Speed and Ability Spheres in stock"),
            ]),
            Toggle("custom", "Customization items", "The items needed to add abilities to weapons and armor are randomized.", implemented: true),
        ]),
        new("gear", "Gear", "Weapon and armor abilities, stats and shops.",
        [
            Toggle("gearab", "Gear abilities",
                "Slots and abilities on every weapon and armor, in shops, chests and your starting gear, are randomized. Celestial weapons and Brotherhood follow the Items tab options.",
                implemented: true),
            Toggle("wshops", "Weapon and armor shops", "Each shop sells a random selection of gear.", implemented: true, subOptions:
            [
                new("wprices", "Random prices", "Prices change too"),
            ]),
            Toggle("wstats", "Weapon stats", "Change how weapons deal damage. Also applies to equipment enemies drop.", implemented: true, subOptions:
            [
                new("crit", "Critical hit chance", "Random crit rate per weapon"),
                new("atk", "Attack power", "Random attack bonus per weapon"),
                new("formula", "Damage formula", "How damage is calculated",
                    "With Keep things sane off, weapons can heal enemies or deal 0 damage"),
            ]),
        ]),
        new("aeons", "Aeons", "Aeon stats and the items that power them up.",
        [
            Pick("abase", "Aeon base stats", "The lowest stats each aeon can have, which rise as you fight more battles.", null,
                new("orig", "Original"), new("shuffle", "Shuffle between aeons"), new("random", "Randomize")) with { Implemented = true },
            Pick("ascale", "Aeon stat growth", "How aeon stats grow with Yuna’s.", null,
                new("orig", "Original"), new("shuffle", "Shuffle between aeons"), new("random", "Randomize")) with { Implemented = true },
            Toggle("aitems", "Aeon power-up items", "The items that raise aeon stats or teach aeons abilities are randomized.", implemented: true),
        ]),
        new("grid", "Sphere Grid", "Reshape the Sphere Grid itself. Works with the Standard and Expert grids. Only takes effect when you start a new game.",
        [
            Pick("gridmode", "Grid contents",
                "What sits on each node of the grid. Shuffle moves the original nodes around; controlled random places every ability exactly once among random stat nodes; fully random can repeat or leave out abilities. With Keep things sane on, lock nodes stay where they are. Rikku starts knowing Steal and Use, and Kimahri, Wakka and Lulu the abilities their tutorials need.",
                null,
                new("orig", "Original"), new("shuffle", "Shuffle nodes"), new("controlled", "Controlled random"), new("random", "Fully random"))
                with { Implemented = true },
            Pick("gridfill", "Empty or full grid", "Clear every stat node so you have to fill the grid yourself with spheres, or fill every empty node with a random stat node.", null,
                new("none", "Unchanged"), new("empty", "Empty grid"), new("full", "Full grid")) with { Implemented = true },
            Pick("nodes", "Node strength", "Make stat nodes stronger or weaker.", null,
                new("none", "Unchanged"), new("up", "Upgrade nodes"), new("down", "Downgrade nodes")) with { Implemented = true },
            Toggle("locks", "Remove lock nodes", "Every path on the grid is open from the start, no Key Spheres needed.", implemented: true),
        ]),
        new("blitzball", "Blitzball", "The players you can scout and sign for the Besaid Aurochs.",
        [
            new("blitz", "Blitzball player stats",
                "How every Blitzball player’s HP, Speed, Endurance, Attack, Pass, Block, Shot and Catch grow with their level.",
                false,
                [new("orig", "Original"), new("shuffle", "Shuffle between players"), new("random", "Randomize")],
                [new("blitzstars", "Include Tidus and Wakka", "Their stats change too. Off keeps the Aurochs’ two stars as they are")],
                Implemented: true),
        ]),
        new("story", "Story", "How the story plays out.",
        [
            Pick("cutscenes", "Skip cutscenes",
                "Covers one scene so far: the Zanarkand Ruins campfire that opens the game. With Hold button to skip (recommended), a prompt fills while you hold the skip button during the scene; after about a second the scene ends and the story moves on. The late-game return to the campfire isn't skipped.",
                "Experimental: only the opening scene has been played through. If a skip ever leaves you stuck, use Restore original.",
                new(Cutscenes.Original, "Original"), new(Cutscenes.Hold, "Hold button to skip"), new(Cutscenes.Always, "Always skip"))
                with
                {
                    Implemented = true,
                    Badge = "Experimental",
                    Dropdown = new OptionDropdown("cutscenebutton", "Skip button",
                        [.. SkipButtons.All.Select(b => new Choice(b.Id, b.Label))],
                        ShownFor: new HashSet<string> { Cutscenes.Hold },
                        Hint: "Xbox buttons in brackets. Use Record to press the button on your controller instead. Keyboard keys can't be recorded, since the game lets you rebind them.",
                        Warnings: SkipButtons.All.Where(b => b.AdvancesDialogue).ToDictionary(b => b.Id,
                            b => $"{b.Label} also advances dialogue, so holding it may skip lines before the scene skips.")),
                },
        ]),
        new("general", "General", "Settings that apply to the whole randomizer.",
        [
            Toggle("sane", "Keep things sane", "Keeps random values within sensible limits so the game stays playable.", badge: "Recommended", implemented: true),
            Toggle("spoiler", "Spoiler log", "Saves a text file listing everything that was changed. Open it from History any time.", implemented: true),
        ]),
    ];

    public static IEnumerable<OptionGroup> AllGroups => Tabs.SelectMany(t => t.Groups);
}
