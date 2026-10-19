using System.Buffers.Binary;
using FfxRandomizer.Core.Vbf;

namespace FfxRandomizer.Core.GameData;

/// <summary>The eight Blitzball stats, in the order the spoiler log lists them.</summary>
public enum BlitzStat { HP, SP, EN, AT, PA, BL, SH, CA }

/// <summary>
/// One stat's growth curve for one Blitzball player: four floats (a, b, c, kind). The game computes the stat at a level as
/// <list type="bullet">
/// <item>kind 0: a + b·Lv</item>
/// <item>kind 1: a + b·Lv^c</item>
/// <item>kind 2: a + b·Lv − c·Lv²</item>
/// <item>kind 3: a + b·Lv + c·Lv²</item>
/// <item>kind −1: unused slot (the stat is always 1)</item>
/// </list>
/// </summary>
public readonly record struct BlitzGrowth(float A, float B, float C, float Kind)
{
    public const int Size = 16;

    public bool IsUnused => Kind == -1;

    public double ValueAt(int level)
    {
        double a = A, b = B, c = C, lv = level;
        return Kind switch
        {
            -1 => 1,
            1 => a + b * Math.Pow(lv, c),
            2 => a + b * lv - c * lv * lv,
            3 => a + b * lv + c * lv * lv,
            _ => a + b * lv,
        };
    }

    /// <summary>The curve with its starting value scaled by <paramref name="baseFactor"/> and its growth by <paramref name="growthFactor"/>.</summary>
    public BlitzGrowth Scaled(double baseFactor, double growthFactor) => IsUnused ? this : Kind == 1
        // For kind 1, C is an exponent, not a growth amount, so only B carries the growth.
        ? this with { A = (float)(A * baseFactor), B = (float)(B * growthFactor) }
        : this with { A = (float)(A * baseFactor), B = (float)(B * growthFactor), C = (float)(C * growthFactor) };

    public string Formula()
    {
        if (IsUnused) return "1";
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        string lv = Kind == 1 ? $"Lv^{C.ToString("G5", invariant)}" : "Lv";
        var parts = new List<string> { A.ToString("G5", invariant) };
        if (B != 0) parts.Add($"{B.ToString("G5", invariant)}·{lv}");
        if (C != 0 && Kind is 2 or 3) parts.Add($"{(Kind == 2 ? -C : C).ToString("G5", invariant)}·Lv²");
        return string.Join(" + ", parts).Replace("+ -", "− ");
    }
}

/// <summary>
/// Blitzball player stat growth tables. They aren't in a kernel file: each is a 240-float array (60 players × 4 floats) in the
/// shared data of an event script, and every event that shows or uses player stats (the Blitzball matches, and the scouting
/// conversations in towns) carries its own copy. bltz0002 is read as the reference; a change is written into every copy.
/// </summary>
public sealed class BlitzballStats
{
    public const int PlayerCount = 60;
    public const int TableBytes = PlayerCount * BlitzGrowth.Size;

    /// <summary>The event read for the original tables, and the indices of its stat variables (see <see cref="BlitzStat"/>).</summary>
    public const string ReferenceEvent = "bltz0002";
    private static readonly Dictionary<BlitzStat, int> ReferenceVariables = new()
    {
        [BlitzStat.HP] = 0x126, [BlitzStat.AT] = 0x128, [BlitzStat.EN] = 0x129, [BlitzStat.PA] = 0x12A,
        [BlitzStat.SH] = 0x12B, [BlitzStat.BL] = 0x12C, [BlitzStat.CA] = 0x12D, [BlitzStat.SP] = 0x12E,
    };

    public static string EventPath(string eventId) => $"{GamePaths.Master}jppc/event/obj/{eventId[..2]}/{eventId}/{eventId}.ebp";

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<VbfArchive, IReadOnlyList<BlitzballCopy>> CopiesCache = [];

