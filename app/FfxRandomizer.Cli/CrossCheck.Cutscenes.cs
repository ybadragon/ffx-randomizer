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

            var changes = CutsceneSkipFeature.ChangedCode(original.ReadFile(path), files[path]);
            var lines = patchedOut.Replace("\r", "").Split('\n');
            bool Ours(string line) => CodeOffset(line) is int at && changes.Inserted.Any(r => at >= r.Offset && at < r.Offset + r.Length);
            // Instructions blanked out by NOPs: vanilla shows the original instruction, and the parser folds the NOPs into
            // the next instruction's line ("00 ... 00 AEF000 D80000"), so that line loses its NOP prefix for comparing.
            bool Blanked(string line) => CodeOffset(line) is int at && changes.Nopped.Any(n => at >= n.Original && at < n.Original + n.Length);
            string ours = string.Join("\n", lines.Where(Ours));
            foreach (var n in changes.Nopped)
                if (!lines.Any(l => CodeOffset(l) == n.Offset && NopRun().IsMatch(l)))
                    yield return $"{scene.EventId}: the blanked-out instruction at {n.Offset:X4} doesn't read back as NOPs";
            string Unblanked(string line) =>
                CodeOffset(line) is int at && changes.Nopped.Any(n => n.Offset == at) ? NopRun().Replace(line, "", 1) : line;
            // FFXR_CROSSCHECK_DUMP=<folder> saves both listings for reading.
            if (Environment.GetEnvironmentVariable("FFXR_CROSSCHECK_DUMP") is { Length: > 0 } dump)
            {
                Directory.CreateDirectory(dump);
                File.WriteAllText(Path.Combine(dump, $"{scene.EventId}.vanilla.txt"), vanillaOut);
                File.WriteAllText(Path.Combine(dump, $"{scene.EventId}.{mode}.txt"), patchedOut);
            }

            var vanilla = Normalize(vanillaOut.Replace("\r", "").Split('\n').Where(l => !Blanked(l))).ToList();
            var patched = Normalize(lines.Where(l => !Ours(l)).Select(Unblanked)).ToList();
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
            // The hold test must be the "button is down" call on the chosen button: during the intro, then once to start
            // and once per poll while held; never the press-only 0x0046.
            int expectedTests = Cutscenes.IntroFrames / Cutscenes.IntroPollFrames + 1 + Cutscenes.HoldFrames / Cutscenes.PollFrames;
            int downTests = DownTest().Matches(ours).Count(m => m.Groups[1].Value == $"{button.Index:X2}");
            if (downTests != expectedTests)
                yield return $"{scene.EventId}: {downTests} button-down tests on {button.Label} (button {button.Index:X2}h), expected {expectedTests}";
            if (ours.Contains("[0046h]", StringComparison.Ordinal)) yield return $"{scene.EventId}: the watcher uses the press-only 0x0046 test";
            // Shown for the intro, shown with an empty bar, then redrawn once per step; closed and waited for after the
            // intro, before the skip and on release.
            int shows = ours.Split('\n').Count(l => l.Contains($"?showMessage [006Ah]({window}", StringComparison.Ordinal));
            if (shows != Cutscenes.Steps + 2) yield return $"{scene.EventId}: the prompt is shown {shows} times, expected {Cutscenes.Steps + 2}";
            int waits = ours.Split('\n').Count(l => l.Contains($"waitForText [0084h]({window}, p2=1", StringComparison.Ordinal));
            if (waits != 3) yield return $"{scene.EventId}: {waits} waits for the prompt to close, expected 3 (intro, skip and release)";
            if (!IntroLine().IsMatch(ours)) yield return $"{scene.EventId}: no intro prompt \"Hold ... to skip\"";
            // The watcher starts with the scene: its first instruction is the start of the host's watch function. Code
            // inserted at the scene's entry lands at the entry, so only earlier inserts move it.
            int entry = changes.Inserted.Aggregate(scene.SceneStart.Offset, (at, r) => r.Offset < at ? at + r.Length : at);
            var first = lines.FirstOrDefault(l => CodeOffset(l) == entry);
            string start = $"run w{scene.BuiltIn?.Worker ?? scene.HostWorker:X2}::f";
            if (first is null || !first.Contains(start, StringComparison.Ordinal) || !first.Contains("at level 1;", StringComparison.Ordinal))
                yield return $"{scene.EventId}: the scene doesn't start the watcher at its first instruction (\"{first?.Trim()}\")";
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

    /// <summary>A run of NOPs as the parser prints it, after the offset and label columns.</summary>
    [GeneratedRegex(@"(?<=^[0-9A-F]{4}\s+(\S+:)?\s*)00 (\.\.\. 00 |00 )*")]
    private static partial Regex NopRun();

    [GeneratedRegex(@"string=""\{TIME:00\}Hold .*? to skip"" \[")]
    private static partial Regex IntroLine();
}
