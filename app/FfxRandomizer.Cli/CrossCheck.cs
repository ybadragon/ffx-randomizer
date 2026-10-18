using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FfxRandomizer.Core.GameData;
using FfxRandomizer.Core.Randomization;
using FfxRandomizer.Core.Randomization.Features;
using FfxRandomizer.Core.Vbf;

/// <summary>
/// Developer check: decodes our randomized monster files with Karifean's FFXDataParser (an independent Java parser)
/// and compares what it reads against our spoiler. Needs Java and a built FFXDataParser checkout.
/// </summary>
internal static partial class CrossCheck
{
    private static string ParserDir => Environment.GetEnvironmentVariable("FFXDATAPARSER_DIR") ?? @"C:\Github\Personal\FFXDataParser";

    private static string VanillaRoot => Environment.GetEnvironmentVariable("FFXR_VANILLA_ROOT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FFX Randomizer", "FFXRando", "input");

    /// <param name="sabotage">Corrupts one drop after randomizing, to prove the check notices a mismatch.</param>
    public static int Run(VbfArchive original, IGameNames names, RandomizerSettings settings, IReadOnlyList<Seed> seeds, bool sabotage = false)
    {
        if (!Directory.Exists(Path.Combine(ParserDir, "target", "classes")))
            throw new InvalidOperationException($"FFXDataParser isn't built at {ParserDir} (set FFXDATAPARSER_DIR).");
        if (!Directory.Exists(Path.Combine(VanillaRoot, "ffx_ps2")))
            throw new InvalidOperationException($"No extracted original game data at {VanillaRoot}\\ffx_ps2 (set FFXR_VANILLA_ROOT).");

        int failures = 0;
        foreach (var seed in seeds)
        {
            var result = RandomizerEngine.Run(original, settings, seed, names);
            var problems = new List<string>(result.Problems);
            var files = result.Files;
            if (sabotage)
            {
                var copy = files.ToDictionary(f => f.Key, f => (byte[])f.Value.Clone());
                if (copy.ContainsKey(MonsterFile.PathFor(1)))
                {
                    var raldo = new MonsterFile(1, copy[MonsterFile.PathFor(1)]).Loot;
                    raldo.SetDrop(0, raldo.Drop(0) with { Item = (ushort)(raldo.Drop(0).Item ^ 1) });
                }
                if (copy.ContainsKey(KernelFiles.Treasure)) copy[KernelFiles.Treasure][0x14 + 4 * 6 + 1] ^= 1;
                if (copy.ContainsKey(KernelFiles.ItemShops)) copy[KernelFiles.ItemShops][0x14 + 2] ^= 1;
                // Blitzball: one bit of player 30's Catch curve in the reference event (the parser's event matching only compares the
                // first 100 values, players 0-24, so only the curve check can see it), and of Datto's HP curve in Luca's copy only.
                foreach (var blitz in BlitzballStats.FindCopies(original))
                {
                    if (!copy.TryGetValue(blitz.Path, out byte[]? ebp)) continue;
                    if (blitz.Path.EndsWith("/bltz0002.ebp")) ebp[blitz.Offsets[BlitzStat.CA] + 30 * BlitzGrowth.Size] ^= 1;
                    if (blitz.Path.EndsWith("/luca0400.ebp")) ebp[blitz.Offsets[BlitzStat.HP] + 2 * BlitzGrowth.Size] ^= 1;
                }
                files = copy;
            }

            var checkedAreas = new List<string>();
            if (result.Spoiler[EnemyRewardsFeature.Section] is JsonArray enemies)
            {
                var (stdout, errors) = RunParser(files, "PARSE_ALL_MONSTERS");
                problems.AddRange(errors.Select(e => $"parser error: {e}"));
                var decoded = Parse(stdout);
                foreach (var node in enemies) problems.AddRange(Compare((JsonObject)node!, decoded));
                checkedAreas.Add($"{enemies.Count} enemies");
            }
            if (result.Spoiler[TreasureFeature.Section] is JsonArray treasure)
            {
                var (stdout, errors) = RunParser(files, "READ_TREASURES");
                problems.AddRange(errors.Select(e => $"parser error: {e}"));
                problems.AddRange(CompareTreasure(treasure, stdout));
                checkedAreas.Add($"{treasure.Count} treasures");
            }
            if (result.Spoiler[ItemShopsFeature.ShopSection] is JsonArray shops)
            {
                var (stdout, errors) = RunParser(files, "READ_ITEM_SHOPS");
                problems.AddRange(errors.Select(e => $"parser error: {e}"));
                problems.AddRange(CompareShops(shops, stdout));
                checkedAreas.Add($"{shops.Count} shops");
            }
            if (result.Spoiler[BlitzballStatsFeature.Section] is JsonArray blitzball)
            {
                var (stdout, errors) = RunParser(files, "READ_BLITZBALL_STATS");
                problems.AddRange(errors.Select(e => $"parser error: {e}"));
                problems.AddRange(CompareBlitzball(result.Files, blitzball, stdout));
                checkedAreas.Add($"{blitzball.Count} Blitzball players");
            }
            Console.WriteLine(problems.Count == 0
                ? $"seed {seed}: OK ({result.Files.Count} files; {string.Join(", ", checkedAreas)} match FFXDataParser)"
                : $"seed {seed}: {problems.Count} problem(s)");
            foreach (string p in problems.Take(20)) Console.WriteLine($"  {p}");
            if (problems.Count > 0) failures++;
        }
        return failures == 0 ? 0 : 1;
    }

