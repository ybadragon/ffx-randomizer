using System.Text.Json.Nodes;
using FfxRandomizer.Core.Game;
using FfxRandomizer.Core.GameData;
using FfxRandomizer.Core.Patching;
using FfxRandomizer.Core.Randomization;
using FfxRandomizer.Core.Randomization.Features;
using FfxRandomizer.Core.Storage;
using FfxRandomizer.Core.Vbf;

namespace FfxRandomizer.Tests;

/// <summary>Items & Shops features against the installed game's original data (read-only). Pass trivially without the game.</summary>
public class ItemTests
{
    private static readonly Lazy<VbfArchive?> Original = new(() =>
        GameInstall.FindSteamInstall() is { } game ? new PatchManager(game.DataArchivePath, AppPaths.Default).OpenOriginal() : null);

    private static readonly Lazy<IGameNames?> Names = new(() => Original.Value is { } o ? GameText.Load(o) : null);

    private static RandomizerSettings Settings(int variant)
    {
        var s = new RandomizerSettings();
        s.Toggles["sane"] = variant % 2 == 0;
        foreach (string id in new[] { "field", "shops", "custom" }) s.Toggles[id] = true;
        s.Toggles["keyitems"] = (variant & 2) != 0;
        s.Toggles["celestial"] = (variant & 4) != 0;
        s.Toggles["brotherhood"] = (variant & 8) != 0;
        s.Toggles["shopprices"] = (variant & 16) != 0;
        s.Toggles["spheres"] = (variant & 32) != 0;
        return s;
    }

    [Fact]
    public void Every_combination_of_options_passes_its_checks_over_many_seeds()
    {
        if (Original.Value is not { } original) return;

        for (int variant = 0; variant < 64; variant++)
            for (int seed = 0; seed < 4; seed++)
            {
                var result = RandomizerEngine.Run(original, Settings(variant), new Seed($"items-{variant}-{seed}"), Names.Value);
                Assert.True(result.Problems.Count == 0, $"variant {variant} seed {seed}: {string.Join("; ", result.Problems.Take(5))}");
            }
    }

    [Fact]
    public void Everything_together_passes_its_checks()
    {
        if (Original.Value is not { } original) return;

        for (int i = 0; i < 40; i++)
        {
            var s = Settings(i % 64);
            foreach (string id in new[] { "tidus", "rewards", "drops", "steals", "bribes", "elements" }) s.Toggles[id] = true;
            var result = RandomizerEngine.Run(original, s, new Seed($"all-{i}"), Names.Value);
            Assert.True(result.Problems.Count == 0, $"seed all-{i}: {string.Join("; ", result.Problems.Take(5))}");
        }
    }

    [Fact]
    public void Baaj_temple_story_items_never_move()
    {
        if (Original.Value is not { } original) return;

        var before = new KernelTable(original.ReadFile(KernelFiles.Treasure));
        Assert.Equal(new Treasure(Treasure.KindKeyItem, 1, 0xA000), Treasure.Read(before.Record(2)) with { Quantity = 1 });
        Assert.Equal(0xA001, Treasure.Read(before.Record(3)).Item);

        for (int i = 0; i < 20; i++)
        {
            var result = RandomizerEngine.Run(original, Settings(2 | 4 | 8), new Seed($"baaj-{i}"));
            var after = new KernelTable(result.Files[KernelFiles.Treasure]);
            Assert.Equal(Treasure.Read(before.Record(2)), Treasure.Read(after.Record(2)));
            Assert.Equal(Treasure.Read(before.Record(3)), Treasure.Read(after.Record(3)));
        }
    }

    [Fact]
    public void Treasure_gear_is_described_like_FFXDataParser_does()
    {
        if (Original.Value is not { } original) return;

        // FFXDataParser: treasure 19 is "buki_get #6 { Kimahri, Armor {...} }"; treasure 4 is "buki_get #2 { Yuna, Weapon }".
        var context = new RandomizerContext(original, new RandomizerSettings(), new Seed("x"));
        var gear = new KernelTable(original.ReadFile(KernelFiles.Treasure));
        Assert.Equal("""{"gear":6,"for":"Kimahri","type":"Armor"}""",
            RandomizerContext.ToNode(TreasureFeature.Describe(context, Treasure.Read(gear.Record(19))))!.ToJsonString());
        Assert.Equal("""{"gear":2,"for":"Yuna","type":"Weapon"}""",
            RandomizerContext.ToNode(TreasureFeature.Describe(context, Treasure.Read(gear.Record(4))))!.ToJsonString());
    }

    [Fact]
    public void Spoiler_lists_treasure_shops_prices_and_customizations_by_name()
    {
        if (Original.Value is not { } original) return;

        var result = RandomizerEngine.Run(original, Settings(16 | 32), new Seed("names"), Names.Value);
        var treasure = (JsonArray)result.Spoiler[TreasureFeature.Section]!;
        Assert.True(treasure.Count > 300);
        Assert.Contains(treasure, t => t!["was"]!.ToJsonString() == """{"item":"Potion","id":8192,"quantity":2}""" && (int)t["index"]! == 1);

        var shops = (JsonArray)result.Spoiler[ItemShopsFeature.ShopSection]!;
        Assert.All(shops, s => Assert.Equal("Power Sphere", (string?)s!["items"]![0]));
        Assert.Equal("Potion", (string?)result.Spoiler[ItemShopsFeature.PriceSection]![0]!["item"]);
        Assert.NotNull(result.Spoiler[CustomizationFeature.Section]![0]!["ability"]);
    }

    [Fact]
    public void Item_features_are_independent_of_enemy_features_for_the_same_seed()
    {
        if (Original.Value is not { } original) return;

        var itemsOnly = RandomizerEngine.Run(original, Settings(0), new Seed("mix"));
        var s = Settings(0);
        s.Toggles["rewards"] = s.Toggles["drops"] = s.Toggles["elements"] = true;
        var withEnemies = RandomizerEngine.Run(original, s, new Seed("mix"));
        foreach (string file in new[] { KernelFiles.Treasure, KernelFiles.ItemShops, KernelFiles.Customizations })
            Assert.Equal(itemsOnly.Files[file], withEnemies.Files[file]);
    }
}
