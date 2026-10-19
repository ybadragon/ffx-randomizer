using FfxRandomizer.Core.GameData;
using FfxRandomizer.Core.Randomization;
using FfxRandomizer.Core.Randomization.Features;
using FfxRandomizer.Core.Vbf;

internal static partial class CrossCheck
{
    /// <summary>Events whose listings are compared: this many picked at random, plus every event with a skippable scene.</summary>
    private const int SoftResetSample = 30;

    /// <summary>
    /// Disassembles a sample of the events the soft reset edited with PARSE_EVENT and checks: outside the inserted code
    /// the listing is the vanilla listing; the inserted code holds the watcher as its worker's main, testing L1, R1 and
    /// Start with the "button is down" call once per poll, then fading to black and going to room 23 (test20, the title)
    /// at spawn 0.
    /// </summary>
    private static IEnumerable<string> CompareSoftReset(VbfArchive original, Seed seed, IReadOnlyDictionary<string, byte[]> files, List<string> checkedAreas, bool blitzball, System.Text.Json.Nodes.JsonNode? spoiler, int[] buttons)
    {
        // The events the feature says it covered (not the ones that look covered, so a broken watcher is still checked).
        var leftOut = (spoiler as System.Text.Json.Nodes.JsonArray)?.SelectMany(e => e?["leftOut"]?.AsArray() ?? []).Select(e => (string?)e?["event"]).ToHashSet() ?? [];
        var covered = SoftReset.FieldEvents(original).Where(id => !leftOut.Contains(id) && files.ContainsKey(Cutscenes.EventPath(id))).ToList();
        var random = covered.ToList();
        Rng.For(seed, "crosscheck.softreset").Shuffle(random);
        var sample = random.Take(SoftResetSample)
            .Concat(Cutscenes.ByEvent.Select(g => g.Key).Where(covered.Contains))
            .Distinct().Order(StringComparer.Ordinal).ToList();

        // Events given a worker of their own are compared with the original renumbered the same way (see Baseline).
        var baselines = sample.Select(id => (Path: Cutscenes.EventPath(id), Baseline: Baseline(original, id, files[Cutscenes.EventPath(id)])))
            .Where(b => b.Baseline.OwnWorker).ToDictionary(b => b.Path, b => b.Baseline.Bytes);
        var (vanillaAll, vanillaErrors) = RunParser(baselines, "PARSE_EVENT", sample);
        var (patchedAll, patchedErrors) = RunParser(files, "PARSE_EVENT", sample);
        foreach (string e in vanillaErrors.Concat(patchedErrors)) yield return $"soft reset: parser error: {e}";
        var vanilla = SplitListings(vanillaAll);
        var patched = SplitListings(patchedAll);

        int polls = SoftReset.HoldFrames / SoftReset.PollFrames + 1;
        foreach (string id in sample)
        {
            if (!vanilla.TryGetValue(id, out string? v) || !patched.TryGetValue(id, out string? p))
            {
                yield return $"{id}: the parser printed no listing";
                continue;
            }
            // Randomized Blitzball tables show up in the variables' values; the Blitzball comparison checks those.
            var (ours, problems, _, _) = CompareListing(original, files, id, "softreset", v, p,
                blitzball ? l => l.Contains("BlitzballStatGrowthTable", StringComparison.Ordinal) : null);
            foreach (string problem in problems) yield return problem;
            var lines = ours.Split('\n');
            if (!lines.Any(l => MainLabel().IsMatch(l) && l.Contains("call Common.wait [0000h](frames=2 [02h])", StringComparison.Ordinal)))
                yield return $"{id}: no worker's main starts with the watcher's 2-frame wait";
            foreach (int button in buttons)
            {
                int tests = DownTest().Matches(ours).Count(m => m.Groups[1].Value == $"{button:X2}");
                // Another feature's code (a cutscene skip on the same button) may test it too.
                if (tests < polls) yield return $"{id}: {tests} tests of button {button:X2}h, expected {polls}";
            }
            foreach (string expected in new[]
            {
                "call Common.setRoomFadeoutTime [00BBh](frames=15 [0Fh])",
                "call Common.setRoomFadeoutMode [00BCh](fadeMode=Black [00h])",
                "call Common.transitionToRoom? [0011h](room=test20 [17h], spawnpoint=0 [00h])",
            })
                if (!ours.Contains(expected, StringComparison.Ordinal)) yield return $"{id}: no \"{expected}\" in the inserted code";
        }
        checkedAreas.Add($"soft reset in {covered.Count} events ({sample.Count} disassembled)");
    }

    /// <summary>A multi-event PARSE_EVENT output split per event (each starts with "--- path/xxxx0000.ebp ---").</summary>
    private static Dictionary<string, string> SplitListings(string output)
    {
        var listings = new Dictionary<string, string>();
        string? current = null;
        var text = new System.Text.StringBuilder();
        foreach (string line in output.Replace("\r", "").Split('\n'))
        {
            if (line.StartsWith("--- ", StringComparison.Ordinal) && line.EndsWith(".ebp ---", StringComparison.Ordinal))
            {
                if (current is not null) listings[current] = text.ToString();
                current = Path.GetFileNameWithoutExtension(line[4..^4]);
                text.Clear();
            }
            text.Append(line).Append('\n');
        }
        if (current is not null) listings[current] = text.ToString();
        return listings;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[0-9A-F]{4,5}\s+(\S+,)?w[0-9A-F]{2}::main[,:]")]
    private static partial System.Text.RegularExpressions.Regex MainLabel();
}
