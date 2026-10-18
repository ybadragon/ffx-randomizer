using System.Text.Json.Nodes;
using FfxRandomizer.Core.Game;
using FfxRandomizer.Core.GameData;
using FfxRandomizer.Core.Patching;
using FfxRandomizer.Core.Randomization;
using FfxRandomizer.Core.Randomization.Features;
using FfxRandomizer.Core.Storage;
using FfxRandomizer.Core.Vbf;

namespace FfxRandomizer.Tests;

/// <summary>Random encounters and enemy stats against the installed game's original data (read-only). Pass trivially without the game.</summary>
public class EncounterTests
{
    private static readonly Lazy<VbfArchive?> Original = new(() =>
        GameInstall.FindSteamInstall() is { } game ? new PatchManager(game.DataArchivePath, AppPaths.Default).OpenOriginal() : null);

    private static readonly string[] EncounterModes = ["random", "swap", "scale"];
    private static readonly string[] StatModes = ["shuffle", "shufflenorm", "random"];

    private static RandomizerSettings Settings(string encounters = "orig", string stats = "orig", bool sane = true, bool rewards = false)
    {
        var s = new RandomizerSettings();
        s.Toggles["sane"] = sane;
        s.Choices["encounters"] = encounters;
        s.Choices["estats"] = stats;
        if (rewards)
        {
            foreach (string id in new[] { "rewards", "drops", "steals", "bribes", "elements" }) s.Toggles[id] = true;
        }
        return s;
    }

    [Fact]
    public void Reads_the_encounter_tables_like_FFXRando()
    {
        if (Original.Value is not { } original) return;

        var battles = EncounterFiles.RandomEncounterBattles(original);
        // 783 battles from the tables FFXRando reads, plus 8 that exist under fields sharing another field's table.
        Assert.Equal(791, battles.Count);
        Assert.Contains("kami00_00", battles);
        Assert.Contains("mihn05_00", battles);
        Assert.All(battles, b => Assert.True(original.IndexOf(EncounterFiles.PathFor(b)) >= 0, b));
        Assert.Equal([209, 80, -1, -1, -1, -1, -1, -1], EncounterFiles.Monsters(original.ReadFile(EncounterFiles.PathFor("kami00_00"))));

        var context = new RandomizerContext(original, Settings(), new Seed("pool"));
        var pool = EnemyEncountersFeature.EligibleEnemies(context, battles);
        Assert.Equal(137, pool.Count);
        Assert.DoesNotContain(147, pool); // Sinscale: the Zanarkand opening stays as it is
        Assert.DoesNotContain(159, pool); // Sinspawn Ammes
        Assert.Contains(211, pool);       // the battle treasure chest, which FFXRando whitelists
    }

    [Fact]
    public void Every_mode_passes_every_check_across_many_seeds()
    {
        if (Original.Value is not { } original) return;

        int run = 0;
        foreach (string encounters in EncounterModes.Prepend("orig"))
        foreach (string stats in StatModes.Prepend("orig"))
        {
            if (encounters == "orig" && stats == "orig") continue;
            for (int i = 0; i < 8; i++, run++)
            {
                var settings = Settings(encounters, stats, sane: i % 3 != 0, rewards: i % 2 == 0);
                var result = RandomizerEngine.Run(original, settings, new Seed($"enc-{run}"));
                Assert.True(result.Problems.Count == 0, $"{encounters}/{stats} seed enc-{run}: {string.Join("; ", result.Problems.Take(5))}");
            }
        }
    }

    [Fact]
    public void Encounters_are_a_one_to_one_shuffle_and_story_battles_are_untouched()
    {
        if (Original.Value is not { } original) return;

        var result = RandomizerEngine.Run(original, Settings("random"), new Seed("one-to-one"));
        var pairs = ((JsonArray)result.Spoiler[EnemyEncountersFeature.Section]!).Select(p => ((int)p!["replacedIndex"]!, (int)p["byIndex"]!)).ToList();
        Assert.True(pairs.Count > 100);
        Assert.Equal(pairs.Count, pairs.Select(p => p.Item2).Distinct().Count());
        Assert.All(pairs, p => Assert.NotEqual(p.Item1, p.Item2));

        // Only battle files and nothing else (no monster files: "own stats" leaves stats alone).
        Assert.All(result.Files.Keys, path => Assert.Contains("/battle/btl/", path));
        foreach (string battle in new[] { "znkd08_00", "znkd08_01" })
            Assert.False(result.Files.ContainsKey(EncounterFiles.PathFor(battle)) &&
                         !EncounterFiles.Monsters(result.Files[EncounterFiles.PathFor(battle)]).SequenceEqual(EncounterFiles.Monsters(original.ReadFile(EncounterFiles.PathFor(battle)))),
                $"{battle} changed");
    }

