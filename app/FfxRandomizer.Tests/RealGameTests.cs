using FfxRandomizer.Core.Game;
using FfxRandomizer.Core.GameData;
using FfxRandomizer.Core.Patching;
using FfxRandomizer.Core.Randomization;
using FfxRandomizer.Core.Storage;

namespace FfxRandomizer.Tests;

/// <summary>Read-only checks against the installed game. They pass trivially on machines without it.</summary>
public class RealGameTests
{
    [Fact]
    public void Tidus_starts_with_520_hp_in_the_original_data()
    {
        var game = GameInstall.FindSteamInstall();
        if (game is null) return;

        var original = new PatchManager(game.DataArchivePath, AppPaths.Default).OpenOriginal();
        foreach (string language in GamePaths.LanguageFolders)
        {
            var table = new KernelTable(original.ReadFile(GamePaths.LanguageKernel(language, PlySave.FileName)));
            Assert.Equal(520u, table.ReadUInt32(PlySave.Tidus, PlySave.MaxHp));
        }
    }

    [Fact]
    public void Tidus_mod_keeps_every_file_the_same_size()
    {
        var game = GameInstall.FindSteamInstall();
        if (game is null) return;

        var original = new PatchManager(game.DataArchivePath, AppPaths.Default).OpenOriginal();
        var settings = RandomizerSettings.Defaults();
        settings.Toggles["tidus"] = true;
        var result = RandomizerEngine.Run(original, settings, new Seed("1"));

        Assert.Equal(8, result.Files.Count);
        foreach (var (name, bytes) in result.Files)
            Assert.Equal(original.SizeOf(original.IndexOf(name)), bytes.Length);
    }
}
