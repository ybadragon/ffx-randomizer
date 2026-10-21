using FfxRandomizer.Core.GameData;

namespace FfxRandomizer.Core.Randomization.Features;

/// <summary>
/// Changes Blitzball players' stat growth. Shuffle hands each player another player's whole set of curves. Randomize builds every
/// curve from the same stat of a random player, with its starting value and its growth each scaled by a random factor.
/// The tables are written into every event file that carries a copy (see <see cref="BlitzballStats"/>).
/// </summary>
public sealed class BlitzballStatsFeature : IRandomizerFeature
{
    public const string Section = "blitzball";
    public const int MinLevel = 1;
    public const int MaxLevel = 99;

    /// <summary>Tidus and Wakka, who stay as they are unless "blitzstars" is on.</summary>
    public static readonly IReadOnlyList<int> Stars = [0, 1];

    private static OptionGroup Group => OptionCatalog.AllGroups.First(g => g.Id == "blitz");

    public bool IsEnabled(RandomizerSettings settings) => settings.ChoiceOf(Group) != "orig";

    public void Apply(RandomizerContext context)
    {
        var original = BlitzballStats.Read(context.Original);
        var stats = original.Clone();
        var rng = context.RngFor("blitzball.stats");
        bool sane = context.Settings.IsOn("sane");
        var players = Enumerable.Range(0, BlitzballStats.PlayerCount)
            .Where(p => context.Settings.IsOn("blitzstars") || !Stars.Contains(p)).ToList();
        var donors = new Dictionary<int, int>();

        if (context.Settings.ChoiceOf(Group) == "shuffle")
        {
            var order = players.ToList();
            rng.Shuffle(order);
            for (int i = 0; i < players.Count; i++)
            {
                donors[players[i]] = order[i];
                foreach (var stat in Enum.GetValues<BlitzStat>()) stats[players[i], stat] = original[order[i], stat];
            }
        }
        else
        {
            foreach (var stat in Enum.GetValues<BlitzStat>())
            {
                var limits = Limits.For(original, stat, sane);
                var pool = Enumerable.Range(0, BlitzballStats.PlayerCount).Select(p => original[p, stat])
                    .Where(g => !g.IsUnused && (!sane || limits.Allows(g))).ToList();
                foreach (int player in players)
                {
                    if (original[player, stat].IsUnused) continue;
                    stats[player, stat] = Roll(rng, pool, limits, sane);
                }
            }
        }

        foreach (var copy in BlitzballStats.FindCopies(context.Original)) stats.WriteTo(context.Edit(copy.Path), copy);

        foreach (int player in players)
        {
            var entry = new Dictionary<string, object> { ["player"] = player, ["name"] = context.Names.BlitzballPlayer(player) };
            if (donors.TryGetValue(player, out int donor)) entry["statsOf"] = context.Names.BlitzballPlayer(donor);
            entry["stats"] = Enum.GetValues<BlitzStat>().ToDictionary(s => s.ToString(), s => Describe(stats[player, s]));
            context.Spoil(Section, entry);
        }
    }

    private static BlitzGrowth Roll(Rng rng, IReadOnlyList<BlitzGrowth> pool, Limits limits, bool sane)
    {
        (double low, double high) = sane ? (0.75, 1.25) : (0.4, 1.8);
        BlitzGrowth donor = rng.Pick(pool);
        for (int attempt = 0; attempt < 20; attempt++)
        {
            var curve = donor.Scaled(low + rng.NextDouble() * (high - low), low + rng.NextDouble() * (high - low));
            if (limits.Allows(curve)) return curve;
        }
        // The donor itself came from the pool, which only holds curves within the limits (with sane on).
        return donor;
    }