    private sealed record Decoded(int Gil, int Ap, int ApOverkill, int PrimaryChance, int SecondaryChance, int StealChance,
        Dictionary<string, (int Id, int Quantity)> Items, Dictionary<string, Elements> ElementSets);

    /// <summary>"Index 4 [04h] (Offset 0024) - Gear: buki_get #2 [02h] { Yuna [01h], Weapon {...} }" and so on, one line per treasure.</summary>
    private static IEnumerable<string> CompareTreasure(JsonArray entries, string stdout)
    {
        var lines = TreasureLine().Matches(stdout.Replace("\r", "")).ToDictionary(m => int.Parse(m.Groups[1].Value), m => m.Groups[2].Value);
        foreach (var entry in entries)
        {
            int index = (int)entry!["index"]!;
            var now = entry["now"]!;
            string expected = now["gil"] is { } gil ? $"Gil: {(int)gil} "
                : now["keyItem"] is not null ? $"Key Item: {(string?)now["keyItem"]} [{(int)now["id"]!:X4}h]"
                : now["gear"] is { } gear ? $"Gear: buki_get #{(int)gear} [{(int)gear:X2}h] {{ {(string?)now["for"]} "
                : $"Item: {(int)now["quantity"]!}x {(string?)now["item"]} [{(int)now["id"]!:X4}h]";
            if (!lines.TryGetValue(index, out string? actual)) yield return $"treasure {index}: missing from FFXDataParser output";
            else if (!actual.StartsWith(expected, StringComparison.Ordinal))
                yield return $"treasure {index}: our spoiler says \"{expected.Trim()}\" but FFXDataParser reads \"{actual}\"";
            else if (now["type"] is { } type && !actual.Contains($"], {(string?)type}", StringComparison.Ordinal))
                yield return $"treasure {index}: our spoiler says {(string?)type} but FFXDataParser reads \"{actual}\"";
        }
    }

    /// <summary>"Index N [..h] (...) - Price (unused): 100%" followed by "Slot #k: Name" lines.</summary>
    private static IEnumerable<string> CompareShops(JsonArray entries, string stdout)
    {
        var shops = ShopBlock().Matches(stdout.Replace("\r", "")).ToDictionary(
            m => int.Parse(m.Groups[1].Value),
            m => Regex.Matches(m.Groups[2].Value, @"^Slot #\d+: (.*)$", RegexOptions.Multiline).Select(s => s.Groups[1].Value.Trim().TrimStart('[').TrimEnd(']')).ToList());
        foreach (var entry in entries)
        {
            int shop = (int)entry!["shop"]!;
            var ours = ((JsonArray)entry["items"]!).Select(n => (string)n!).ToList();
            if (!shops.TryGetValue(shop, out var theirs)) yield return $"shop {shop}: missing from FFXDataParser output";
            else if (!ours.SequenceEqual(theirs))
                yield return $"shop {shop}: our spoiler lists [{string.Join(", ", ours)}] but FFXDataParser reads [{string.Join(", ", theirs)}]";
        }
    }

