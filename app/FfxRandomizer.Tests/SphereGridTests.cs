using FfxRandomizer.Core.Game;
using FfxRandomizer.Core.GameData;
using FfxRandomizer.Core.Patching;
using FfxRandomizer.Core.Randomization;
using FfxRandomizer.Core.Randomization.Features;
using FfxRandomizer.Core.Storage;
using FfxRandomizer.Core.Vbf;

namespace FfxRandomizer.Tests;

/// <summary>Sphere Grid options against the installed game's original data (read-only). Pass trivially without the game.</summary>
public class SphereGridTests
{
    private static readonly Lazy<VbfArchive?> Original = new(() =>
        GameInstall.FindSteamInstall() is { } game ? new PatchManager(game.DataArchivePath, AppPaths.Default).OpenOriginal() : null);

    private static readonly string[] Modes = ["orig", "shuffle", "controlled", "random"];
    private static readonly string[] Fills = ["none", "empty", "full"];
    private static readonly string[] Strengths = ["none", "up", "down"];

    private static RandomizerSettings Settings(string mode, string fill = "none", string strength = "none", bool locks = false, bool sane = true)
    {
        var s = new RandomizerSettings();
        s.Toggles["sane"] = sane;
        s.Toggles["locks"] = locks;
        s.Choices["gridmode"] = mode;
        s.Choices["gridfill"] = fill;
        s.Choices["nodes"] = strength;
        return s;
    }

    private static byte[] Nodes(byte[] contents) => contents.AsSpan(SphereGridFile.ContentsHeader).ToArray();

    [Fact]
    public void Node_names_cover_every_panel_row()
    {
        Assert.Equal(SphereNodes.LastAbility + 1, SphereNodes.Names.Count);
        Assert.Equal("Steal", SphereNodes.Name(SphereNodes.Steal));
        Assert.Equal("Use", SphereNodes.Name(SphereNodes.Use));
        Assert.Equal("Quick Pockets", SphereNodes.Name(SphereNodes.LastAbility));
    }

    [Theory]
    [InlineData(0x02, 0x05, 0x02)] // Strength +1
    [InlineData(0x04, 0x05, 0x02)] // Strength +3
    [InlineData(0x21, 0x21, 0x1E)] // Accuracy +4
    [InlineData(0x22, 0x23, 0x22)] // HP +200
    [InlineData(0x25, 0x24, 0x26)] // MP +20
    [InlineData(0x24, 0x24, 0x26)] // MP +40: FFXRando's downgrade turned MP +20 and +10 into +40 instead
    [InlineData(0x01, 0x01, 0x01)]
    [InlineData(0x3A, 0x3A, 0x3A)]
    public void Node_strength_moves_within_the_same_stat(byte node, byte up, byte down)
    {
        Assert.Equal(up, SphereNodes.Upgrade(node));
        Assert.Equal(down, SphereNodes.Downgrade(node));
    }

    [Fact]
    public void Every_combination_of_options_passes_its_checks()
    {
        if (Original.Value is not { } original) return;

        int run = 0;
        foreach (string mode in Modes)
            foreach (string fill in Fills)
                foreach (string strength in Strengths)
                    foreach (bool locks in new[] { false, true })
                        foreach (bool sane in new[] { false, true })
                        {
                            var settings = Settings(mode, fill, strength, locks, sane);
                            if (!new SphereGridFeature().IsEnabled(settings)) continue;
                            var result = RandomizerEngine.Run(original, settings, new Seed($"grid-{run++}"));
                            Assert.True(result.Problems.Count == 0,
                                $"{mode}/{fill}/{strength}/locks={locks}/sane={sane}: {string.Join("; ", result.Problems.Take(5))}");
                        }
    }

    [Fact]
    public void Each_mode_passes_over_many_seeds()
    {
        if (Original.Value is not { } original) return;

        foreach (string mode in Modes.Skip(1))
            for (int seed = 0; seed < 25; seed++)
            {
                var result = RandomizerEngine.Run(original, Settings(mode, sane: seed % 2 == 0), new Seed($"{mode}-{seed}"));
                Assert.True(result.Problems.Count == 0, $"{mode} seed {seed}: {string.Join("; ", result.Problems.Take(5))}");
            }
    }

