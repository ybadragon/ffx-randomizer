using System.Text.Json.Nodes;
using FfxRandomizer.Core.Game;
using FfxRandomizer.Core.GameData;
using FfxRandomizer.Core.Patching;
using FfxRandomizer.Core.Randomization;
using FfxRandomizer.Core.Randomization.Features;
using FfxRandomizer.Core.Storage;
using FfxRandomizer.Core.Vbf;

namespace FfxRandomizer.Tests;

/// <summary>Gear features against the installed game's original data (read-only). Pass trivially without the game.</summary>
public class GearTests
{
    private static readonly Lazy<VbfArchive?> Original = new(() =>
        GameInstall.FindSteamInstall() is { } game ? new PatchManager(game.DataArchivePath, AppPaths.Default).OpenOriginal() : null);

    private static readonly Lazy<IGameNames?> Names = new(() => Original.Value is { } o ? GameText.Load(o) : null);

    private static readonly string[] Bits = ["sane", "celestial", "brotherhood", "wprices", "crit", "atk", "formula", "geardrops"];

    private static RandomizerSettings Settings(int variant)
    {
        var s = new RandomizerSettings();
        foreach (string id in new[] { "gearab", "wshops", "wstats", "rewards" }) s.Toggles[id] = true;
        for (int bit = 0; bit < Bits.Length; bit++) s.Toggles[Bits[bit]] = (variant & (1 << bit)) != 0;
        return s;
    }

    [Fact]
    public void Every_combination_of_options_passes_its_checks()
    {
        if (Original.Value is not { } original) return;

        for (int variant = 0; variant < 1 << Bits.Length; variant++)
        {
            var result = RandomizerEngine.Run(original, Settings(variant), new Seed($"gear-{variant}"), Names.Value);
            Assert.True(result.Problems.Count == 0, $"variant {variant}: {string.Join("; ", result.Problems.Take(5))}");
        }
    }

    [Fact]
    public void Everything_on_passes_over_many_seeds()
    {
        if (Original.Value is not { } original) return;

        for (int i = 0; i < 30; i++)
        {
            var s = Settings(i % 2 == 0 ? 0xFF : 0xFE);
            foreach (string id in new[] { "tidus", "drops", "steals", "bribes", "elements", "field", "shops", "custom", "shopprices", "spheres" }) s.Toggles[id] = true;
            var result = RandomizerEngine.Run(original, s, new Seed($"gear-all-{i}"), Names.Value);
            Assert.True(result.Problems.Count == 0, $"seed gear-all-{i}: {string.Join("; ", result.Problems.Take(5))}");
        }
    }

    [Fact]
    public void Checks_catch_a_broken_gear_file()
    {
        if (Original.Value is not { } original) return;

        var s = Settings(0xFF);
        var context = new RandomizerContext(original, s, new Seed("break-me"));
        foreach (var feature in RandomizerEngine.Features.Where(f => f.IsEnabled(s))) feature.Apply(context);
        var gear = new KernelTable(context.ChangedFiles[GearFiles.ShopGear]);
        new GearRecord(gear.Bytes.AsMemory(gear.DataOffset + 5 * gear.RecordSize, gear.RecordSize)).SetAbility(0, 0x7777);
        gear.Record(6)[0x04] ^= 1;

        var problems = RandomizerEngine.Features.Where(f => f.IsEnabled(s)).SelectMany(f => f.Validate(context)).ToList();
        Assert.Contains(problems, p => p.Contains("shop_arms #5") && p.Contains("0x7777"));
        Assert.Contains(problems, p => p.Contains("shop_arms #6: byte 0x04 changed"));
    }

    [Fact]
    public void Sane_formulas_always_deal_damage_and_sane_enemies_without_gear_drop_none()
    {
        if (Original.Value is not { } original) return;

        var result = RandomizerEngine.Run(original, Settings(0xFF), new Seed("sane-formulas"), Names.Value);
        foreach (var (_, _, gear) in GearFiles.ReadAll(path => result.Files[path]))
            if (!gear.IsArmor && !gear.IsCelestial && gear.Power > 0)
                Assert.Contains(gear.Formula, WeaponFormulas.Sane);

        foreach (int index in MonsterFile.AllIndices(original))
        {
            var before = new MonsterFile(index, original.ReadFile(MonsterFile.PathFor(index)));
            if (!before.HasLoot || before.Loot.GearDropChance != 0) continue;
            byte[] after = result.Files.TryGetValue(MonsterFile.PathFor(index), out byte[]? b) ? b : before.Bytes;
            Assert.Equal(0, new MonsterFile(index, after).Loot.GearDropChance);
        }
    }

    [Fact]
    public void Monster_gear_rolls_use_more_than_one_ability()
    {
        if (Original.Value is not { } original) return;

        // FFXRando wrote one ability into all eight slots; each roll should be drawn on its own.
        var result = RandomizerEngine.Run(original, Settings(0xFF), new Seed("rolls"), Names.Value);
        var entries = ((JsonArray)result.Spoiler[EnemyRewardsFeature.Section]!).OfType<JsonObject>().Where(e => e["gear"] is not null).ToList();
        Assert.NotEmpty(entries);
        int varied = entries.Count(e => ((JsonArray)e["gear"]!["weapon"]!["Tidus"]!["rolls"]!).Select(n => (string?)n).Distinct().Count() > 1);
        Assert.True(varied > entries.Count * 9 / 10, $"{varied} of {entries.Count} enemies have varied rolls");
    }

    [Fact]
    public void Same_seed_gives_the_same_gear()
    {
        if (Original.Value is not { } original) return;

        var a = RandomizerEngine.Run(original, Settings(0xFE), new Seed("again"));
        var b = RandomizerEngine.Run(original, Settings(0xFE), new Seed("again"));
        foreach (string path in new[] { GearFiles.Weapons, GearFiles.ShopGear, GearFiles.TreasureGear, GearFiles.Shops, GearFiles.AbilityPrices })
            Assert.Equal(a.Files[path], b.Files[path]);
    }
}
