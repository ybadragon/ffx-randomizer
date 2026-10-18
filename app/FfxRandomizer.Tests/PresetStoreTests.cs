using FfxRandomizer.Core.Randomization;
using FfxRandomizer.Core.Storage;

namespace FfxRandomizer.Tests;

public class PresetStoreTests
{
    [Fact]
    public void Presets_survive_a_reload_and_names_stay_unique()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        var settings = RandomizerSettings.Defaults();
        settings.Toggles["tidus"] = true;

        var store = new PresetStore(paths);
        var first = store.Add("Friday night chaos", settings);
        var second = store.Add("friday night chaos", settings);
        Assert.Equal("friday night chaos 2", second.Name);

        settings.Toggles["od"] = true;
        store.Update(first.Id, settings);
        store.Rename(second.Id, "Race rules");

        var reloaded = new PresetStore(paths);
        Assert.Equal(["Friday night chaos", "Race rules"], reloaded.Mine.Select(p => p.Name));
        Assert.True(reloaded.Find(first.Id)!.Settings.IsOn("od"));
        Assert.Throws<InvalidOperationException>(() => reloaded.Rename(second.Id, "Chaos"));
    }

    [Fact]
    public void Built_in_presets_are_read_only_but_can_be_duplicated()
    {
        using var dir = new TempDir();
        var store = new PresetStore(new AppPaths(dir.Path));
        var chaos = PresetStore.BuiltIns.Single(p => p.Name == "Chaos");

        Assert.Throws<InvalidOperationException>(() => store.Delete(chaos.Id));
        var copy = store.Duplicate(chaos.Id);
        Assert.Equal("Chaos copy", copy.Name);
        Assert.True(copy.Settings.SameAs(chaos.Settings));
        Assert.Equal(chaos.Id, PresetStore.BuiltIns.Single(p => p.Name == "Chaos").Id);
    }
}