    private static readonly Lazy<HashSet<string>> VanillaBlitzballEvents = new(() => FullBlitzballEvents(RunParser(new Dictionary<string, byte[]>(), "READ_BLITZBALL_STATS").Stdout));

    /// <summary>
    /// READ_BLITZBALL_STATS prints bltz0002's curve for each player ("- blitzballPlayer:N [..h] -" then "HP: 70.0 + Lv * 30.0 + Lv^2 * 0.711"
    /// and so on), then one "event X 1D=BlitzballStatGrowthTable_HP ..." line per event whose arrays start like bltz0002's.
    /// Checks the curves against our files and spoiler, and that every event listed in the original game still lists all eight
    /// tables (so every copy was written the same way).
    /// </summary>
    /// <param name="intended">The files the run produced (before any sabotage): what the parser should find.</param>
    private static IEnumerable<string> CompareBlitzball(IReadOnlyDictionary<string, byte[]> intended, JsonArray entries, string stdout)
    {
        stdout = stdout.Replace("\r", "");
        var theirs = new Dictionary<(int Player, BlitzStat Stat), Func<int, double>>();
        foreach (Match block in BlitzPlayerBlock().Matches(stdout))
        {
            int player = int.Parse(block.Groups[1].Value);
            foreach (Match line in Regex.Matches(block.Groups[2].Value, @"^(\w\w): (.*)$", RegexOptions.Multiline))
                theirs[(player, Enum.Parse<BlitzStat>(line.Groups[1].Value))] = ParseBlitzFormula(line.Groups[2].Value);
        }

        var ours = BlitzballStats.FromEvent(intended[BlitzballStats.EventPath(BlitzballStats.ReferenceEvent)]);
        int[] levels = [1, 2, 10, 50, 99];
        for (int player = 0; player < BlitzballStats.PlayerCount; player++)
        {
            foreach (var stat in Enum.GetValues<BlitzStat>())
            {
                if (!theirs.TryGetValue((player, stat), out var curve))
                {
                    yield return $"Blitzball player {player} {stat}: missing from FFXDataParser output";
                    continue;
                }
                // Both sides now evaluate the same float coefficients, so anything beyond rounding noise is a real difference.
                if (levels.Any(l => Math.Abs(curve(l) - ours[player, stat].ValueAt(l)) > 1e-9 * Math.Max(1, Math.Abs(curve(l)))))
                    yield return $"Blitzball player {player} {stat}: we wrote {ours[player, stat].Formula()} but FFXDataParser reads something else";
            }
        }
        foreach (var entry in entries)
        {
            int player = (int)entry!["player"]!;
            foreach (var stat in Enum.GetValues<BlitzStat>())
            {
                if (!theirs.TryGetValue((player, stat), out var curve)) continue;
                var shown = entry["stats"]![stat.ToString()]!;
                // The parser's printed coefficients are rounded, so a value right at a whole number may floor either way.
                static bool Same(int shown, double value) => shown == (int)Math.Floor(value + 1e-4) || shown == (int)Math.Floor(value - 1e-4);
                if (!Same((int)shown["lv1"]!, curve(1)) || !Same((int)shown["lv99"]!, curve(99)))
                    yield return $"Blitzball player {player} {stat}: spoiler says {(int)shown["lv1"]!}-{(int)shown["lv99"]!} but FFXDataParser gives {curve(1):0.##}-{curve(99):0.##}";
            }
        }

        var full = FullBlitzballEvents(stdout);
        foreach (string missing in VanillaBlitzballEvents.Value.Except(full).Order())
            yield return $"Blitzball: event {missing} no longer holds all eight tables matching bltz0002";
    }

