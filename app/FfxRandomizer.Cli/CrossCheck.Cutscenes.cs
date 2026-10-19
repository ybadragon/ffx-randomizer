using System.Text.RegularExpressions;
using FfxRandomizer.Core.GameData;
using FfxRandomizer.Core.Randomization;
using FfxRandomizer.Core.Randomization.Features;

internal static partial class CrossCheck
{
    /// <summary>
    /// Disassembles each patched event with PARSE_EVENT and checks:
    /// <list type="bullet">
    /// <item>the scene function now starts with our check, jumping to the exit label at its new address;</item>
    /// <item>the original first instruction follows right after it;</item>
    /// <item>apart from that one line, the listing is the vanilla listing with addresses ignored, so every other worker,
    /// function and label reads back the same.</item>
    /// </list>
    /// </summary>
    private static IEnumerable<string> CompareCutscenes(RandomizerSettings settings, IReadOnlyDictionary<string, byte[]> files)
    {
        string mode = CutsceneSkipFeature.Mode(settings);
        foreach (var scene in Cutscenes.All)
        {
            var patch = CutsceneSkipFeature.PatchFor(scene, mode);
            var (vanillaOut, vanillaErrors) = RunParser(new Dictionary<string, byte[]>(), "PARSE_EVENT", patch.EventId);
            var (patchedOut, patchedErrors) = RunParser(files, "PARSE_EVENT", patch.EventId);
            foreach (string e in vanillaErrors.Concat(patchedErrors)) yield return $"{patch.EventId}: parser error: {e}";

            var lines = patchedOut.Replace("\r", "").Split('\n');
            string at = $"{patch.CodeOffset:X4} ";
            var check = lines.FirstOrDefault(l => l.StartsWith(at, StringComparison.Ordinal));
            var exit = patch.Jumps[0];
            string exitLabel = $"j{exit.Jump:X2} ({exit.CodeOffset + patch.Bytes.Length:X4})";
            if (check is null)
            {
                yield return $"{patch.EventId}: FFXDataParser shows no line at {patch.CodeOffset:X4}";
                continue;
            }
            if (!check.Contains("GameMoment < 2", StringComparison.Ordinal) || !check.Contains(exitLabel, StringComparison.Ordinal))
                yield return $"{patch.EventId}: expected \"GameMoment < 2 ... -> {exitLabel}\" at {patch.CodeOffset:X4} but FFXDataParser reads \"{check.Trim()}\"";
            if (mode == CutsceneSkipFeature.HoldSelect && !check.Contains("HeldButtonsBitfield [0046h] & [Select [08h]]", StringComparison.Ordinal))
                yield return $"{patch.EventId}: the Select check isn't there; FFXDataParser reads \"{check.Trim()}\"";
            string next = $"{patch.CodeOffset + patch.Bytes.Length:X4} ";
            if (!lines.Any(l => l.StartsWith(next, StringComparison.Ordinal) && l.Contains("Map.setGfxVisibility", StringComparison.Ordinal)))
                yield return $"{patch.EventId}: the scene's original first instruction isn't at {patch.CodeOffset + patch.Bytes.Length:X4}";
            if (!lines.Any(l => l.StartsWith($"{exit.CodeOffset + patch.Bytes.Length:X4} j{exit.Jump:X2}:", StringComparison.Ordinal)))
                yield return $"{patch.EventId}: label j{exit.Jump:X2} isn't at {exit.CodeOffset + patch.Bytes.Length:X4}";

            // Apart from our check line, the listing must match vanilla once addresses and function labels (which move
            // from the old first instruction onto the check) are ignored.
            var vanilla = Normalize(vanillaOut).ToList();
            var patched = Normalize(patchedOut).ToList();
            patched.Remove(Normalize(check).Single());
            var diff = vanilla.Except(patched).Concat(patched.Except(vanilla)).ToList();
            if (diff.Count > 0 || vanilla.Count != patched.Count)
                yield return $"{patch.EventId}: {diff.Count} other line(s) differ from vanilla (vanilla {vanilla.Count} lines, patched {patched.Count}), e.g. \"{diff.FirstOrDefault()}\"";
        }

        static IEnumerable<string> Normalize(string listing) => listing.Replace("\r", "").Split('\n')
            .Select(l => EventAddress().Replace(l, "").Replace(" ", ""))
            .Where(l => l.Length > 0);
    }

    /// <summary>A line's leading code offset, a function label like "w0D::f02:", and jump targets like "(1234)".</summary>
    [GeneratedRegex(@"^[0-9A-F]{4}\b|\bw[0-9A-F]{2}::\w+:|\([0-9A-F]{4}\)")]
    private static partial Regex EventAddress();
}