    /// <summary>What the features after this one make of an event file, given it as this one leaves it.</summary>
    private static byte[] LaterEdits(RandomizerContext context, string path, byte[] tabled)
    {
        byte[] result = tabled;
        string id = Path.GetFileNameWithoutExtension(path);
        if (new CutsceneSkipFeature().IsEnabled(context.Settings) && Cutscenes.ByEvent.FirstOrDefault(g => g.Key.Equals(id, StringComparison.OrdinalIgnoreCase)) is { } scenes)
            result = Cutscenes.Build(p => p.Equals(path, StringComparison.OrdinalIgnoreCase) ? tabled : context.Original.IndexOf(p) >= 0 ? context.Original.ReadFile(p) : null,
                [.. scenes], CutsceneSkipFeature.Mode(context.Settings), CutsceneSkipFeature.Button(context.Settings))[path];
        if (SoftResetFeature.On(context.Settings)) result = SoftReset.WithWatcher(path, result, SoftResetFeature.Buttons(context.Settings));
        return result;
    }

    private static object Describe(BlitzGrowth g) => new
    {
        formula = g.Formula(),
        lv1 = (int)Math.Floor(g.ValueAt(MinLevel)),
        lv99 = (int)Math.Floor(g.ValueAt(MaxLevel)),
    };

    /// <summary>
    /// The values a stat's curve may take over levels 1-99. With "sane" on: between 1 and the highest value any original player
    /// reaches, and never falling as the player levels up. Without it: between 1 and twice that highest value (255 at most for the
    /// stats other than HP), so the game still gets numbers of the size it expects.
    /// </summary>
    public sealed record Limits(double Max, bool MustRise)
    {
        public static Limits For(BlitzballStats original, BlitzStat stat, bool sane)
        {
            double max = Enumerable.Range(0, BlitzballStats.PlayerCount).Where(p => !original[p, stat].IsUnused)
                .Max(p => Levels.Max(l => original[p, stat].ValueAt(l)));
            return sane ? new Limits(max, true) : new Limits(stat == BlitzStat.HP ? max * 2 : Math.Min(max * 2, 255), false);
        }

        public bool Allows(BlitzGrowth curve)
        {
            if (curve.IsUnused) return true;
            if (!(float.IsFinite(curve.A) && float.IsFinite(curve.B) && float.IsFinite(curve.C))) return false;
            double previous = double.MinValue;
            foreach (int level in Levels)
            {
                double value = curve.ValueAt(level);
                if (!double.IsFinite(value) || value < 1 || value > Max) return false;
                if (MustRise && value < previous) return false;
                previous = value;
            }
            return true;
        }
    }

    private static IEnumerable<int> Levels => Enumerable.Range(MinLevel, MaxLevel - MinLevel + 1);

    /// <summary>
    /// An event file as this feature leaves it, rebuilt from what was finally written (features after it, like the
    /// cutscene skip, may have added code, which moves the tables along): the original with the written tables. The
    /// original when Blitzball is off or the event holds no tables.
    /// </summary>
    public static byte[] AsLeft(RandomizerContext context, string path)
    {
        byte[] original = context.Original.ReadFile(path);
        if (!new BlitzballStatsFeature().IsEnabled(context.Settings) || !context.ChangedFiles.TryGetValue(path, out byte[]? written)) return original;
        if (BlitzballStats.FindCopies(context.Original).FirstOrDefault(c => c.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) is not { } copy)
            return original;
        byte[] tabled = (byte[])original.Clone();
        BlitzballStats.FromCopy(written, copy.In(original, written)).WriteTo(tabled, copy);
        return tabled;
    }

    /// <summary>
    /// Reads a file as the features up to and including this one leave it (see <see cref="AsLeft"/>), for features after
    /// it to rebuild their own edits on; null for files the archive doesn't have.
    /// </summary>
    public static Func<string, byte[]?> ReadAsLeft(RandomizerContext context) => path =>
        context.Original.IndexOf(path) < 0 ? null
        : path.EndsWith(".ebp", StringComparison.OrdinalIgnoreCase) ? AsLeft(context, path)
        : context.Original.ReadFile(path);

