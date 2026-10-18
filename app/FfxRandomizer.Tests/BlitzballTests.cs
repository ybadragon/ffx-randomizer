using System.Text.Json.Nodes;
using FfxRandomizer.Core.Game;
using FfxRandomizer.Core.GameData;
using FfxRandomizer.Core.Patching;
using FfxRandomizer.Core.Randomization;
using FfxRandomizer.Core.Randomization.Features;
using FfxRandomizer.Core.Storage;
using FfxRandomizer.Core.Vbf;

namespace FfxRandomizer.Tests;

/// <summary>Blitzball player stats. Curve maths runs everywhere; the rest reads the installed game (read-only) and passes trivially without it.</summary>
public class BlitzballTests
{
    private static readonly Lazy<VbfArchive?> Original = new(() =>
        GameInstall.FindSteamInstall() is { } game ? new PatchManager(game.DataArchivePath, AppPaths.Default).OpenOriginal() : null);

    private static RandomizerSettings Settings(string mode, bool sane = true, bool stars = false)
    {
        var s = new RandomizerSettings();
        s.Toggles["sane"] = sane;
        s.Choices["blitz"] = mode;
        s.Toggles["blitzstars"] = stars;
        return s;
    }

    [Theory]
    [InlineData(70f, 30f, 0.711f, 3f, 99, 70 + 30 * 99 + 0.711 * 99 * 99)]
    [InlineData(5f, 0.8f, 0.005f, 2f, 50, 5 + 0.8 * 50 - 0.005 * 50 * 50)]
    [InlineData(4f, 0.0055f, 2f, 1f, 10, 4 + 0.0055 * 100)]
    [InlineData(60f, 0.08f, 0f, 0f, 10, 60 + 0.8)]
    [InlineData(9f, 9f, 9f, -1f, 10, 1)]
    public void Growth_curves_compute_like_the_parser_shows_them(float a, float b, float c, float kind, int level, double expected) =>
        Assert.Equal(expected, new BlitzGrowth(a, b, c, kind).ValueAt(level), 3);

    [Fact]
    public void Scaling_keeps_the_exponent_of_power_curves()
    {
        var power = new BlitzGrowth(4, 0.0055f, 2, 1).Scaled(2, 3);
        Assert.Equal(new BlitzGrowth(8, 0.0165f, 2, 1), power);
        var quadratic = new BlitzGrowth(10, 1, -0.004f, 3).Scaled(1, 2);
        Assert.Equal(new BlitzGrowth(10, 2, -0.008f, 3), quadratic);
    }

    [Fact]
    public void Reads_the_original_tables_and_names()
    {
        if (Original.Value is not { } original) return;
        var stats = BlitzballStats.Read(original);
        var text = GameText.Load(original);

        Assert.Equal("Tidus", text.BlitzballPlayerName(0));
        Assert.Equal("Wakka", text.BlitzballPlayerName(1));
        Assert.Equal("Datto", text.BlitzballPlayerName(2));
        Assert.Equal("Miyu", text.BlitzballPlayerName(59));
        // Tidus: HP 70 + 30·Lv + 0.711·Lv², Shot 9 + 0.88·Lv − 0.0018·Lv².
        Assert.Equal(new BlitzGrowth(70, 30, 0.711f, 3), stats[0, BlitzStat.HP]);
        Assert.Equal(new BlitzGrowth(9, 0.88f, -0.0018f, 3), stats[0, BlitzStat.SH]);
    }

    [Fact]
    public void Every_copy_of_the_tables_is_found()
    {
        if (Original.Value is not { } original) return;
        var copies = BlitzballStats.FindCopies(original).Select(c => Path.GetFileNameWithoutExtension(c.Path)).ToList();

        // The 39 events FFXDataParser lists, plus Vita (psv_), Chinese (cn_) and debug variants and hiku2100.
        Assert.Equal(52, copies.Count);
        foreach (string e in new[] { "bltz0002", "bltz0000", "bltz0200", "bltz0201", "luca0400", "kami0100", "cn_bltz0201", "psv_hiku2100" })
            Assert.Contains(e, copies);
    }

