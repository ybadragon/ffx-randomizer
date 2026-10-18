using System.Buffers.Binary;
using System.Text.Json.Nodes;
using FfxRandomizer.Core.Game;
using FfxRandomizer.Core.GameData;
using FfxRandomizer.Core.Patching;
using FfxRandomizer.Core.Randomization;
using FfxRandomizer.Core.Randomization.Features;
using FfxRandomizer.Core.Storage;
using FfxRandomizer.Core.Vbf;

namespace FfxRandomizer.Tests;

/// <summary>Party and Aeon features against the installed game's original data (read-only). Pass trivially without the game.</summary>
public class PartyTests
{
    private static readonly Lazy<VbfArchive?> Original = new(() =>
        GameInstall.FindSteamInstall() is { } game ? new PatchManager(game.DataArchivePath, AppPaths.Default).OpenOriginal() : null);

    private static readonly Lazy<IGameNames?> Names = new(() => Original.Value is { } o ? GameText.Load(o) : null);

    private static readonly string[] Modes = ["orig", "shuffle", "random"];

    private static RandomizerSettings Settings(int variant)
    {
        var s = new RandomizerSettings();
        s.Choices["pstats"] = Modes[variant % 3];
        s.Choices["abase"] = Modes[variant / 3 % 3];
        s.Choices["ascale"] = Modes[variant / 9 % 3];
        s.Toggles["od"] = (variant & 1) != 0;
        s.Toggles["poison"] = (variant & 2) != 0;
        s.Toggles["aitems"] = (variant & 4) != 0;
        s.Toggles["sane"] = (variant & 8) == 0;
        s.Toggles["tidus"] = (variant & 16) != 0;
        return s;
    }

    private static uint U32(byte[] file, int record, int offset) => new KernelTable(file).ReadUInt32(record, offset);

    [Fact]
    public void Every_mix_of_options_passes_its_checks_over_many_seeds()
    {
        if (Original.Value is not { } original) return;

        for (int variant = 0; variant < 54; variant++)
            for (int seed = 0; seed < 4; seed++)
            {
                var result = RandomizerEngine.Run(original, Settings(variant), new Seed($"party-{variant}-{seed}"), Names.Value);
                Assert.True(result.Problems.Count == 0, $"variant {variant} seed {seed}: {string.Join("; ", result.Problems.Take(5))}");
            }
    }

    [Theory]
    [InlineData("random")]
    [InlineData("shuffle")]
    public void Tidus_test_mod_still_wins_over_party_stats(string mode)
    {
        if (Original.Value is not { } original) return;

        var s = new RandomizerSettings();
        s.Toggles["tidus"] = true;
        s.Choices["pstats"] = mode;
        for (int i = 0; i < 10; i++)
        {
            var result = RandomizerEngine.Run(original, s, new Seed($"tidus-{mode}-{i}"));
            Assert.Empty(result.Problems);
            foreach (string language in GamePaths.LanguageFolders)
            {
                byte[] file = result.Files[GamePaths.LanguageKernel(language, PlySave.FileName)];
                foreach (int offset in new[] { PlySave.BaseHp, PlySave.CurrentHp, PlySave.MaxHp })
                    Assert.Equal(TidusHpFeature.Hp, U32(file, PlySave.Tidus, offset));
            }
            var tidus = result.Spoiler[PartyStatsFeature.Section]!.AsArray().Single(e => (string?)e!["character"] == "Tidus")!;
            Assert.Equal(9999, (int)tidus["hp"]!["to"]!);
            Assert.Equal(9999, (int)tidus["stats"]!["hp"]!["to"]!);
        }
    }

    [Fact]
    public void Every_language_keeps_its_own_names_and_gets_the_same_numbers()
    {
        if (Original.Value is not { } original) return;

        var s = new RandomizerSettings();
        s.Choices["pstats"] = s.Choices["ascale"] = "random";
        s.Toggles["od"] = s.Toggles["poison"] = s.Toggles["tidus"] = true;
        var result = RandomizerEngine.Run(original, s, new Seed("languages"));
        foreach (string file in new[] { PlySave.FileName, PlyRom.FileName })
        {
            int numbersFrom = file == PlySave.FileName ? 0x02 : 0x10;
            var us = new KernelTable(result.Files[GamePaths.LanguageKernel("new_uspc", file)]);
            foreach (string language in GamePaths.LanguageFolders)
            {
                var before = new KernelTable(original.ReadFile(GamePaths.LanguageKernel(language, file)));
                var after = new KernelTable(result.Files[GamePaths.LanguageKernel(language, file)]);
                for (int r = 0; r < after.RecordCount; r++)
                {
                    Assert.True(after.Record(r)[..numbersFrom].SequenceEqual(before.Record(r)[..numbersFrom]), $"{language} {file} {r} text");
                    Assert.True(after.Record(r)[numbersFrom..].SequenceEqual(us.Record(r)[numbersFrom..]), $"{language} {file} {r} numbers");
                }
            }
        }
    }

    [Fact]
    public void A_language_left_behind_is_caught()
    {
        if (Original.Value is not { } original) return;

        var s = new RandomizerSettings();
        s.Choices["pstats"] = "random";
        var context = new RandomizerContext(original, s, new Seed("behind"));
        var feature = new PartyStatsFeature();
        feature.Apply(context);
        Assert.Empty(feature.Validate(context));

        context.Edit(GamePaths.LanguageKernel("new_depc", PlySave.FileName))[20 + PlySave.BaseHp] ^= 1;
        Assert.Contains(feature.Validate(context), p => p.Contains("new_depc"));
    }

