using FfxRandomizer.Core.GameData;

namespace FfxRandomizer.Core.Randomization.Features;

/// <summary>Poison takes 50% of max HP a turn instead of 25%, for every character and aeon (FFXRando's poison_is_deadly).</summary>
public sealed class DeadlyPoisonFeature : IRandomizerFeature
{
    public const byte Percent = 50;

    public bool IsEnabled(RandomizerSettings s) => s.IsOn("poison");

    public void Apply(RandomizerContext context)
    {
        var tables = LanguageTables.Edit(context, PlySave.FileName);
        int was = tables[0].Record(PlySave.Tidus)[PlySave.PoisonDamage];
        foreach (var table in tables)
            for (int i = 0; i < table.RecordCount; i++) table.Record(i)[PlySave.PoisonDamage] = Percent;
        context.Spoil("rules", new { Rule = "Poison damage per turn", From = $"{was}% of max HP", To = $"{Percent}% of max HP" });
    }

    public IEnumerable<string> Validate(RandomizerContext context)
    {
        foreach (string p in LanguageTables.CheckInSync(context, PlySave.FileName, 0x02)) yield return p;
        var table = new KernelTable(context.ChangedFiles[GamePaths.LanguageKernel(LanguageTables.Reference, PlySave.FileName)]);
        for (int i = 0; i < table.RecordCount; i++)
            if (table.Record(i)[PlySave.PoisonDamage] != Percent) yield return $"ply_save record {i}: poison damage {table.Record(i)[PlySave.PoisonDamage]}%.";
    }
}