    public IEnumerable<string> Validate(RandomizerContext context)
    {
        var original = BlitzballStats.Read(context.Original);
        var copies = BlitzballStats.FindCopies(context.Original);
        if (copies.Count == 0)
        {
            yield return "Blitzball: no event file holds the player stat tables.";
            yield break;
        }

        if (!context.ChangedFiles.TryGetValue(copies[0].Path, out byte[]? first))
        {
            yield return $"Blitzball: {copies[0].Path} wasn't updated.";
            yield break;
        }
        var written = BlitzballStats.FromCopy(first, copies[0].In(context.Original.ReadFile(copies[0].Path), first));
        bool sane = context.Settings.IsOn("sane");
        bool stars = context.Settings.IsOn("blitzstars");

        foreach (var copy in copies)
        {
            if (!context.ChangedFiles.TryGetValue(copy.Path, out byte[]? bytes))
            {
                yield return $"Blitzball: {copy.Path} wasn't updated.";
                continue;
            }
            byte[] before = context.Original.ReadFile(copy.Path);
            // Soft reset adds code to field events, which moves the tables (shared data) along; nothing else may change.
            // Later features may edit the event too (cutscene skips, soft reset), adding code that moves the tables along;
            // then the file must be exactly what they make of the original with these tables.
            bool grown = bytes.Length != before.Length;
            var mine = BlitzballStats.FromCopy(bytes, copy.In(before, bytes));
            foreach (var stat in Enum.GetValues<BlitzStat>())
            {
                if (!mine.Encode(stat).AsSpan().SequenceEqual(written.Encode(stat)))
                    yield return $"Blitzball: {copy.Path} has a different {stat} table than {copies[0].Path}.";
            }
            if (grown)
            {
                byte[] tabled = (byte[])before.Clone();
                mine.WriteTo(tabled, copy);
                if (!LaterEdits(context, copy.Path, tabled).AsSpan().SequenceEqual(bytes))
                    yield return $"Blitzball: {copy.Path} isn't its stat tables plus the later features' edits.";
            }
            else if (FirstChangeOutsideTables(before, bytes, copy) is int at)
                yield return $"Blitzball: {copy.Path} changed outside the stat tables at 0x{at:X}.";
        }

        foreach (var stat in Enum.GetValues<BlitzStat>())
        {
            var limits = Limits.For(original, stat, sane);
            var originals = Enumerable.Range(0, BlitzballStats.PlayerCount).Select(p => original[p, stat]).ToHashSet();
            for (int player = 0; player < BlitzballStats.PlayerCount; player++)
            {
                var curve = written[player, stat];
                if (!stars && Stars.Contains(player) && curve != original[player, stat])
                    yield return $"Blitzball: player {player}'s {stat} changed although Tidus and Wakka are left out.";
                if (!originals.Contains(curve) && !limits.Allows(curve))
                    yield return $"Blitzball: player {player}'s {stat} curve ({curve.Formula()}) is outside the allowed range.";
            }
        }

        foreach (var problem in SpoilerMatches(context, written)) yield return problem;
    }

    private static int? FirstChangeOutsideTables(byte[] before, byte[] after, BlitzballCopy copy)
    {
        var tables = copy.Offsets.Values.Order().ToList();
        int start = 0;
        foreach (int table in tables.Append(before.Length))
        {
            int length = table - start;
            if (length > 0)
            {
                int diff = before.AsSpan(start, length).CommonPrefixLength(after.AsSpan(start, length));
                if (diff < length) return start + diff;
            }
            start = table + BlitzballStats.TableBytes;
        }
        return null;
    }

    private static IEnumerable<string> SpoilerMatches(RandomizerContext context, BlitzballStats written)
    {
        if (context.Spoiler[Section] is not System.Text.Json.Nodes.JsonArray entries) yield break;
        foreach (var entry in entries)
        {
            int player = (int)entry!["player"]!;
            foreach (var stat in Enum.GetValues<BlitzStat>())
            {
                string? formula = (string?)entry["stats"]?[stat.ToString()]?["formula"];
                if (formula != written[player, stat].Formula())
                    yield return $"Blitzball: the spoiler log's {stat} for player {player} doesn't match the game files.";
            }
        }
    }
}
