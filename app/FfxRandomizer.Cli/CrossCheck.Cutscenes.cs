using System.Text.RegularExpressions;
using FfxRandomizer.Core.GameData;
using FfxRandomizer.Core.GameData.Events;
using FfxRandomizer.Core.Randomization;
using FfxRandomizer.Core.Randomization.Features;
using FfxRandomizer.Core.Vbf;

internal static partial class CrossCheck
{
    /// <summary>
    /// Disassembles each edited event with PARSE_EVENT and checks:
    /// <list type="bullet">
    /// <item>outside the inserted code, the listing is the vanilla listing once addresses and labels are ignored, so every
    /// original worker, function and jump reads back the same;</item>
    /// <item>the inserted code is what the mode should add: the first-visit guard jumping to the exit, and for hold-to-skip
    /// the watcher (held-button test, prompt in its message window, skip run at level 2) and the prompt-closing stop;</item>
    /// <item>the parser resolves the watcher's prompt strings to the "Hold ... to skip" text.</item>
    /// </list>
    /// </summary>
    private static IEnumerable<string> CompareCutscenes(VbfArchive original, RandomizerSettings settings, IReadOnlyDictionary<string, byte[]> files)
    {
        string mode = CutsceneSkipFeature.Mode(settings);
        var button = CutsceneSkipFeature.Button(settings);
        foreach (var scene in Cutscenes.All)
        {
            string path = Cutscenes.EventPath(scene.EventId);
            var (vanillaOut, vanillaErrors) = RunParser(new Dictionary<string, byte[]>(), "PARSE_EVENT", scene.EventId);
            var (patchedOut, patchedErrors) = RunParser(files, "PARSE_EVENT", scene.EventId);
            foreach (string e in vanillaErrors.Concat(patchedErrors)) yield return $"{scene.EventId}: parser error: {e}";

            var inserted = CutsceneSkipFeature.InsertedCode(original.ReadFile(path), files[path]);
            var lines = patchedOut.Replace("\r", "").Split('\n');
            bool Ours(string line) => CodeOffset(line) is int at && inserted.Any(r => at >= r.Offset && at < r.Offset + r.Length);
            string ours = string.Join("\n", lines.Where(Ours));
            // FFXR_CROSSCHECK_DUMP=<folder> saves both listings for reading.
            if (Environment.GetEnvironmentVariable("FFXR_CROSSCHECK_DUMP") is { Length: > 0 } dump)
            {
                Directory.CreateDirectory(dump);
                File.WriteAllText(Path.Combine(dump, $"{scene.EventId}.vanilla.txt"), vanillaOut);
                File.WriteAllText(Path.Combine(dump, $"{scene.EventId}.{mode}.txt"), patchedOut);
            }

            var vanilla = Normalize(vanillaOut.Replace("\r", "").Split('\n')).ToList();
            var patched = Normalize(lines.Where(l => !Ours(l))).ToList();
            var diff = vanilla.Except(patched).Concat(patched.Except(vanilla)).ToList();
            if (diff.Count > 0 || vanilla.Count != patched.Count)
                yield return $"{scene.EventId}: {diff.Count} line(s) outside the inserted code differ from vanilla (vanilla {vanilla.Count}, patched {patched.Count}), e.g. \"{diff.FirstOrDefault()}\"";

            string guard = $"(GameMoment < {scene.MomentBelow} [{scene.MomentBelow:X2}h]) -> j{scene.Exit.Jump:X2}";
            if (!ours.Contains(guard, StringComparison.Ordinal)) yield return $"{scene.EventId}: no \"{guard}\" in the inserted code";
            if (mode != Cutscenes.Hold) continue;

            string window = $"msgWindow={scene.MessageWindow} [{scene.MessageWindow:X2}h]";
            foreach (string expected in new[]
            {
                $"call Common.setMessageBoxTransparent [0066h]({window}, transparent=false",
                $"call Common.displayFieldString [0064h]({window}",
                $"call Common.?closeMessage [006Bh]({window})",
                $"run w{scene.SceneWorker:X2}::f{scene.BuiltIn?.SkipFunction ?? 0:X2} at level 2, await end",
                "Hold ",
            })
                if (!ours.Contains(expected, StringComparison.Ordinal)) yield return $"{scene.EventId}: no \"{expected}\" in the inserted code";
            int prompts = PromptLine().Matches(ours).Count;
            if (prompts != Cutscenes.Steps + 1) yield return $"{scene.EventId}: the parser shows {prompts} hold-to-skip prompts, expected {Cutscenes.Steps + 1}";
            // The hold test must be the "button is down" call on the chosen button, polled once plus once per poll step,
            // never the press-only 0x0046; the prompt is shown once.
            int downTests = DownTest().Matches(ours).Count(m => m.Groups[1].Value == $"{button.Index:X2}");
            if (downTests != 1 + Cutscenes.HoldFrames / Cutscenes.PollFrames)
                yield return $"{scene.EventId}: {downTests} button-down tests on {button.Label} (button {button.Index:X2}h), expected {1 + Cutscenes.HoldFrames / Cutscenes.PollFrames}";
            if (ours.Contains("[0046h]", StringComparison.Ordinal)) yield return $"{scene.EventId}: the watcher uses the press-only 0x0046 test";
            int shows = ours.Split('\n').Count(l => l.Contains($"?showMessage [006Ah]({window}", StringComparison.Ordinal));
            if (shows != Cutscenes.Steps + 1) yield return $"{scene.EventId}: the prompt is shown {shows} times, expected {Cutscenes.Steps + 1} (open, then one redraw per step)";
            int waits = ours.Split('\n').Count(l => l.Contains($"waitForText [0084h]({window}, p2=1", StringComparison.Ordinal));
            if (waits != 2) yield return $"{scene.EventId}: {waits} waits for the prompt to close, expected 2 (skip and release)";
        }

        static IEnumerable<string> Normalize(IEnumerable<string> listing) => listing
            .Select(l => EventLabel().Replace(l, "").Replace(" ", ""))
            .Select(l => JumpTarget().Replace(l, ""))
            .Select(l => PrivateAddress().Replace(l, "priv"))
            .Where(l => l.Length > 0);
    }

    private static int? CodeOffset(string line) =>
        line.Length >= 5 && line[4] == ' ' && int.TryParse(line[..4], System.Globalization.NumberStyles.HexNumber, null, out int at) ? at : null;

    /// <summary>A line's code offset and its label column (function and jump names, which move with inserted code).</summary>
    [GeneratedRegex(@"^[0-9A-F]{4}\s+(\S+:)?")]
    private static partial Regex EventLabel();

    [GeneratedRegex(@"\([0-9A-F]{4}\)")]
    private static partial Regex JumpTarget();

    /// <summary>
    /// The parser prints private-data pointers with the private data's script address, which moves when tables grow, and
    /// each worker's function and jump counts, which grow for the watcher's host (the feature's own checks cover those).
    /// </summary>
    [GeneratedRegex(@"&?priv[0-9A-F]{4,}|(?<=privateDataaddr=)[0-9A-F]+|(?<=^w[0-9A-F]{2}:\{.*)(Functions|Jumps)=\d+\[[0-9A-F]+h\]")]
    private static partial Regex PrivateAddress();

    [GeneratedRegex(@"controllerButtonPressed1\? \[004Ch\]\(controllerButton=[^\[]*\[([0-9A-F]{2})h\]\)")]
    private static partial Regex DownTest();

    [GeneratedRegex(@"string=""(\{TIME:00\})?Hold .*? to skip  ")]
    private static partial Regex PromptLine();
}