    [Fact]
    public void Controlled_random_places_every_ability_once_on_every_grid()
    {
        if (Original.Value is not { } original) return;

        var result = RandomizerEngine.Run(original, Settings("controlled", sane: false), new Seed("controlled"));
        foreach (var grid in SphereGridFile.All)
        {
            var nodes = Nodes(result.Files[grid.ContentsPath]);
            for (int ability = SphereNodes.FirstAbility; ability <= SphereNodes.LastAbility; ability++)
                Assert.Equal(1, nodes.Count(n => n == ability));
        }
    }

    [Fact]
    public void Shuffle_keeps_the_same_nodes_and_sane_keeps_locks_in_place()
    {
        if (Original.Value is not { } original) return;

        var result = RandomizerEngine.Run(original, Settings("shuffle"), new Seed("shuffle"));
        foreach (var grid in SphereGridFile.All)
        {
            var before = Nodes(original.ReadFile(grid.ContentsPath));
            var after = Nodes(result.Files[grid.ContentsPath]);
            Assert.Equal(before.Order(), after.Order());
            Assert.NotEqual(before, after);
            for (int i = 0; i < before.Length; i++)
            {
                Assert.Equal(SphereNodes.IsLock(before[i]), SphereNodes.IsLock(after[i]));
                if (!SphereNodes.IsRegular(before[i])) Assert.Equal(before[i], after[i]);
            }
        }
    }

    [Fact]
    public void Changed_grids_give_the_tutorial_abilities_in_every_language()
    {
        if (Original.Value is not { } original) return;

        var result = RandomizerEngine.Run(original, Settings("random", sane: false), new Seed("abilities"));
        foreach (string language in GamePaths.LanguageFolders)
        {
            string path = GamePaths.LanguageKernel(language, PlySave.FileName);
            var before = new KernelTable(original.ReadFile(path));
            var after = new KernelTable(result.Files[path]);
            foreach (var (character, _, commands) in SphereGridFeature.RequiredAbilities)
                foreach (int command in commands) Assert.True(SphereNodes.Knows(after.Record(character), command));
            // Only the learned-command bits may change, and only by being set.
            for (int r = 0; r < before.RecordCount; r++)
            {
                var b = before.Record(r).ToArray();
                var a = after.Record(r).ToArray();
                for (int k = 0; k < b.Length; k++)
                {
                    bool inCommands = k is >= SphereNodes.LearnedCommands and < SphereNodes.LearnedCommands + 12;
                    if (!inCommands) Assert.Equal(b[k], a[k]);
                    else Assert.Equal(b[k], (byte)(a[k] & b[k]));
                }
            }
        }
    }

    [Fact]
    public void Lock_removal_alone_leaves_the_party_alone()
    {
        if (Original.Value is not { } original) return;

        var result = RandomizerEngine.Run(original, Settings("orig", locks: true), new Seed("locks"));
        Assert.DoesNotContain(result.Files.Keys, k => k.EndsWith(PlySave.FileName, StringComparison.Ordinal));
        Assert.Equal(SphereGridFile.All.Count, result.Files.Count);
        foreach (var grid in SphereGridFile.All)
            Assert.DoesNotContain(Nodes(result.Files[grid.ContentsPath]), SphereNodes.IsLock);
    }

    [Fact]
    public void Same_seed_gives_the_same_grid()
    {
        if (Original.Value is not { } original) return;

        var a = RandomizerEngine.Run(original, Settings("controlled", "full", "up"), new Seed("same"));
        var b = RandomizerEngine.Run(original, Settings("controlled", "full", "up"), new Seed("same"));
        foreach (var grid in SphereGridFile.All) Assert.Equal(a.Files[grid.ContentsPath], b.Files[grid.ContentsPath]);
    }

    [Fact]
    public void Checks_catch_a_broken_grid()
    {
        if (Original.Value is not { } original) return;

        var settings = Settings("controlled");
        var context = new RandomizerContext(original, settings, new Seed("broken"));
        var feature = new SphereGridFeature();
        feature.Apply(context);
        Assert.Empty(feature.Validate(context));

        var expert = context.Edit(SphereGridFile.All[2].ContentsPath);
        int steal = Array.IndexOf(expert, SphereNodes.Steal, SphereGridFile.ContentsHeader);
        expert[steal] = SphereNodes.Use;
        Assert.Contains(feature.Validate(context), p => p.Contains("Steal appears 0 times"));
    }
}
