using System.Text.Json.Nodes;
using FfxRandomizer.Core.GameData;

namespace FfxRandomizer.Core.Randomization.Features;

/// <summary>
/// How each aeon's stats grow with Yuna's (ply_rom.bin records 8-17, coefficients 0x18-0x29), randomized or shuffled
/// between aeons. FFXRando's randomize/shuffleAeonStatScaling, applied to the aeon records: its reader stops after the
/// first 8 records, which are the party, so upstream never actually changed anything. A coefficient that is set stays
/// at least 1, since the A coefficients divide.
/// </summary>
public sealed class AeonGrowthFeature : IRandomizerFeature
{
    private static readonly OptionGroup Group = OptionCatalog.AllGroups.First(g => g.Id == "ascale");
    private const int Length = PlyRom.CoefficientsEnd - PlyRom.Coefficients;

    public bool IsEnabled(RandomizerSettings s) => s.ChoiceOf(Group) != "orig";

    public static JsonObject SpoilerEntry(RandomizerContext context, int aeon) =>
        context.SpoilerEntry(AeonStatsFeature.Section, AeonFiles.Names[aeon], new { Aeon = AeonFiles.Names[aeon] });

    public void Apply(RandomizerContext context)
    {
        var rng = context.RngFor("aeons.growth");
        var tables = LanguageTables.Edit(context, PlyRom.FileName);
        int count = AeonFiles.Names.Length;
        var before = Enumerable.Range(0, count).Select(a => tables[0].Record(PlyRom.FirstAeon + a).Slice(PlyRom.Coefficients, Length).ToArray()).ToList();
        int[] source = Enumerable.Range(0, count).ToArray();
        List<byte[]> after;
        if (context.Settings.ChoiceOf(Group) == "shuffle")
        {
            rng.Shuffle(source);
            after = source.Select(a => before[a]).ToList();
        }
        else
        {
            after = before.Select(c => c.Select(v => (byte)LanguageTables.Spread(rng, v, 1, 255)).ToArray()).ToList();
        }

        foreach (var table in tables)
            for (int a = 0; a < count; a++) after[a].CopyTo(table.Record(PlyRom.FirstAeon + a)[PlyRom.Coefficients..]);

        for (int a = 0; a < count; a++)
        {
            var growth = new JsonObject();
            for (int s = 0; s < PlyRom.StatNames.Length; s++)
                growth[PlyRom.StatNames[s]] = RandomizerContext.ToNode(new
                {
                    From = new int[] { before[a][s * 2], before[a][s * 2 + 1] },
                    To = new int[] { after[a][s * 2], after[a][s * 2 + 1] },
                });
            var entry = SpoilerEntry(context, a);
            if (source[a] != a) entry["growthFrom"] = AeonFiles.Names[source[a]];
            entry["growth"] = growth;
        }
    }

    public IEnumerable<string> Validate(RandomizerContext context)
    {
        foreach (string p in LanguageTables.CheckInSync(context, PlyRom.FileName, 0x10)) yield return p;
        var table = new KernelTable(context.ChangedFiles[GamePaths.LanguageKernel(LanguageTables.Reference, PlyRom.FileName)]);
        var original = LanguageTables.Original(context, PlyRom.FileName);
        var originalSets = Enumerable.Range(0, AeonFiles.Names.Length)
            .Select(a => original.Record(PlyRom.FirstAeon + a).Slice(PlyRom.Coefficients, Length).ToArray()).ToList();
        for (int r = 0; r < table.RecordCount; r++)
        {
            byte[] now = table.Record(r).ToArray(), was = original.Record(r).ToArray();
            bool aeon = r >= PlyRom.FirstAeon && r < PlyRom.FirstAeon + AeonFiles.Names.Length;
            for (int b = 0; b < now.Length; b++)
            {
                bool coefficient = aeon && b >= PlyRom.Coefficients && b < PlyRom.CoefficientsEnd;
                if (!coefficient && now[b] != was[b]) yield return $"ply_rom record {r}: byte 0x{b:X2} changed.";
            }
            if (!aeon) continue;
            // A zero coefficient means "doesn't grow" and must stay zero; a set one must stay set. Shuffling moves whole sets.
            byte[] coefficients = now[PlyRom.Coefficients..PlyRom.CoefficientsEnd];
            bool matchesSome = originalSets.Any(set => set.Select(v => v == 0).SequenceEqual(coefficients.Select(v => v == 0)));
            if (!matchesSome) yield return $"{AeonFiles.Names[r - PlyRom.FirstAeon]}: growth coefficients turned on or off.";
        }
    }
}