    [Fact]
    public void Shuffled_party_stats_come_from_other_party_members()
    {
        if (Original.Value is not { } original) return;

        var s = new RandomizerSettings();
        s.Choices["pstats"] = "shuffle";
        var file = original.ReadFile(GamePaths.LanguageKernel("new_uspc", PlySave.FileName));
        var hpBefore = Enumerable.Range(0, PlySave.PartySize).Select(i => U32(file, i, PlySave.BaseHp)).Order().ToList();
        for (int i = 0; i < 10; i++)
        {
            var result = RandomizerEngine.Run(original, s, new Seed($"shuffle-{i}"));
            var after = result.Files[GamePaths.LanguageKernel("new_uspc", PlySave.FileName)];
            Assert.Equal(hpBefore, Enumerable.Range(0, PlySave.PartySize).Select(c => U32(after, c, PlySave.BaseHp)).Order().ToList());
        }
    }

    [Fact]
    public void Overdrive_modes_are_learned_and_Auron_stays_Stoic()
    {
        if (Original.Value is not { } original) return;

        var s = new RandomizerSettings();
        s.Toggles["od"] = true;
        var seen = new HashSet<int>();
        for (int i = 0; i < 30; i++)
        {
            var result = RandomizerEngine.Run(original, s, new Seed($"od-{i}"));
            Assert.Empty(result.Problems);
            var table = new KernelTable(result.Files[GamePaths.LanguageKernel("new_uspc", PlySave.FileName)]);
            Assert.Equal(2, table.Record(PlySave.Auron)[PlySave.OverdriveMode]);
            for (int c = 0; c < PlySave.PartySize; c++)
            {
                int mode = table.Record(c)[PlySave.OverdriveMode];
                seen.Add(mode);
                Assert.NotEqual(0u, BinaryPrimitives.ReadUInt32LittleEndian(table.Record(c)[PlySave.OverdriveLearned..]) & 1u << mode);
            }
        }
        Assert.True(seen.Count > 10, $"only saw modes {string.Join(",", seen)}");
    }

    [Fact]
    public void Aeon_growth_changes_the_aeon_records_and_nothing_else()
    {
        if (Original.Value is not { } original) return;

        var s = new RandomizerSettings();
        s.Choices["ascale"] = "random";
        var result = RandomizerEngine.Run(original, s, new Seed("growth"), Names.Value);
        Assert.Empty(result.Problems);
        var before = new KernelTable(original.ReadFile(GamePaths.LanguageKernel("new_uspc", PlyRom.FileName)));
        var after = new KernelTable(result.Files[GamePaths.LanguageKernel("new_uspc", PlyRom.FileName)]);
        int changed = Enumerable.Range(0, after.RecordCount).Count(r => !after.Record(r).SequenceEqual(before.Record(r)));
        Assert.InRange(changed, 8, 10);
        Assert.All(Enumerable.Range(0, PlyRom.FirstAeon), r => Assert.True(after.Record(r).SequenceEqual(before.Record(r))));

        var aeons = (JsonArray)result.Spoiler[AeonStatsFeature.Section]!;
        Assert.Equal(AeonFiles.Names, aeons.Select(a => (string?)a!["aeon"]));
        Assert.Equal("[6,20]", aeons[0]!["growth"]!["hp"]!["from"]!.ToJsonString());
    }

    [Fact]
    public void Aeon_base_stats_still_rise_through_the_story()
    {
        if (Original.Value is not { } original) return;

        foreach (string mode in new[] { "random", "shuffle" })
            for (int i = 0; i < 20; i++)
            {
                var s = new RandomizerSettings();
                s.Choices["abase"] = mode;
                var result = RandomizerEngine.Run(original, s, new Seed($"abase-{mode}-{i}"));
                Assert.Empty(result.Problems);
                Assert.NotEqual(original.ReadFile(AeonFiles.SumAssure), result.Files[AeonFiles.SumAssure]);
            }
    }

    [Fact]
    public void Aeon_stat_boosts_keep_their_amount_and_name_their_new_item()
    {
        if (Original.Value is not { } original) return;

        var s = new RandomizerSettings();
        s.Toggles["aitems"] = true;
        var result = RandomizerEngine.Run(original, s, new Seed("aitems"), Names.Value);
        Assert.Empty(result.Problems);
        var spoiler = (JsonArray)result.Spoiler[AeonItemsFeature.Section]!;
        Assert.Equal(77, spoiler.Count);
        Assert.Contains(spoiler, e => (string?)e!["teaches"] == "HP +100" && (int)e["now"]!["quantity"]! == 1);
        Assert.Contains(spoiler, e => (string?)e!["teaches"] == "Fire" && (string?)e["was"]!["item"] == "Bomb Fragment");
    }

    [Fact]
    public void Party_features_leave_other_features_alone_for_the_same_seed()
    {
        if (Original.Value is not { } original) return;

        var a = new RandomizerSettings();
        a.Toggles["custom"] = a.Toggles["rewards"] = a.Toggles["drops"] = true;
        var b = a.Clone();
        b.Choices["pstats"] = "random";
        b.Toggles["aitems"] = true;
        var without = RandomizerEngine.Run(original, a, new Seed("mix"));
        var with = RandomizerEngine.Run(original, b, new Seed("mix"));
        foreach (var (file, bytes) in without.Files) Assert.Equal(bytes, with.Files[file]);
    }
}
