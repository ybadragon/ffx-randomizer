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
    bool Implemented = false);

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
            Pick("pstats", "Party stats", "Starting stats for every party member.", null,
                new("orig", "Original"), new("shuffle", "Shuffle between characters"), new("random", "Randomize")),
            Toggle("od", "Starting Overdrive mode", "Each character begins with a random Overdrive mode instead of their usual one."),
        ]),
        new("enemies", "Enemies", "Who you fight, how tough they are and what they leave behind.",
        [
            Toggle("rewards", "Enemy rewards", "What enemies give you after battle.", implemented: true, subOptions:
            [
                new("drops", "Item drops", "Gil, AP and normal, rare and overkill drops"),
                new("steals", "Steals", "What Steal and Mug can take (always succeeds)"),
                new("bribes", "Bribes", "What Bribe gets you"),
                new("geardrops", "Equipment drops", "Weapons and armor enemies drop", Implemented: false),
            ]),
            Toggle("elements", "Elemental affinities",
                "Enemy weaknesses, resistances, immunities and absorptions for Fire, Ice, Thunder, Water and Holy are re-rolled.",
                implemented: true),
            Pick("estats", "Enemy stats", "How strong individual enemies are.", null,
                new("orig", "Original"), new("shuffle", "Shuffle"), new("shufflenorm", "Shuffle, even out defenses"), new("random", "Randomize")),
            Toggle("encounters", "Random encounters", "Which enemies you meet in each area.",
                warning: "One Thunder Plains battle has been reported to crash the game. Turning on No Encounters gets you past it."),
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
            Toggle("gearab", "Gear abilities", "Abilities on weapons and armor are randomized."),
            Toggle("wshops", "Weapon and armor shops", "Each shop sells a random selection of gear.", subOptions:
            [
                new("wprices", "Random prices", "Prices change too"),
            ]),
            Toggle("wstats", "Weapon stats", "Change how weapons deal damage.", subOptions:
            [
                new("crit", "Critical hit chance", "Random crit rate per weapon"),
                new("atk", "Attack power", "Random attack bonus per weapon"),
                new("formula", "Damage formula", "How damage is calculated", "Can make weapons deal 0 damage"),
            ]),
        ]),
        new("aeons", "Aeons", "Aeon stats and the items that power them up.",
        [
            Pick("abase", "Aeon base stats", "Each aeon’s stats when you first get it.", null,
                new("orig", "Original"), new("shuffle", "Shuffle between aeons"), new("random", "Randomize")),
            Pick("ascale", "Aeon stat growth", "How aeon stats grow as your party gets stronger.", null,
                new("orig", "Original"), new("shuffle", "Shuffle between aeons"), new("random", "Randomize")),
            Toggle("aitems", "Aeon power-up items", "The items that raise aeon stats or teach aeons abilities are randomized."),
        ]),
        new("grid", "Sphere Grid", "Reshape the Sphere Grid itself. Works with the Standard and Expert grids.",
        [
            Pick("gridmode", "Grid contents", "What sits on each node of the grid.",
                "Controlled random on the Expert grid can leave out Steal and Use.",
                new("orig", "Original"), new("shuffle", "Shuffle nodes"), new("controlled", "Controlled random"), new("random", "Fully random")),
            Pick("nodes", "Node strength", "Make stat nodes stronger or weaker.", null,
                new("none", "Unchanged"), new("up", "Upgrade nodes"), new("down", "Downgrade nodes")),
            Toggle("locks", "Remove lock nodes", "Every path on the grid is open from the start, no Key Spheres needed."),
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
        new("general", "General", "Settings that apply to the whole randomizer.",
        [
            Toggle("sane", "Keep things sane", "Keeps random values within sensible limits so the game stays playable.", badge: "Recommended", implemented: true),
            Toggle("spoiler", "Spoiler log", "Saves a text file listing everything that was changed. Open it from History any time.", implemented: true),
        ]),
    ];

    public static IEnumerable<OptionGroup> AllGroups => Tabs.SelectMany(t => t.Groups);
}