    [Fact]
    public void Swap_gives_newcomers_the_replaced_enemys_stats()
    {
        if (Original.Value is not { } original) return;

        var result = RandomizerEngine.Run(original, Settings("swap"), new Seed("swap"));
        foreach (var pair in (JsonArray)result.Spoiler[EnemyEncountersFeature.Section]!)
        {
            int from = (int)pair!["replacedIndex"]!, to = (int)pair["byIndex"]!;
            if (KeepsOwnStats(original, from, to)) continue;
            var replaced =new MonsterFile(from, original.ReadFile(MonsterFile.PathFor(from))).Stats;
            var now = new MonsterFile(to, result.Files[MonsterFile.PathFor(to)]).Stats;
            Assert.Equal(replaced.Hp, now.Hp);
            Assert.Equal(replaced.Strength, now.Strength);
            Assert.Equal(replaced.Defense, now.Defense);
        }
    }

    /// <summary>Pairs involving the battle chest or a 1 HP placeholder are moved without any stat change.</summary>
    private static bool KeepsOwnStats(VbfArchive original, int from, int to) =>
        from == EnemyEncountersFeature.BattleTreasureChest || to == EnemyEncountersFeature.BattleTreasureChest ||
        new MonsterFile(from, original.ReadFile(MonsterFile.PathFor(from))).Stats.Hp <= 1 ||
        new MonsterFile(to, original.ReadFile(MonsterFile.PathFor(to))).Stats.Hp <= 1;

    [Fact]
    public void Scale_lands_between_the_two_enemies()
    {
        if (Original.Value is not { } original) return;

        var result = RandomizerEngine.Run(original, Settings("scale"), new Seed("scale"));
        foreach (var pair in (JsonArray)result.Spoiler[EnemyEncountersFeature.Section]!)
        {
            int from = (int)pair!["replacedIndex"]!, to = (int)pair["byIndex"]!;
            if (KeepsOwnStats(original, from, to)) continue;
            var a =new MonsterFile(from, original.ReadFile(MonsterFile.PathFor(from))).Stats;
            var b = new MonsterFile(to, original.ReadFile(MonsterFile.PathFor(to))).Stats;
            var now = new MonsterFile(to, result.Files[MonsterFile.PathFor(to)]).Stats;
            Assert.InRange(now.Hp, Math.Min(a.Hp, b.Hp), Math.Max(a.Hp, b.Hp));
            Assert.InRange(now.Strength, Math.Min(a.Strength, b.Strength), Math.Max(a.Strength, b.Strength));
            Assert.Equal(b.Defense, now.Defense); // FFXRando's scaling leaves defenses alone
        }
    }

    [Fact]
    public void Enemy_stats_respect_sane_limits()
    {
        if (Original.Value is not { } original) return;

        var result = RandomizerEngine.Run(original, Settings(stats: "random"), new Seed("sane-stats"));
        int changed = 0;
        foreach (int index in MonsterFile.AllIndices(original))
        {
            var before = new MonsterFile(index, original.ReadFile(MonsterFile.PathFor(index)));
            if (!result.Files.TryGetValue(MonsterFile.PathFor(index), out byte[]? bytes)) continue;
            var after = new MonsterFile(index, bytes).Stats;
            changed++;
            Assert.True(after.Hp >= 50, $"m{index:D3} HP {after.Hp}");
            Assert.InRange(after.Hp, Math.Min(50u, before.Stats.Hp), Math.Max(50u, before.Stats.Hp * 3));
            Assert.InRange(after.Strength, before.Stats.Strength / 3, Math.Min(255, (int)Math.Round(before.Stats.Strength * 5.0 / 3)));
        }
        Assert.True(changed > 300);
    }

    [Fact]
    public void Same_seed_makes_the_same_game()
    {
        if (Original.Value is not { } original) return;

        var settings = Settings("scale", "shufflenorm", rewards: true);
        var a = RandomizerEngine.Run(original, settings, new Seed("replay"));
        var b = RandomizerEngine.Run(original, settings, new Seed("replay"));
        Assert.Equal(a.Files.Keys.Order(), b.Files.Keys.Order());
        foreach (var (path, bytes) in a.Files) Assert.Equal(bytes, b.Files[path]);
        Assert.Equal(a.Spoiler.ToJsonString(), b.Spoiler.ToJsonString());
    }

    [Fact]
    public void Checks_catch_a_battle_file_edited_outside_the_monster_list()
    {
        if (Original.Value is not { } original) return;

        var context = new RandomizerContext(original, Settings("random"), new Seed("sabotage"));
        var feature = new EnemyEncountersFeature();
        feature.Apply(context);
        Assert.Empty(feature.Validate(context));

        string path = context.ChangedFiles.Keys.First(p => p.Contains("/battle/btl/"));
        context.Edit(path)[0x20] ^= 1;
        Assert.NotEmpty(feature.Validate(context));
    }
}