    private static HashSet<string> FullBlitzballEvents(string stdout) =>
        Regex.Matches(stdout.Replace("\r", ""), @"^event (\w+) (.*)$", RegexOptions.Multiline)
            .Where(m => Regex.Matches(m.Groups[2].Value, "=BlitzballStatGrowthTable_").Count == 8)
            .Select(m => m.Groups[1].Value).ToHashSet();

    /// <summary>
    /// Turns the parser's printed curve back into a function of level: terms "a", "Lv", "Lv * b", "Lv^(e)", "Lv^(e) * b" and
    /// "Lv^2 * c" joined by " + ", optionally ending in " - Lv^2 * c"; "1" alone is an unused slot.
    /// </summary>
    private static Func<int, double> ParseBlitzFormula(string text)
    {
        // Java prints the shortest text that reads back as the same float, so parsing as float recovers the exact coefficient.
        double D(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        var terms = new List<Func<int, double>>();
        string[] minus = text.Split(" - ");
        foreach (string raw in minus[0].Split(" + ").Append(minus.Length > 1 ? "-" + minus[1] : null).OfType<string>())
        {
            bool negative = raw.StartsWith('-');
            string term = raw.TrimStart('-');
            double sign = negative ? -1 : 1;
            if (Regex.Match(term, @"^Lv\^2 \* (.+)$") is { Success: true } sq) { double c = D(sq.Groups[1].Value); terms.Add(l => sign * c * l * l); }
            else if (Regex.Match(term, @"^Lv\^\((.+?)\)(?: \* (.+))?$") is { Success: true } pw)
            {
                double e = D(pw.Groups[1].Value), b = pw.Groups[2].Success ? D(pw.Groups[2].Value) : 1;
                terms.Add(l => sign * b * Math.Pow(l, e));
            }
            else if (Regex.Match(term, @"^Lv(?: \* (.+))?$") is { Success: true } lin)
            {
                double b = lin.Groups[1].Success ? D(lin.Groups[1].Value) : 1;
                terms.Add(l => sign * b * l);
            }
            else { double a = D(term); terms.Add(_ => sign * a); }
        }
        return level => terms.Sum(t => t(level));
    }

    /// <summary>Writes the changed files as a sparse override over the original data and runs one parser mode.</summary>
    private static (string Stdout, List<string> Errors) RunParser(IReadOnlyDictionary<string, byte[]> files, string mode)
    {
        string root = Directory.CreateTempSubdirectory("ffxr-crosscheck-").FullName;
        string link = Path.Combine(root, "ffx_ps2");
        try
        {
            // A directory junction to the original data; the parser reads <root>/mods/ffx_ps2 files in preference.
            using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, Path.Combine(VanillaRoot, "ffx_ps2")])
                   { RedirectStandardOutput = true, UseShellExecute = false })!)
            {
                mklink.WaitForExit();
                if (mklink.ExitCode != 0) throw new IOException("Couldn't create the directory junction for the parser.");
            }
            foreach (var (path, bytes) in files)
            {
                string target = Path.Combine(root, "mods", path.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, bytes);
            }

            var start = new ProcessStartInfo("java", ["-Dstdout.encoding=UTF-8", "-Dstderr.encoding=UTF-8", "-cp", "target/classes", "main.Main", root.Replace('\\', '/') + "/", mode])
            {
                WorkingDirectory = ParserDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            using var process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            string stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            var errors = stderr.Result.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            return (stdout, errors);
        }
        finally
        {
            // Remove the junction itself first so the cleanup can never reach the original data.
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(root, recursive: true);
        }
    }

    private static Dictionary<int, Decoded> Parse(string output)
    {
        var monsters = new Dictionary<int, Decoded>();
        foreach (Match block in MonsterBlock().Matches(output.Replace("\r", "")))
        {
            int index = int.Parse(block.Groups[1].Value);
            string text = block.Groups[2].Value;
            int Int(Regex r, int group = 1) => r.Match(text) is { Success: true } m ? int.Parse(m.Groups[group].Value) : -1;

            var items = new Dictionary<string, (int, int)>();
            foreach (var (section, prefix) in new[] { ("Primary Item Drop", "primary"), ("Secondary Item Drop", "secondary") })
            {
                var lines = Regex.Match(text, $@"^{section}: Chance=\d+/255\n((?:[ ]+(?:COMMON|RARE) .*\n)*)", RegexOptions.Multiline).Groups[1].Value;
                foreach (Match line in DropLine().Matches(lines))
                {
                    string rare = line.Groups[1].Value == "RARE" ? "Rare" : "";
                    string cap = char.ToUpper(prefix[0]) + prefix[1..];
                    items[$"{prefix}{rare}"] = (Convert.ToInt32(line.Groups[3].Value, 16), int.Parse(line.Groups[2].Value));
                    items[$"overkill{cap}{rare}"] = (Convert.ToInt32(line.Groups[5].Value, 16), int.Parse(line.Groups[4].Value));
                }
            }
            var steal = Regex.Match(text, @"^Steal Item: Chance=\d+/255\n((?:[ ]+(?:COMMON|RARE) .*\n)*)", RegexOptions.Multiline).Groups[1].Value;
            foreach (Match line in StealLine().Matches(steal))
                items[line.Groups[1].Value == "RARE" ? "stealRare" : "steal"] = (Convert.ToInt32(line.Groups[3].Value, 16), int.Parse(line.Groups[2].Value));
            if (BribeLine().Match(text) is { Success: true } bribe)
                items["bribe"] = (Convert.ToInt32(bribe.Groups[2].Value, 16), int.Parse(bribe.Groups[1].Value));

            var elements = new Dictionary<string, Elements>();
            if (ElementLine().Match(text) is { Success: true } el)
                foreach (Match set in Regex.Matches(el.Groups[1].Value, @"(\w+)=\[([^\]]*)\]"))
                    elements[set.Groups[1].Value] = set.Groups[2].Value.Split(", ", StringSplitOptions.RemoveEmptyEntries)
                        .Select(n => n == "Thunder" ? Elements.Lightning : Enum.Parse<Elements>(n)).Aggregate(Elements.None, (a, b) => a | b);

            monsters[index] = new Decoded(Int(GilLine()), Int(ApLine()), Int(ApLine(), 2),
                Int(ChanceLine("Primary Item Drop")), Int(ChanceLine("Secondary Item Drop")), Int(ChanceLine("Steal Item")), items, elements);
        }
        return monsters;
    }

    private static IEnumerable<string> Compare(JsonObject entry, Dictionary<int, Decoded> decoded)
    {
        int index = (int)entry["index"]!;
        // FFXDataParser's PARSE_ALL_MONSTERS covers m001-m360; m000 is a placeholder it never reads.
        if (index == 0) yield break;
        string who = $"m{index:D3} {(string?)entry["monster"]}";
        if (!decoded.TryGetValue(index, out var d))
        {
            yield return $"{who}: not in FFXDataParser output";
            yield break;
        }
        IEnumerable<string> Check(string what, int ours, int theirs) =>
            ours == theirs ? [] : [$"{who}: {what} is {ours} in our spoiler but {theirs} according to FFXDataParser"];

        if (entry["drops"] is JsonObject drops)
        {
            foreach (string p in Check("gil", (int)entry["gil"]!["to"]!, d.Gil)) yield return p;
            foreach (string p in Check("AP", (int)entry["ap"]!["to"]!, d.Ap)) yield return p;
            foreach (string p in Check("overkill AP", (int)entry["apOverkill"]!["to"]!, d.ApOverkill)) yield return p;
            int primaryChance = (int)drops["chance"]!["primary"]!, secondaryChance = (int)drops["chance"]!["secondary"]!;
            foreach (string p in Check("primary drop chance", primaryChance, d.PrimaryChance)) yield return p;
            foreach (string p in Check("secondary drop chance", secondaryChance, d.SecondaryChance)) yield return p;
            foreach (var (slot, _, _) in MonsterLoot.DropSlots)
            {
                // The parser only lists drop items when that drop can happen.
                bool shown = slot.Contains("rimary") ? primaryChance > 0 : secondaryChance > 0;
                if (shown) foreach (string p in CheckItem(slot, drops[slot]!, d)) yield return p;
            }
        }
        if (entry["steal"] is JsonObject steal)
        {
            foreach (string p in Check("steal chance", 255, d.StealChance)) yield return p;
            foreach (string p in CheckItem("steal", steal["normal"]!, d)) yield return p;
            foreach (string p in CheckItem("stealRare", steal["rare"]!, d)) yield return p;
        }
        if (entry["bribe"] is { } bribe)
            foreach (string p in CheckItem("bribe", bribe, d)) yield return p;
        if (entry["elements"] is JsonObject elements)
        {
            foreach (var (ours, theirs) in new[] { ("weak", "Weak"), ("resist", "Resist"), ("immune", "Immune"), ("absorb", "Absorb") })
            {
                var mine = ((JsonArray)elements[ours]!).Select(n => Enum.Parse<Elements>((string)n!)).Aggregate(Elements.None, (a, b) => a | b);
                var parsed = d.ElementSets.GetValueOrDefault(theirs);
                if (mine != parsed) yield return $"{who}: {ours} is [{mine}] in our spoiler but [{parsed}] according to FFXDataParser";
            }
        }

        static IEnumerable<string> CheckItemCore(string who, string slot, JsonNode shown, Decoded d)
        {
            int id = (int)shown["id"]!, quantity = (int)shown["quantity"]!;
            if (!d.Items.TryGetValue(slot, out var theirs)) yield return $"{who}: {slot} missing from FFXDataParser output";
            else if (theirs.Id != id || (slot != "bribe" && theirs.Quantity != quantity))
                yield return $"{who}: {slot} is {quantity}x {id:X4}h in our spoiler but {theirs.Quantity}x {theirs.Id:X4}h according to FFXDataParser";
        }
        IEnumerable<string> CheckItem(string slot, JsonNode shown, Decoded dd) => CheckItemCore(who, slot, shown, dd);
    }

    [GeneratedRegex(@"^--- ffx_ps2/ffx/master/jppc/battle/mon/_m(\d{3})/m\d{3}\.bin ---\n(.*?)(?=^--- |\z)", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex MonsterBlock();
    [GeneratedRegex(@"^- blitzballPlayer:(\d+) \[[0-9A-F]+h\] -\n((?:\w\w: .*\n?)*)", RegexOptions.Multiline)] private static partial Regex BlitzPlayerBlock();
    [GeneratedRegex(@"^Gil=(\d+)", RegexOptions.Multiline)] private static partial Regex GilLine();
    [GeneratedRegex(@"^AP Normal=(\d+) Overkill=(\d+)", RegexOptions.Multiline)] private static partial Regex ApLine();
    [GeneratedRegex(@"[ ]+(COMMON|RARE) Normal (\d+)x .*?\[([0-9A-F]{4})h\]; Overkill (\d+)x .*?\[([0-9A-F]{4})h\]")] private static partial Regex DropLine();
    [GeneratedRegex(@"[ ]+(COMMON|RARE) (\d+)x .*?\[([0-9A-F]{4})h\]")] private static partial Regex StealLine();
    [GeneratedRegex(@"^Bribe Baseline: (\d+)x .*?\[([0-9A-F]{4})h\]", RegexOptions.Multiline)] private static partial Regex BribeLine();
    [GeneratedRegex(@"^Element Affinities: (.*)$", RegexOptions.Multiline)] private static partial Regex ElementLine();
    private static Regex ChanceLine(string section) => new($@"^{section}: Chance=(\d+)/255", RegexOptions.Multiline);
    [GeneratedRegex(@"^Index (\d+) \[[0-9A-F]+h\] \(Offset [0-9A-F]+\) - (.*)$", RegexOptions.Multiline)] private static partial Regex TreasureLine();
    [GeneratedRegex(@"^Index (\d+) \[[0-9A-F]+h\] \(Offset [0-9A-F]+\) - Price.*\n((?:Slot #.*\n?)*)", RegexOptions.Multiline)] private static partial Regex ShopBlock();
}