    [Theory]
    [InlineData("shuffle")]
    [InlineData("random")]
    public void Hundreds_of_seeds_pass_every_check(string mode)
    {
        if (Original.Value is not { } original) return;
        for (int i = 0; i < 150; i++)
        {
            var settings = Settings(mode, sane: i % 3 != 0, stars: i % 2 == 0);
            var result = RandomizerEngine.Run(original, settings, new Seed($"blitz-{mode}-{i}"));
            Assert.True(result.Problems.Count == 0, $"seed blitz-{mode}-{i}: {string.Join("; ", result.Problems.Take(5))}");
            Assert.Equal(52, result.Files.Count);
        }
    }

    [Fact]
    public void Shuffle_moves_whole_players_and_leaves_the_stars_alone()
    {
        if (Original.Value is not { } original) return;
        var before = BlitzballStats.Read(original);
        var result = RandomizerEngine.Run(original, Settings("shuffle"), new Seed("blitz-shuffle"));
        var after = BlitzballStats.FromEvent(result.Files[BlitzballStats.EventPath(BlitzballStats.ReferenceEvent)]);

        foreach (int star in BlitzballFeatureStars) Assert.Equal(Profile(before, star), Profile(after, star));
        var originals = Enumerable.Range(2, 58).Select(p => Profile(before, p)).ToList();
        var shuffled = Enumerable.Range(2, 58).Select(p => Profile(after, p)).ToList();
        Assert.Equal(originals.Order(), shuffled.Order());
        Assert.True(Enumerable.Range(0, 58).Count(i => originals[i] != shuffled[i]) > 40);
    }

    [Fact]
    public void Randomize_with_sane_on_stays_in_range_and_never_falls()
    {
        if (Original.Value is not { } original) return;
        var before = BlitzballStats.Read(original);
        var result = RandomizerEngine.Run(original, Settings("random", stars: true), new Seed("blitz-random"));
        var after = BlitzballStats.FromEvent(result.Files[BlitzballStats.EventPath(BlitzballStats.ReferenceEvent)]);

        foreach (var stat in Enum.GetValues<BlitzStat>())
        {
            var limits = BlitzballStatsFeature.Limits.For(before, stat, sane: true);
            for (int p = 0; p < BlitzballStats.PlayerCount; p++)
            {
                Assert.True(limits.Allows(after[p, stat]), $"player {p} {stat}: {after[p, stat].Formula()}");
            }
        }
        Assert.NotEqual(Profile(before, 0), Profile(after, 0));
    }

    [Fact]
    public void Spoiler_lists_each_changed_player_with_names()
    {
        if (Original.Value is not { } original) return;
        var result = RandomizerEngine.Run(original, Settings("shuffle"), new Seed("blitz-spoiler"), GameText.Load(original));
        var entries = (JsonArray)result.Spoiler[BlitzballStatsFeature.Section]!;

        Assert.Equal(58, entries.Count);
        var datto = entries.First(e => (int)e!["player"]! == 2)!;
        Assert.Equal("Datto", (string?)datto["name"]);
        Assert.NotNull((string?)datto["statsOf"]);
        Assert.NotNull((string?)datto["stats"]!["HP"]!["formula"]);
    }

    [Fact]
    public void Same_seed_makes_the_same_tables()
    {
        if (Original.Value is not { } original) return;
        var a = RandomizerEngine.Run(original, Settings("random"), new Seed("blitz-same"));
        var b = RandomizerEngine.Run(original, Settings("random"), new Seed("blitz-same"));
        string path = BlitzballStats.EventPath(BlitzballStats.ReferenceEvent);
        Assert.Equal(a.Files[path], b.Files[path]);
    }

    [Fact]
    public void Original_choice_changes_nothing()
    {
        if (Original.Value is not { } original) return;
        var result = RandomizerEngine.Run(original, Settings("orig", stars: true), new Seed("blitz-orig"));
        Assert.Empty(result.Files);
    }

    private static readonly int[] BlitzballFeatureStars = [.. BlitzballStatsFeature.Stars];

    private static string Profile(BlitzballStats stats, int player) =>
        string.Join("|", Enum.GetValues<BlitzStat>().Select(s => stats[player, s].ToString()));
}
