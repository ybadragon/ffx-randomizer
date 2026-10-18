using FfxRandomizer.Core.GameData;
using FfxRandomizer.Core.Randomization;
using FfxRandomizer.Core.Storage;
using FfxRandomizer.Core.Vbf;

namespace FfxRandomizer.Tests;

public class RandomizationTests
{
    [Fact]
    public void Seeds_and_random_streams_never_change_between_versions()
    {
        // Pinned values: if these change, every saved seed in players' History stops replaying.
        Assert.Equal(0x6648CBB988B40125UL, new Seed("RACE-2026").Value);
        var rng = Rng.For(new Seed("863228749"), "party");
        Assert.Equal([0xB8711F0A32B93510UL, 0xBAD5025D2D2AF9C0UL, 0xAC253FA697E8FDF0UL],
            new[] { rng.NextUInt64(), rng.NextUInt64(), rng.NextUInt64() });
    }

    [Fact]
    public void Generator_matches_the_xoshiro256starstar_reference()
    {
        var rng = new Rng(1, 2, 3, 4);
        Assert.Equal([11520UL, 0UL, 1509978240UL, 1215971899390074240UL],
            new[] { rng.NextUInt64(), rng.NextUInt64(), rng.NextUInt64(), rng.NextUInt64() });
    }

    [Fact]
    public void Sections_get_independent_streams()
    {
        var seed = new Seed("12345");
        Assert.NotEqual(Rng.For(seed, "party").NextUInt64(), Rng.For(seed, "enemies").NextUInt64());
        Assert.Equal(Rng.For(seed, "party").NextUInt64(), Rng.For(seed, "party").NextUInt64());
        Assert.Equal(new Seed(" 12345 ").Value, seed.Value);
    }

    [Fact]
    public void Next_stays_in_range()
    {
        var rng = new Rng(42);
        for (int i = 0; i < 10_000; i++) Assert.InRange(rng.Next(-3, 7), -3, 7);
    }

    [Fact]
    public void Tidus_mod_sets_hp_in_every_language()
    {
        using var dir = new TempDir();
        string path = dir.File("FFX_Data.vbf");
        TestArchive.Write(path, GamePaths.LanguageFolders
            .Select(l => (GamePaths.LanguageKernel(l, PlySave.FileName), TestArchive.PlySave(520)))
            .ToList());
        var settings = RandomizerSettings.Defaults();
        settings.Toggles["tidus"] = true;

        var result = RandomizerEngine.Run(VbfArchive.Open(path), settings, new Seed("1"));

        Assert.Equal(8, result.Files.Count);
        foreach (var bytes in result.Files.Values)
        {
            var table = new KernelTable(bytes);
            Assert.Equal(9999u, table.ReadUInt32(PlySave.Tidus, PlySave.BaseHp));
            Assert.Equal(9999u, table.ReadUInt32(PlySave.Tidus, PlySave.CurrentHp));
            Assert.Equal(9999u, table.ReadUInt32(PlySave.Tidus, PlySave.MaxHp));
            Assert.Equal(0u, table.ReadUInt32(1, PlySave.MaxHp));
        }
        Assert.Equal("""[{"character":"Tidus","hp":{"from":520,"to":9999}}]""", result.Spoiler["party"]!.ToJsonString());
    }

    [Fact]
    public void Spoiler_file_is_json_with_run_details_and_sections()
    {
        using var dir = new TempDir();
        var settings = RandomizerSettings.Defaults();
        settings.Toggles["tidus"] = true;
        var spoiler = new System.Text.Json.Nodes.JsonObject { ["party"] = new System.Text.Json.Nodes.JsonArray(1, 2) };
        var run = new RunResult(new Seed("RACE-2026"), settings, new Dictionary<string, byte[]>(), spoiler);
        var entry = new HistoryEntry { Seed = "RACE-2026", PresetName = "Race rules" };

        string path = SpoilerLog.Write(new AppPaths(dir.Path), run, entry);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;

        Assert.EndsWith(".json", path);
        Assert.Equal("ffx-randomizer-spoiler", (string?)json["format"]);
        Assert.Equal("RACE-2026", (string?)json["seed"]);
        Assert.Equal("Race rules", (string?)json["preset"]);
        Assert.Equal("[\"sane\",\"spoiler\",\"tidus\"]", json["settings"]!["toggles"]!.ToJsonString());
        Assert.Equal("[1,2]", json["party"]!.ToJsonString());
    }

    [Fact]
    public void Settings_compare_by_effect_not_by_stored_keys()
    {
        var a = RandomizerSettings.Defaults();
        var b = RandomizerSettings.Defaults();
        b.Toggles["od"] = false;
        b.Choices["pstats"] = "orig";
        Assert.True(a.SameAs(b));

        b.Toggles["tidus"] = true;
        Assert.False(a.SameAs(b));
        Assert.Equal(3, b.ActiveCount);
    }

    [Fact]
    public void Spoiler_summary_lists_active_boxes_with_their_choices()
    {
        var settings = new RandomizerSettings();
        settings.Toggles["rewards"] = true;
        settings.Toggles["drops"] = true;
        settings.Toggles["steals"] = true;
        settings.Choices["gridmode"] = "shuffle";

        Assert.Equal(["Enemy rewards: Item drops, Steals", "Grid contents: Shuffle nodes"], SpoilerLog.Describe(settings));
    }
}
