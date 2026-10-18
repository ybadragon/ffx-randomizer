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
                // shop_arms #3's power, the first gear shop's first slot, Sensor's price, Raldo's gear drop chance.
                if (copy.ContainsKey(GearFiles.ShopGear)) copy[GearFiles.ShopGear][0x14 + 3 * 22 + 0x09] ^= 1;
                if (copy.ContainsKey(GearFiles.Shops)) copy[GearFiles.Shops][0x14 + 2] ^= 1;
                if (copy.ContainsKey(GearFiles.AbilityPrices)) copy[GearFiles.AbilityPrices][0x14] ^= 1;
                if (copy.ContainsKey(MonsterFile.PathFor(1)) && result.Settings.IsOn("geardrops"))
                {
                    var raldo = new MonsterFile(1, copy[MonsterFile.PathFor(1)]).Loot;
                    raldo.SetWeaponAbility(0, 1, (ushort)(raldo.WeaponAbility(0, 1) == 0x8000 ? 0x8001 : 0x8000));
                }
                string usSave = GamePaths.LanguageKernel("new_uspc", PlySave.FileName), usRom = GamePaths.LanguageKernel("new_uspc", PlyRom.FileName);
                if (copy.ContainsKey(usSave)) copy[usSave][0x14 + PlySave.Wakka * 148 + PlySave.BaseMag] ^= 1;
                if (copy.ContainsKey(usRom)) copy[usRom][0x14 + (PlyRom.FirstAeon + 4) * 44 + PlyRom.Coefficients + 3] ^= 1;
                if (copy.ContainsKey(AeonFiles.SumGrow)) copy[AeonFiles.SumGrow][0x14 + 8 * 20 + 4] ^= 1;
                if (copy.TryGetValue(SphereGridFile.All[1].ContentsPath, out byte[]? standard)) standard[SphereGridFile.ContentsHeader] ^= 1;
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
            if (result.Spoiler[GearAbilitiesFeature.Section] is JsonArray gear)
            {
                foreach (var (table, path) in GearFiles.Tables)
                {
                    var entries = gear.OfType<JsonObject>().Where(e => (string?)e["table"] == table).ToList();
                    if (entries.Count == 0) continue;
                    var (stdout, errors) = RunParser(files, "READ_WEAPON_FILE", path);
                    problems.AddRange(errors.Select(e => $"parser error: {e}"));
                    problems.AddRange(CompareGear(table, entries, stdout));
                }
                checkedAreas.Add($"{gear.Count} gear");
            }
            if (result.Spoiler[GearShopsFeature.ShopSection] is JsonArray gearShops)
            {
                var (stdout, errors) = RunParser(files, "READ_GEAR_SHOPS");
                problems.AddRange(errors.Select(e => $"parser error: {e}"));
                problems.AddRange(CompareGearShops(gearShops, stdout));
                checkedAreas.Add($"{gearShops.Count} gear shops");
            }
            if (result.Spoiler[GearShopsFeature.PriceSection] is JsonArray prices)
            {
                var (stdout, errors) = RunParser(files, "READ_GEAR_ABILITIES");
                problems.AddRange(errors.Select(e => $"parser error: {e}"));
                problems.AddRange(CompareGearPrices(prices, stdout));
                checkedAreas.Add($"{prices.Count} ability prices");
            }
            var party = (result.Spoiler[PartyStatsFeature.Section] as JsonArray)?.Where(e => e!["stats"] is not null || e!["overdriveMode"] is not null).ToList() ?? [];
            var growth = (result.Spoiler[AeonStatsFeature.Section] as JsonArray)?.Where(e => e!["growth"] is not null).ToList() ?? [];
            bool poison = result.Spoiler["rules"] is JsonArray;
            if (party.Count > 0 || growth.Count > 0 || poison)
            {
                var (stdout, errors) = RunParser(files, "READ_PC_STATS");
                problems.AddRange(errors.Select(e => $"parser error: {e}"));
                problems.AddRange(CompareParty(party, growth, poison, stdout));
                checkedAreas.Add($"{party.Count} characters, {growth.Count} aeon growths{(poison ? ", poison" : "")}");
            }
            if (result.Spoiler[AeonItemsFeature.Section] is JsonArray aeonItems)
            {
                var (stdout, errors) = RunParser(files, "READ_CUSTOMIZATIONS");
                problems.AddRange(errors.Select(e => $"parser error: {e}"));
                problems.AddRange(CompareAeonItems(aeonItems, stdout));
                checkedAreas.Add($"{aeonItems.Count} aeon items");
            }
            if (result.Spoiler[SphereGridFeature.Section] is JsonArray grids)
            {
                problems.AddRange(CompareSphereGrids(grids, result.Files, files));
                checkedAreas.Add($"{grids.Count} sphere grids");
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
        Dictionary<string, (int Id, int Quantity)> Items, Dictionary<string, Elements> ElementSets, DecodedGear? Gear);

    /// <summary>Equipment drop as FFXDataParser prints it; Rolls is keyed "weapon:Tidus" and lists the forced ability then roll counts.</summary>
    private sealed record DecodedGear(int Chance, int Formula, int Power, int Crit, int SlotsByte, int RollsByte, Dictionary<string, string> Rolls);

    private const string CharacterLetters = "TYAKWLR";

    private static DecodedGear? ParseGear(string text)
    {
        var block = GearBlock().Match(text);
        if (!block.Success) return null;
        int chance = int.Parse(block.Groups[1].Value);
        var stats = GearStatsLine().Match(block.Groups[2].Value);
        if (!stats.Success) return new DecodedGear(chance, -1, -1, -1, -1, -1, []);
        var rolls = new Dictionary<string, string>();
        string kind = "";
        foreach (string line in block.Groups[2].Value.Split('\n').Skip(1))
        {
            if (line.StartsWith(" Weapon ", StringComparison.Ordinal)) kind = "weapon";
            else if (line.StartsWith(" Armor ", StringComparison.Ordinal)) kind = "armor";
            var who = GearRollLine().Match(line);
            if (!who.Success) continue;
            var abilities = GearRollItem().Matches(who.Groups[2].Value).Select(a => (Name: a.Groups[1].Value, Count: a.Groups[3].Value)).ToList();
            foreach (char letter in who.Groups[1].Value)
                rolls[$"{kind}:{KernelFiles.CharacterNames[CharacterLetters.IndexOf(letter)]}"] = DescribeRolls(
                    abilities.FirstOrDefault(a => a.Count == "Forced").Name,
                    abilities.Where(a => a.Count != "Forced").SelectMany(a => Enumerable.Repeat(a.Name, int.Parse(a.Count.Split('/')[0]))));
        }
        return new DecodedGear(chance, int.Parse(stats.Groups[1].Value), int.Parse(stats.Groups[2].Value), int.Parse(stats.Groups[3].Value),
            Convert.ToInt32(stats.Groups[4].Value, 16), Convert.ToInt32(stats.Groups[5].Value, 16), rolls);
    }

    private static string DescribeRolls(string? forced, IEnumerable<string> rolls) =>
        $"forced={forced ?? "-"}; " + string.Join(", ", rolls.Order(StringComparer.Ordinal));

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

    private static List<string> AbilityNames(string list) =>
        list.Split(", ", StringSplitOptions.RemoveEmptyEntries).Where(a => a != "Empty").Select(a => HexSuffix().Replace(a, "")).ToList();

    /// <summary>"Index N [..h] (Offset ..) - { Tidus [00h], Weapon [00h], Formula=... [01h], Power=16, Crit=3%, Slots=1 {Sensor [8000h]} ... }".</summary>
    private static IEnumerable<string> CompareGear(string table, List<JsonObject> entries, string stdout)
    {
        var lines = GearLine().Matches(stdout.Replace("\r", "")).ToDictionary(m => int.Parse(m.Groups[1].Value));
        foreach (var entry in entries)
        {
            int index = (int)entry["index"]!;
            string who = $"{table} #{index}";
            if (!lines.TryGetValue(index, out var m))
            {
                yield return $"{who}: missing from FFXDataParser output";
                continue;
            }
            string theirs = m.Value;
            if (m.Groups[2].Value != (string?)entry["for"] || m.Groups[3].Value != (string?)entry["type"])
                yield return $"{who}: our spoiler says {(string?)entry["for"]} {(string?)entry["type"]} but FFXDataParser reads \"{theirs}\"";
            if (entry["formula"] is { } formula && Convert.ToInt32(m.Groups[4].Value, 16) != (int)formula["to"]!["id"]!)
                yield return $"{who}: formula {(int)formula["to"]!["id"]!:X2}h in our spoiler but FFXDataParser reads \"{theirs}\"";
            if (entry["power"] is { } power && int.Parse(m.Groups[5].Value) != (int)power["to"]!)
                yield return $"{who}: power {(int)power["to"]!} in our spoiler but FFXDataParser reads \"{theirs}\"";
            if (entry["crit"] is { } crit && int.Parse(m.Groups[6].Value) != (int)crit["to"]!)
                yield return $"{who}: crit {(int)crit["to"]!} in our spoiler but FFXDataParser reads \"{theirs}\"";
            if (entry["slots"] is { } slots && int.Parse(m.Groups[7].Value) != (int)slots["to"]!)
                yield return $"{who}: {(int)slots["to"]!} slots in our spoiler but FFXDataParser reads \"{theirs}\"";
            if (entry["abilities"]?["to"] is JsonArray abilities
                && !abilities.Select(n => (string)n!).SequenceEqual(AbilityNames(m.Groups[8].Value)))
                yield return $"{who}: abilities [{string.Join(", ", abilities)}] in our spoiler but FFXDataParser reads \"{theirs}\"";
        }
    }

    /// <summary>"Index N ... - Price (unused): 100%" followed by "Slot #k: { gear } [IIh]" lines, II being the shop_arms.bin index.</summary>
    private static IEnumerable<string> CompareGearShops(JsonArray entries, string stdout)
    {
        var shops = ShopBlock().Matches(stdout.Replace("\r", "")).ToDictionary(
            m => int.Parse(m.Groups[1].Value),
            m => GearShopSlot().Matches(m.Groups[2].Value).Select(s => (Index: Convert.ToInt32(s.Groups[3].Value, 16), Abilities: AbilityNames(s.Groups[2].Value), Gear: s.Groups[1].Value)).ToList());
        foreach (var entry in entries)
        {
            int shop = (int)entry!["shop"]!;
            var ours = ((JsonArray)entry["gear"]!).OfType<JsonObject>().ToList();
            if (!shops.TryGetValue(shop, out var theirs))
            {
                yield return $"gear shop {shop}: missing from FFXDataParser output";
                continue;
            }
            if (!ours.Select(g => (int)g["index"]!).SequenceEqual(theirs.Select(t => t.Index)))
                yield return $"gear shop {shop}: our spoiler sells [{string.Join(", ", ours.Select(g => (int)g["index"]!))}] but FFXDataParser reads [{string.Join(", ", theirs.Select(t => t.Index))}]";
            else
                for (int i = 0; i < ours.Count; i++)
                    if (!((JsonArray)ours[i]["abilities"]!).Select(n => (string)n!).SequenceEqual(theirs[i].Abilities) || !theirs[i].Gear.StartsWith($"{{ {(string?)ours[i]["for"]} [", StringComparison.Ordinal))
                        yield return $"gear shop {shop} slot {i}: our spoiler says {ours[i].ToJsonString()} but FFXDataParser reads \"{theirs[i].Gear}\"";
        }
    }

    /// <summary>"8000 (Offset 0014) - Sensor { ..., Price=200 gil } ...", one line per auto-ability.</summary>
    private static IEnumerable<string> CompareGearPrices(JsonArray entries, string stdout)
    {
        var prices = GearPriceLine().Matches(stdout.Replace("\r", "")).ToDictionary(m => Convert.ToInt32(m.Groups[1].Value, 16) - 0x8000, m => int.Parse(m.Groups[2].Value));
        for (int i = 0; i < entries.Count; i++)
        {
            int ours = (int)entries[i]!["to"]!;
            if (!prices.TryGetValue(i, out int theirs)) yield return $"{(string?)entries[i]!["ability"]}: price missing from FFXDataParser output";
            else if (ours != theirs) yield return $"{(string?)entries[i]!["ability"]}: price {ours} in our spoiler but {theirs} according to FFXDataParser";
        }
    }

    /// <summary>The "Index N ..." blocks of one file's section in a parser listing, by index.</summary>
    private static Dictionary<int, string> FileBlocks(string stdout, string fileName)
    {
        var section = Regex.Match(stdout.Replace("\r", ""), $@"^--- [^\n]*/{Regex.Escape(fileName)} ---\n(.*?)(?=^--- ffx_ps2/|\z)", RegexOptions.Multiline | RegexOptions.Singleline);
        return IndexBlock().Matches(section.Groups[1].Value).ToDictionary(m => int.Parse(m.Groups[1].Value), m => m.Groups[2].Value);
    }

    /// <summary>READ_PC_STATS: the US ply_save.bin (base stats, Overdrive mode, poison) and ply_rom.bin (aeon growth) listings.</summary>
    private static IEnumerable<string> CompareParty(List<JsonNode?> party, List<JsonNode?> growth, bool poison, string stdout)
    {
        var saves = FileBlocks(stdout, PlySave.FileName);
        foreach (var entry in party)
        {
            string name = (string)entry!["character"]!;
            int index = Array.IndexOf(KernelFiles.CharacterNames, name);
            if (!saves.TryGetValue(index, out string? block))
            {
                yield return $"{name}: missing from FFXDataParser's ply_save.bin listing";
                continue;
            }
            if (entry["stats"] is JsonObject stats)
            {
                var parsed = Regex.Matches(block[..block.IndexOf("--- Current ---", StringComparison.Ordinal)], @"(\w+)=(\d+)")
                    .ToDictionary(m => m.Groups[1].Value, m => int.Parse(m.Groups[2].Value));
                foreach (var (ours, theirs) in new[] { ("hp", "HP"), ("mp", "MP"), ("strength", "STR"), ("defense", "DEF"), ("magic", "MAG"),
                    ("magicDefense", "MDF"), ("agility", "AGI"), ("luck", "LCK"), ("accuracy", "ACC") })
                {
                    int expected = (int)stats[ours]!["to"]!;
                    if (parsed.GetValueOrDefault(theirs, -1) != expected)
                        yield return $"{name}: base {theirs} is {expected} in our spoiler but {parsed.GetValueOrDefault(theirs, -1)} according to FFXDataParser";
                }
            }
            if (entry["overdriveMode"] is { } od)
            {
                int mode = Array.IndexOf(AeonFiles.OverdriveModes, (string)od["to"]!);
                var shown = Regex.Match(block, @"^Overdrive Mode=.*\[([0-9A-F]{2})h\]$", RegexOptions.Multiline);
                var learned = Regex.Match(block, @"^Already learned: .*\[([0-9A-F]+)h\]$", RegexOptions.Multiline);
                if (!shown.Success || Convert.ToInt32(shown.Groups[1].Value, 16) != mode)
                    yield return $"{name}: Overdrive mode is {od["to"]} in our spoiler but FFXDataParser reads \"{shown.Value}\"";
                if (!learned.Success || (Convert.ToInt64(learned.Groups[1].Value, 16) & 1L << mode) == 0)
                    yield return $"{name}: FFXDataParser doesn't list {od["to"]} as learned (\"{learned.Value}\")";
            }
        }
        if (poison)
            foreach (var (index, block) in saves)
                if (!block.Contains($"Poison Damage={DeadlyPoisonFeature.Percent}%", StringComparison.Ordinal))
                    yield return $"ply_save record {index}: FFXDataParser doesn't read {DeadlyPoisonFeature.Percent}% poison damage";

        var roms = FileBlocks(stdout, PlyRom.FileName);
        string[] labels = ["HP", "MP", "STR", "DEF", "MAG", "MDF", "AGI", "EVA", "ACC"];
        foreach (var entry in growth)
        {
            string name = (string)entry!["aeon"]!;
            int index = PlyRom.FirstAeon + Array.IndexOf(AeonFiles.Names, name);
            if (!roms.TryGetValue(index, out string? block))
            {
                yield return $"{name}: missing from FFXDataParser's ply_rom.bin listing";
                continue;
            }
            var lines = GrowthLine().Matches(block).ToDictionary(m => m.Groups[1].Value, m => m);
            for (int s = 0; s < labels.Length; s++)
            {
                var to = (JsonArray)entry["growth"]![PlyRom.StatNames[s]]!["to"]!;
                int a = (int)to[0]!, b = (int)to[1]!;
                if (!lines.TryGetValue(labels[s], out var line))
                {
                    yield return $"{name}: no {labels[s]} growth line from FFXDataParser";
                    continue;
                }
                int theirA = int.Parse(line.Groups[2].Value);
                int theirB = (int)Math.Round(double.Parse(line.Groups[3].Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture) * (s < 2 ? 100 : 10));
                if (theirA != a || theirB != b)
                    yield return $"{name}: {labels[s]} growth is /{a}, *{b} in our spoiler but /{theirA}, *{theirB} according to FFXDataParser";
            }
        }
    }

    /// <summary>READ_CUSTOMIZATIONS, sum_grow.bin section: "Index N ... - Aeon - Dark Attack [300Ah]: 6x Smoke Bomb [2028h]".</summary>
    private static IEnumerable<string> CompareAeonItems(JsonArray entries, string stdout)
    {
        var lines = FileBlocks(stdout, "sum_grow.bin").ToDictionary(b => b.Key, b => b.Value.Trim());
        foreach (var entry in entries)
        {
            int index = (int)entry!["index"]!;
            string teaches = (string)entry["teaches"]!;
            int id = (int)entry["now"]!["id"]!, quantity = (int)entry["now"]!["quantity"]!;
            if (!lines.TryGetValue(index, out string? line))
            {
                yield return $"aeon item {index}: missing from FFXDataParser output";
                continue;
            }
            var m = AeonItemLine().Match(line);
            bool stat = teaches.Contains(" +", StringComparison.Ordinal);
            bool sameWhat = m.Success && (stat ? m.Groups[1].Value.EndsWith(teaches[teaches.IndexOf(" +", StringComparison.Ordinal)..], StringComparison.Ordinal)
                : m.Groups[1].Value.StartsWith(teaches + " [", StringComparison.Ordinal));
            if (!sameWhat || int.Parse(m.Groups[2].Value) != quantity || Convert.ToInt32(m.Groups[3].Value, 16) != id)
                yield return $"aeon item {index}: our spoiler says {teaches}: {quantity}x {(string?)entry["now"]!["item"]} [{id:X4}h] but FFXDataParser reads \"{line}\"";
        }
    }

    /// <summary>Writes the changed files as a sparse override over the original data and runs one parser mode.</summary>
    private static (string Stdout, List<string> Errors) RunParser(IReadOnlyDictionary<string, byte[]> files, string mode, params string[] arguments)
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

            var start = new ProcessStartInfo("java", ["-Dstdout.encoding=UTF-8", "-Dstderr.encoding=UTF-8", "-cp", "target/classes", "main.Main", root.Replace('\\', '/') + "/", mode, .. arguments])
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
                Int(ChanceLine("Primary Item Drop")), Int(ChanceLine("Secondary Item Drop")), Int(ChanceLine("Steal Item")), items, elements, ParseGear(text));
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
        if (entry["gear"] is JsonObject gear)
        {
            int chance = (int)gear["chance"]!["to"]!;
            if (d.Gear is not { } g) yield return $"{who}: gear drop missing from FFXDataParser output";
            else
            {
                foreach (string p in Check("gear drop chance", chance, g.Chance)) yield return p;
                // The parser only prints the gear's details when it can drop, and its abilities when there are ability rolls.
                if (chance > 0)
                {
                    foreach (string p in Check("gear formula", (int)gear["formula"]!["id"]!, g.Formula)) yield return p;
                    foreach (string p in Check("gear power", (int)gear["power"]!, g.Power)) yield return p;
                    foreach (string p in Check("gear crit", (int)gear["crit"]!, g.Crit)) yield return p;
                    foreach (string p in Check("gear slot byte", (int)gear["slotsByte"]!, g.SlotsByte)) yield return p;
                    foreach (string p in Check("gear ability roll byte", (int)gear["abilityRollsByte"]!, g.RollsByte)) yield return p;
                    if ((int)gear["abilityRollsByte"]! > 0)
                        foreach (string kind in new[] { "weapon", "armor" })
                            foreach (string character in KernelFiles.CharacterNames)
                            {
                                var mine = gear[kind]![character]!;
                                string ours = DescribeRolls((string?)mine["forced"], ((JsonArray)mine["rolls"]!).Select(n => (string)n!));
                                string theirs = g.Rolls.GetValueOrDefault($"{kind}:{character}", "(missing)");
                                if (ours != theirs) yield return $"{who}: {character}'s {kind} abilities are \"{ours}\" in our spoiler but \"{theirs}\" according to FFXDataParser";
                            }
                }
            }
        }
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
    [GeneratedRegex(@"^Gil=(\d+)", RegexOptions.Multiline)] private static partial Regex GilLine();
    [GeneratedRegex(@"^AP Normal=(\d+) Overkill=(\d+)", RegexOptions.Multiline)] private static partial Regex ApLine();
    [GeneratedRegex(@"[ ]+(COMMON|RARE) Normal (\d+)x .*?\[([0-9A-F]{4})h\]; Overkill (\d+)x .*?\[([0-9A-F]{4})h\]")] private static partial Regex DropLine();
    [GeneratedRegex(@"[ ]+(COMMON|RARE) (\d+)x .*?\[([0-9A-F]{4})h\]")] private static partial Regex StealLine();
    [GeneratedRegex(@"^Bribe Baseline: (\d+)x .*?\[([0-9A-F]{4})h\]", RegexOptions.Multiline)] private static partial Regex BribeLine();
    [GeneratedRegex(@"^Element Affinities: (.*)$", RegexOptions.Multiline)] private static partial Regex ElementLine();
    private static Regex ChanceLine(string section) => new($@"^{section}: Chance=(\d+)/255", RegexOptions.Multiline);
    [GeneratedRegex(@"^Index (\d+) \[[0-9A-F]+h\] \(Offset [0-9A-F]+\) - (.*)$", RegexOptions.Multiline)] private static partial Regex TreasureLine();
    [GeneratedRegex(@"^Index (\d+) \[[0-9A-F]+h\] \(Offset [0-9A-F]+\) - Price.*\n((?:Slot #.*\n?)*)", RegexOptions.Multiline)] private static partial Regex ShopBlock();
    [GeneratedRegex(@" \[[0-9A-F]{4}h\]$")] private static partial Regex HexSuffix();
    [GeneratedRegex(@"^Gear Drop: Chance=(\d+)/255\n((?:[ ].*\n?)*)", RegexOptions.Multiline)] private static partial Regex GearBlock();
    [GeneratedRegex(@"^ Formula=(\d+), Power=(\d+), Crit=(\d+)%, Slots=.*?\[([0-9A-F]{2})h\], Ability Rolls=.*?\[([0-9A-F]{2})h\]")] private static partial Regex GearStatsLine();
    [GeneratedRegex(@"([TYAKWLR]+): (.*)$")] private static partial Regex GearRollLine();
    [GeneratedRegex(@"(.+?) \[([0-9A-F]{4})h\] \((Forced|\d+/7)\)(?:, |$)")] private static partial Regex GearRollItem();
    [GeneratedRegex(@"^Index (\d+) \[[0-9A-F]+h\] \(Offset [0-9A-F]+\) - \{ (.+?) \[[0-9A-F]+h\], (Weapon|Armor) \[[0-9A-F]+h\], Formula=.*? \[([0-9A-F]{2})h\], Power=(\d+), Crit=(\d+)%, Slots=(\d+) \{([^}]*)\}.*$", RegexOptions.Multiline)]
    private static partial Regex GearLine();
    [GeneratedRegex(@"^Slot #\d+: (\{ .*?Slots=\d+ \{([^}]*)\}.*\}) \[([0-9A-F]+)h\]$", RegexOptions.Multiline)] private static partial Regex GearShopSlot();
    [GeneratedRegex(@"^([0-9A-F]{4}) \(Offset [0-9A-F]+\) - .*?Price=(\d+) gil", RegexOptions.Multiline)] private static partial Regex GearPriceLine();
    [GeneratedRegex(@"^Index (\d+) \[[0-9A-F]+h\] \(Offset [0-9A-F]+\) - (.*?)(?=^Index |\z)", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex IndexBlock();
    [GeneratedRegex(@"^(HP|MP|STR|DEF|MAG|MDF|AGI|EVA|ACC):\s+\(Yuna's Total Stats /\s*(\d+)\) \+ Yuna's \w+\s+\* ([\d.,]+)", RegexOptions.Multiline)]
    private static partial Regex GrowthLine();
    [GeneratedRegex(@"^Aeon - (.*): (\d+)x .*\[([0-9A-F]{4})h\]$")] private static partial Regex AeonItemLine();
}
