using System.Text.Json.Nodes;
using FfxRandomizer.Core.Game;
using FfxRandomizer.Core.GameData;
using FfxRandomizer.Core.Patching;
using FfxRandomizer.Core.Randomization;
using FfxRandomizer.Core.Randomization.Features;
using FfxRandomizer.Core.Storage;
using FfxRandomizer.Core.Vbf;

namespace FfxRandomizer.Tests;

/// <summary>Enemy features against the installed game's original data (read-only). Pass trivially without the game.</summary>
public class EnemyTests
{
    private static readonly Lazy<VbfArchive?> Original = new(() =>
        GameInstall.FindSteamInstall() is { } game ? new PatchManager(game.DataArchivePath, AppPaths.Default).OpenOriginal() : null);

    private static RandomizerSettings Settings(bool sane = true, bool drops = true, bool steals = true, bool bribes = true, bool elements = true)
    {
        var s = new RandomizerSettings();
        s.Toggles["sane"] = sane;
        s.Toggles["rewards"] = true;
        s.Toggles["drops"] = drops;
        s.Toggles["steals"] = steals;
        s.Toggles["bribes"] = bribes;
        s.Toggles["elements"] = elements;
        return s;
    }

    [Fact]
    public void Reads_every_monster_and_builds_the_item_pool()
    {
        if (Original.Value is not { } original) return;

        var indices = MonsterFile.AllIndices(original);
        Assert.Equal(361, indices.Count);
        Assert.Equal(0, indices[0]);
        Assert.True(indices.Count(i => new MonsterFile(i, original.ReadFile(MonsterFile.PathFor(i))).HasLoot) > 300);

        var pool = ItemPool.Build(original);
        Assert.Contains((ushort)0x2000, pool.Items);
        Assert.InRange(pool.Items.Count, 60, 120);
        Assert.All(pool.Items, item => Assert.InRange(item, 0x2000, 0x20FF));
    }

    [Fact]
    public void Hundreds_of_seeds_pass_every_check()
    {
        if (Original.Value is not { } original) return;

        for (int i = 0; i < 300; i++)
        {
            var result = RandomizerEngine.Run(original, Settings(sane: i % 3 != 0), new Seed($"bulk-{i}"));
            Assert.True(result.Problems.Count == 0, $"seed bulk-{i}: {string.Join("; ", result.Problems.Take(5))}");
            Assert.InRange(result.Files.Count, 300, 361);
        }
    }

    [Fact]
    public void Same_seed_makes_the_same_game()
    {
        if (Original.Value is not { } original) return;

        var a = RandomizerEngine.Run(original, Settings(), new Seed("replay"));
        var b = RandomizerEngine.Run(original, Settings(), new Seed("replay"));
        Assert.Equal(a.Files.Keys.Order(), b.Files.Keys.Order());
        foreach (var (path, bytes) in a.Files) Assert.Equal(bytes, b.Files[path]);
        Assert.Equal(a.Spoiler.ToJsonString(), b.Spoiler.ToJsonString());
    }

    [Fact]
    public void Turning_one_option_off_leaves_the_others_unchanged()
    {
        if (Original.Value is not { } original) return;

        var all = RandomizerEngine.Run(original, Settings(), new Seed("independent"));
        var noBribes = RandomizerEngine.Run(original, Settings(bribes: false), new Seed("independent"));

        foreach (var key in new[] { "drops", "steal", "elements" })
            Assert.Equal(Section(all, key), Section(noBribes, key));
        Assert.NotEqual(Section(all, "bribe"), Section(noBribes, "bribe"));
    }

    [Fact]
    public void Spoiler_lists_each_enemy_once_with_its_new_rewards()
    {
        if (Original.Value is not { } original) return;

        var result = RandomizerEngine.Run(original, Settings(), new Seed("spoiler"), GameText.Load(original));
        var enemies = (JsonArray)result.Spoiler[EnemyRewardsFeature.Section]!;
        var ochu = enemies.Single(e => (int)e!["index"]! == 189)!;
        Assert.Equal("Ochu", (string?)ochu["monster"]);
        int dropId = (int)ochu["drops"]!["primary"]!["id"]!;
        Assert.Equal(GameText.Load(original).ItemName(dropId), (string?)ochu["drops"]!["primary"]!["item"]);
        Assert.DoesNotContain(enemies, e => ((string?)e!["drops"]?["primary"]?["item"])?.StartsWith("Item 0x") == true);
        Assert.Equal(enemies.Count, enemies.Select(e => (int)e!["index"]!).Distinct().Count());

        var first = enemies.First(e => e!["drops"] is not null)!;
        Assert.NotNull(first["monster"]);
        Assert.NotNull(first["drops"]!["primary"]!["item"]);
        Assert.NotNull(first["steal"]!["normal"]);
        Assert.NotNull(first["bribe"]);
    }

    [Fact]
    public void Checks_catch_a_corrupted_file()
    {
        if (Original.Value is not { } original) return;

        var context = new RandomizerContext(original, Settings(), new Seed("corrupt"));
        var feature = new EnemyRewardsFeature();
        feature.Apply(context);
        Assert.Empty(feature.Validate(context));

        var path = MonsterFile.PathFor(MonsterFile.AllIndices(original).First(i => context.ChangedFiles.ContainsKey(MonsterFile.PathFor(i))));
        context.ChangedFiles[path][0x10] ^= 0xFF;
        Assert.Contains(feature.Validate(context), p => p.Contains("outside the loot"));

        context.ChangedFiles[path][0x10] ^= 0xFF;
        var monster = new MonsterFile(0, context.ChangedFiles[path]);
        var loot = monster.Loot;
        loot.Bribe = new ItemStack(0xA001, 1);
        Assert.Contains(feature.Validate(context), p => p.Contains("bribe") && p.Contains("isn't a normal item"));
    }

    private static string Section(RunResult result, string key) =>
        string.Join("|", ((JsonArray)result.Spoiler[EnemyRewardsFeature.Section]!).Select(e => e![key]?.ToJsonString() ?? ""));
}
