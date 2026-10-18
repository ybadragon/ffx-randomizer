using System.Buffers.Binary;
using System.Text.Json.Nodes;
using FfxRandomizer.Core.GameData;

namespace FfxRandomizer.Core.Randomization.Features;

/// <summary>
/// Aeon base stats: the least each aeon's stats can be at each of the 20 tiers in sum_assure.bin (the tiers follow the battle count: 60 battles, then every 30 up to 600).
/// Randomize scales each aeon's HP, MP, Strength, Defense, Magic, Magic Defense, Agility and Accuracy by its own
/// factor (spread like FFXRando's normal(value, value / 2)) across every tier, so aeons still grow as the battle count rises.
/// Shuffle swaps whole aeons. FFXRando read this table in 120-byte steps, so it changed only Valefor and shuffled
/// whole tiers instead of aeons; this applies what the option describes.
/// </summary>
public sealed class AeonStatsFeature : IRandomizerFeature
{
    public const string Section = "aeons";
    private const int Aeons = 10;

    private static readonly OptionGroup Group = OptionCatalog.AllGroups.First(g => g.Id == "abase");

    /// <summary>The stats changed: name, offset, size in bytes, lowest and highest value.</summary>
    private static readonly (string Name, int Offset, int Size, int Min, int Max)[] Stats =
    [
        ("hp", AeonFiles.Hp, 2, 50, ushort.MaxValue), ("mp", AeonFiles.Mp, 2, 1, 9999),
        ("strength", AeonFiles.Str, 1, 1, 255), ("defense", AeonFiles.Def, 1, 1, 255), ("magic", AeonFiles.Mag, 1, 1, 255),
        ("magicDefense", AeonFiles.Mdef, 1, 1, 255), ("agility", AeonFiles.Agi, 1, 1, 255), ("accuracy", AeonFiles.Acc, 1, 1, 255),
    ];

    public bool IsEnabled(RandomizerSettings s) => s.ChoiceOf(Group) != "orig";

    private static int Read(ReadOnlySpan<byte> r, int offset, int size) => size == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(r[offset..]) : r[offset];

    private static void Write(Span<byte> r, int offset, int size, int value)
    {
        if (size == 2) BinaryPrimitives.WriteUInt16LittleEndian(r[offset..], (ushort)value);
        else r[offset] = (byte)value;
    }

    public void Apply(RandomizerContext context)
    {
        var rng = context.RngFor("aeons.stats");
        var table = new KernelTable(context.Edit(AeonFiles.SumAssure));
        int tiers = table.RecordCount / Aeons;
        var before = Enumerable.Range(0, table.RecordCount).Select(r => table.Record(r).ToArray()).ToList();
        int[] source = Enumerable.Range(0, Aeons).ToArray();
        var factors = new Dictionary<int, int[]>();

        if (context.Settings.ChoiceOf(Group) == "shuffle")
        {
            rng.Shuffle(source);
            for (int t = 0; t < tiers; t++)
                for (int a = 0; a < Aeons; a++) before[t * Aeons + source[a]].CopyTo(table.Record(t * Aeons + a));
        }
        else
        {
            for (int a = 0; a < Aeons; a++)
            {
                // Percent of the original value, spread like normal(value, value / 2).
                int[] percent = Stats.Select(_ => rng.Normal(100, 50, 10, 300)).ToArray();
                factors[a] = percent;
                for (int t = 0; t < tiers; t++)
                {
                    var r = table.Record(t * Aeons + a);
                    for (int s = 0; s < Stats.Length; s++)
                    {
                        var (_, offset, size, min, max) = Stats[s];
                        int value = Read(r, offset, size);
                        if (value != 0) Write(r, offset, size, Math.Clamp((int)Math.Round(value * percent[s] / 100.0), min, max));
                    }
                }
            }
        }

        for (int a = 0; a < Aeons; a++)
        {
            var stats = new JsonObject();
            for (int s = 0; s < Stats.Length; s++)
            {
                var (name, offset, size, _, _) = Stats[s];
                stats[name] = RandomizerContext.ToNode(new
                {
                    From = Read(before[a], offset, size),
                    To = Read(table.Record(a), offset, size),
                    LastTier = new { From = Read(before[(tiers - 1) * Aeons + a], offset, size), To = Read(table.Record((tiers - 1) * Aeons + a), offset, size) },
                });
            }
            var entry = AeonGrowthFeature.SpoilerEntry(context, a);
            if (source[a] != a) entry["baseStatsFrom"] = AeonFiles.Names[source[a]];
            if (factors.TryGetValue(a, out int[]? percent))
                entry["baseStatsPercent"] = RandomizerContext.ToNode(Stats.Select((s, i) => (s.Name, Value: percent[i])).ToDictionary(p => p.Name, p => p.Value));
            entry["baseStats"] = stats;
        }
    }

    public IEnumerable<string> Validate(RandomizerContext context)
    {
        var original = new KernelTable(context.Original.ReadFile(AeonFiles.SumAssure));
        var table = new KernelTable(context.ChangedFiles[AeonFiles.SumAssure]);
        if (table.Bytes.Length != original.Bytes.Length || !table.Bytes.AsSpan(0, 20).SequenceEqual(original.Bytes.AsSpan(0, 20)))
        {
            yield return "sum_assure.bin: size or header changed.";
            yield break;
        }
        int tiers = table.RecordCount / Aeons;
        foreach (var (name, offset, size, _, max) in Stats)
        {
            // Every aeon's minimums still rise (or hold) from tier to tier where the original's did.
            bool originalRises = Enumerable.Range(0, Aeons).All(a => Enumerable.Range(1, tiers - 1).All(t =>
                Read(original.Record(t * Aeons + a), offset, size) >= Read(original.Record((t - 1) * Aeons + a), offset, size)));
            for (int a = 0; a < Aeons; a++)
                for (int t = 0; t < tiers; t++)
                {
                    int value = Read(table.Record(t * Aeons + a), offset, size);
                    if (value > max) yield return $"{AeonFiles.Names[a]} tier {t}: {name} {value}.";
                    if (originalRises && t > 0 && value < Read(table.Record((t - 1) * Aeons + a), offset, size))
                        yield return $"{AeonFiles.Names[a]} tier {t}: {name} drops below the tier before.";
                }
        }
        for (int a = 0; a < Aeons; a++)
            if (Read(table.Record(a), AeonFiles.Hp, 2) < 50) yield return $"{AeonFiles.Names[a]}: HP below 50.";
    }
}
