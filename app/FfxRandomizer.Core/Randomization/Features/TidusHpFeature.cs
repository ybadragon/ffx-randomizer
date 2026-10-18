using FfxRandomizer.Core.GameData;

namespace FfxRandomizer.Core.Randomization.Features;

/// <summary>Gives Tidus 9999 HP from the start: an easy in-game check that patching works.</summary>
public sealed class TidusHpFeature : IRandomizerFeature
{
    public const uint Hp = 9999;

    public bool IsEnabled(RandomizerSettings settings) => settings.IsOn("tidus");

    public void Apply(RandomizerContext context)
    {
        uint before = 0;
        foreach (string language in GamePaths.LanguageFolders)
        {
            var table = new KernelTable(context.Edit(GamePaths.LanguageKernel(language, PlySave.FileName)));
            before = table.ReadUInt32(PlySave.Tidus, PlySave.MaxHp);
            table.WriteUInt32(PlySave.Tidus, PlySave.BaseHp, Hp);
            table.WriteUInt32(PlySave.Tidus, PlySave.CurrentHp, Hp);
            table.WriteUInt32(PlySave.Tidus, PlySave.MaxHp, Hp);
        }
        context.Spoil("party", new { Character = "Tidus", Hp = new { From = before, To = Hp } });
    }

    public IEnumerable<string> Validate(RandomizerContext context)
    {
        foreach (string language in GamePaths.LanguageFolders)
        {
            var table = new KernelTable(context.ChangedFiles[GamePaths.LanguageKernel(language, PlySave.FileName)]);
            if (table.ReadUInt32(PlySave.Tidus, PlySave.MaxHp) != Hp) yield return $"Tidus HP wasn't set in {language}.";
        }
    }
}
