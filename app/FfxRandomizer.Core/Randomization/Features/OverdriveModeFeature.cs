using System.Buffers.Binary;
using FfxRandomizer.Core.GameData;

namespace FfxRandomizer.Core.Randomization.Features;

/// <summary>
/// Each party member except Auron starts in a random Overdrive mode (ply_save.bin 0x38), learned from the start.
/// Follows FFXRando's randomize_starting_overdrive_mode; Auron stays Stoic because the opening tutorial needs it.
/// </summary>
public sealed class OverdriveModeFeature : IRandomizerFeature
{
    private const int Stoic = 2;

    public bool IsEnabled(RandomizerSettings s) => s.IsOn("od");

    public void Apply(RandomizerContext context)
    {
        var rng = context.RngFor("party.overdrive");
        var tables = LanguageTables.Edit(context, PlySave.FileName);
        for (int i = 0; i < PlySave.PartySize; i++)
        {
            if (i == PlySave.Auron) continue;
            int mode = rng.Next(0, AeonFiles.OverdriveModes.Length - 1);
            int was = tables[0].Record(i)[PlySave.OverdriveMode];
            foreach (var table in tables)
            {
                var r = table.Record(i);
                r[PlySave.OverdriveMode] = (byte)mode;
                uint learned = BinaryPrimitives.ReadUInt32LittleEndian(r[PlySave.OverdriveLearned..]);
                if (mode != Stoic) learned &= ~(1u << Stoic);
                BinaryPrimitives.WriteUInt32LittleEndian(r[PlySave.OverdriveLearned..], learned | 1u << mode);
            }
            PartyStatsFeature.SpoilerEntry(context, i)["overdriveMode"] =
                RandomizerContext.ToNode(new { From = AeonFiles.OverdriveModes[was], To = AeonFiles.OverdriveModes[mode] });
        }
    }

    public IEnumerable<string> Validate(RandomizerContext context)
    {
        foreach (string p in LanguageTables.CheckInSync(context, PlySave.FileName, 0x02)) yield return p;
        var table = new KernelTable(context.ChangedFiles[GamePaths.LanguageKernel(LanguageTables.Reference, PlySave.FileName)]);
        var original = LanguageTables.Original(context, PlySave.FileName);
        for (int i = 0; i < PlySave.PartySize; i++)
        {
            var r = table.Record(i).ToArray();
            int mode = r[PlySave.OverdriveMode];
            string who = KernelFiles.CharacterNames[i];
            if (mode >= AeonFiles.OverdriveModes.Length) yield return $"{who}: Overdrive mode {mode}.";
            else if (BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(PlySave.OverdriveCounters + mode * 2)) == 0xFFFF) yield return $"{who}: can't learn mode {mode}.";
            else if ((BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(PlySave.OverdriveLearned)) & 1u << mode) == 0) yield return $"{who}: mode {mode} isn't learned.";
            if (i == PlySave.Auron && mode != original.Record(i)[PlySave.OverdriveMode]) yield return "Auron's Overdrive mode changed.";
        }
    }
}