    /// <summary>
    /// Every event file holding the tables, with where each table sits. The copies are found by content, since each event declares
    /// them as different variables (and there are Vita, Chinese and debug variants of several events). A file holding only some of
    /// the tables, or one twice, is a layout this code doesn't understand, so it throws rather than half-edit it.
    /// </summary>
    public static IReadOnlyList<BlitzballCopy> FindCopies(VbfArchive original) => CopiesCache.GetValue(original, archive =>
    {
        var reference = Read(archive);
        var tables = Enum.GetValues<BlitzStat>().ToDictionary(s => s, s => reference.Encode(s));
        var copies = new List<BlitzballCopy>();
        foreach (string path in archive.Names.Where(n => n.EndsWith(".ebp", StringComparison.OrdinalIgnoreCase)))
        {
            byte[] bytes = archive.ReadFile(path);
            var found = tables.ToDictionary(t => t.Key, t => FindTable(bytes, t.Value));
            if (found.Values.All(f => f.Count == 0)) continue;
            if (found.Values.Any(f => f.Count != 1))
                throw new InvalidDataException($"{path} holds an unexpected mix of Blitzball stat tables ({string.Join(", ", found.Select(f => $"{f.Key}×{f.Value.Count}"))}).");
            copies.Add(new BlitzballCopy(path, found.ToDictionary(f => f.Key, f => f.Value[0])));
        }
        return copies;
    });

    private readonly Dictionary<BlitzStat, BlitzGrowth[]> _tables;

    private BlitzballStats(Dictionary<BlitzStat, BlitzGrowth[]> tables) => _tables = tables;

    public BlitzGrowth this[int player, BlitzStat stat]
    {
        get => _tables[stat][player];
        set => _tables[stat][player] = value;
    }

    public static BlitzballStats Read(VbfArchive archive) => FromEvent(archive.ReadFile(EventPath(ReferenceEvent)));

    /// <summary>Reads the tables from the bytes of bltz0002.ebp.</summary>
    public static BlitzballStats FromEvent(byte[] ebp)
    {
        var script = new EventScript(ebp);
        var tables = new Dictionary<BlitzStat, BlitzGrowth[]>();
        foreach (var (stat, variable) in ReferenceVariables)
            tables[stat] = Decode(ebp.AsSpan(script.SharedArrayOffset(variable, 4 * PlayerCount), TableBytes));
        return new BlitzballStats(tables);
    }

    /// <summary>Reads the tables at the places <paramref name="copy"/> records, from a (possibly edited) copy of that event.</summary>
    public static BlitzballStats FromCopy(byte[] ebp, BlitzballCopy copy) =>
        new(copy.Offsets.ToDictionary(o => o.Key, o => Decode(ebp.AsSpan(o.Value, TableBytes))));

    /// <summary>Writes these tables over the copy in <paramref name="ebp"/>.</summary>
    public void WriteTo(byte[] ebp, BlitzballCopy copy)
    {
        foreach (var (stat, offset) in copy.Offsets) Encode(stat).CopyTo(ebp, offset);
    }

    public BlitzballStats Clone() => new(_tables.ToDictionary(t => t.Key, t => (BlitzGrowth[])t.Value.Clone()));

    public byte[] Encode(BlitzStat stat)
    {
        var bytes = new byte[TableBytes];
        var span = bytes.AsSpan();
        for (int p = 0; p < PlayerCount; p++)
        {
            var g = _tables[stat][p];
            BinaryPrimitives.WriteSingleLittleEndian(span[(p * 16)..], g.A);
            BinaryPrimitives.WriteSingleLittleEndian(span[(p * 16 + 4)..], g.B);
            BinaryPrimitives.WriteSingleLittleEndian(span[(p * 16 + 8)..], g.C);
            BinaryPrimitives.WriteSingleLittleEndian(span[(p * 16 + 12)..], g.Kind);
        }
        return bytes;
    }

    private static BlitzGrowth[] Decode(ReadOnlySpan<byte> table)
    {
        var growth = new BlitzGrowth[PlayerCount];
        for (int p = 0; p < PlayerCount; p++)
        {
            var r = table[(p * 16)..];
            growth[p] = new BlitzGrowth(
                BinaryPrimitives.ReadSingleLittleEndian(r), BinaryPrimitives.ReadSingleLittleEndian(r[4..]),
                BinaryPrimitives.ReadSingleLittleEndian(r[8..]), BinaryPrimitives.ReadSingleLittleEndian(r[12..]));
        }
        return growth;
    }

    /// <summary>Every place in <paramref name="ebp"/> holding exactly <paramref name="table"/>.</summary>
    public static List<int> FindTable(ReadOnlySpan<byte> ebp, ReadOnlySpan<byte> table)
    {
        var found = new List<int>();
        int start = 0;
        while (start <= ebp.Length - table.Length)
        {
            int at = ebp[start..].IndexOf(table);
            if (at < 0) break;
            found.Add(start + at);
            start += at + 4;
        }
        return found;
    }
}

/// <summary>One event file's copy of the stat tables: the file offset of each table.</summary>
public sealed record BlitzballCopy(string Path, IReadOnlyDictionary<BlitzStat, int> Offsets);

