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
        foreach (var group in Cutscenes.ByEvent)
        {
            string eventId = group.Key;
            string path = Cutscenes.EventPath(eventId);
            var (vanillaOut, vanillaErrors) = RunParser(new Dictionary<string, byte[]>(), "PARSE_EVENT", eventId);
            var (patchedOut, patchedErrors) = RunParser(files, "PARSE_EVENT", eventId);
            foreach (string e in vanillaErrors.Concat(patchedErrors)) yield return $"{eventId}: parser error: {e}";

            var changes = CutsceneSkipFeature.ChangedCode(original.ReadFile(path), files[path]);
            var lines = patchedOut.Replace("\r", "").Split('\n');
            bool Ours(string line) => CodeOffset(line) is int at && changes.Inserted.Any(r => at >= r.Offset && at < r.Offset + r.Length);
            // Instructions blanked out by NOPs: vanilla shows the original instruction, and the parser folds the NOPs into
            // the next instruction's line ("00 ... 00 AEF000 D80000"), so that line loses its NOP prefix for comparing.
            bool Blanked(string line) => CodeOffset(line) is int at && changes.Nopped.Any(n => at >= n.Original && at < n.Original + n.Length);
            string ours = string.Join("\n", lines.Where(Ours));
            foreach (var n in changes.Nopped)
                if (!lines.Any(l => CodeOffset(l) == n.Offset && NopRun().IsMatch(l)))
                    yield return $"{eventId}: the blanked-out instruction at {n.Offset:X4} doesn't read back as NOPs";
            string Unblanked(string line) =>
                CodeOffset(line) is int at && changes.Nopped.Any(n => n.Offset == at) ? NopRun().Replace(line, "", 1) : line;
            // FFXR_CROSSCHECK_DUMP=<folder> saves both listings for reading.
            if (Environment.GetEnvironmentVariable("FFXR_CROSSCHECK_DUMP") is { Length: > 0 } dump)
            {
                Directory.CreateDirectory(dump);
                File.WriteAllText(Path.Combine(dump, $"{eventId}.vanilla.txt"), vanillaOut);
                File.WriteAllText(Path.Combine(dump, $"{eventId}.{mode}.txt"), patchedOut);
            }

            var vanilla = Normalize(vanillaOut.Replace("\r", "").Split('\n').Where(l => !Blanked(l))).ToList();
            var patched = Normalize(lines.Where(l => !Ours(l)).Select(Unblanked)).ToList();
            var diff = vanilla.Except(patched).Concat(patched.Except(vanilla)).ToList();
            if (diff.Count > 0 || vanilla.Count != patched.Count)
                yield return $"{eventId}: {diff.Count} line(s) outside the inserted code differ from vanilla (vanilla {vanilla.Count}, patched {patched.Count}), e.g. \"{diff.FirstOrDefault()}\"";

            // Each scene's first-visit guard (both bounds when it has a lower one), wherever the mode puts it.
            foreach (var scene in group)
                foreach (string bound in scene.MomentFrom > 0
                    ? new[] { $"GameMoment >= {scene.MomentFrom} [{scene.MomentFrom:X2}h]", $"GameMoment < {scene.MomentBelow} [{scene.MomentBelow:X2}h]" }
                    : [$"GameMoment < {scene.MomentBelow} [{scene.MomentBelow:X2}h]"])
                    if (!ours.Contains(bound, StringComparison.Ordinal)) yield return $"{eventId} {scene.Name}: no \"{bound}\" in the inserted code";
            if (mode != Cutscenes.Hold) continue;

            // A watcher (built-in or added) shows the intro and the bar, tests the button through the intro and while
            // held, and runs the skip above the scene; a start check (scenes that stay in the room) only shows the bar
            // while the button is held as the scene starts.
            var watched = group.Where(s => !s.StaysInRoom).ToList();
            var started = group.Where(s => s.StaysInRoom).ToList();
            string window = $"msgWindow={group.First().MessageWindow} [{group.First().MessageWindow:X2}h]";
            var expectedLines = new List<string> { $"call Common.setMessageBoxTransparent [0066h]({window}, transparent=false", $"call Common.displayFieldString [0064h]({window}", $"call Common.?closeMessage [006Bh]({window})", "Hold " };
            foreach (var scene in watched)
                expectedLines.Add(scene.BuiltIn is { } b
                    ? $"run w{scene.SceneWorker:X2}::f{b.SkipFunction:X2} at level 2, await end"
                    : $"run w{scene.Exit.Worker:X2}::f");
            foreach (string expected in expectedLines)
                if (!ours.Contains(expected, StringComparison.Ordinal)) yield return $"{eventId}: no \"{expected}\" in the inserted code";
            foreach (var scene in watched.Where(s => s.BuiltIn is null))
                if (!Regex.IsMatch(ours, $@"run w{scene.Exit.Worker:X2}::f[0-9A-F]{{2}} at level {scene.SkipLevel}, await end"))
                    yield return $"{eventId} {scene.Name}: no skip run on w{scene.Exit.Worker:X2} at level {scene.SkipLevel}";

            int polls = Cutscenes.HoldFrames / Cutscenes.PollFrames;
            int prompts = PromptLine().Matches(ours).Count;
            if (prompts != group.Count() * (Cutscenes.Steps + 1)) yield return $"{eventId}: the parser shows {prompts} hold-to-skip prompts, expected {group.Count() * (Cutscenes.Steps + 1)}";
            // The hold test must be the "button is down" call on the chosen button, never the press-only 0x0046.
            int expectedTests = watched.Count * (Cutscenes.IntroFrames / Cutscenes.IntroPollFrames + 1 + polls) + started.Count * (1 + polls);
            int downTests = DownTest().Matches(ours).Count(m => m.Groups[1].Value == $"{button.Index:X2}");
            if (downTests != expectedTests)
                yield return $"{eventId}: {downTests} button-down tests on {button.Label} (button {button.Index:X2}h), expected {expectedTests}";
            if (ours.Contains("[0046h]", StringComparison.Ordinal)) yield return $"{eventId}: the skip uses the press-only 0x0046 test";
            // A watcher shows the intro, the empty bar and one redraw per step, and closes the prompt after the intro,
            // before the skip and on release; a start check shows the bar and closes it on skip or release.
            int shows = ours.Split('\n').Count(l => l.Contains($"?showMessage [006Ah]({window}", StringComparison.Ordinal));
            int expectedShows = watched.Count * (Cutscenes.Steps + 2) + started.Count * (Cutscenes.Steps + 1);
            if (shows != expectedShows) yield return $"{eventId}: the prompt is shown {shows} times, expected {expectedShows}";
            int waits = ours.Split('\n').Count(l => l.Contains($"waitForText [0084h]({window}, p2=1", StringComparison.Ordinal));
            if (waits != watched.Count * 3 + started.Count * 2) yield return $"{eventId}: {waits} waits for the prompt to close, expected {watched.Count * 3 + started.Count * 2}";
            if (watched.Count > 0 && !IntroLine().IsMatch(ours)) yield return $"{eventId}: no intro prompt \"Hold ... to skip\"";

            // The inserted code starts each scene's watcher (on its host, at level 1).
            foreach (var scene in watched)
            {
                string start = $"run w{scene.BuiltIn?.Worker ?? scene.HostWorker:X2}::f";
                if (!ours.Split('\n').Any(l => l.Contains(start, StringComparison.Ordinal) && l.Contains("at level 1;", StringComparison.Ordinal)))
                    yield return $"{eventId} {scene.Name}: nothing starts the watcher on w{scene.BuiltIn?.Worker ?? scene.HostWorker:X2}";
            }
        }

        static IEnumerable<string> Normalize(IEnumerable<string> listing) => listing
            .Select(l => EventLabel().Replace(l, "").Replace(" ", ""))
            .Select(l => JumpTarget().Replace(l, ""))
            .Select(l => PrivateAddress().Replace(l, "priv"))
            .Where(l => l.Length > 0);
    }

    private static int? CodeOffset(string line) =>
        line.IndexOf(' ') is int space and (4 or 5) && int.TryParse(line[..space], System.Globalization.NumberStyles.HexNumber, null, out int at) ? at : null;

    /// <summary>A line's code offset and its label column (function and jump names, which move with inserted code).</summary>
    [GeneratedRegex(@"^[0-9A-F]{4,5}\s+(\S+:)?")]
    private static partial Regex EventLabel();

    [GeneratedRegex(@"\([0-9A-F]{4,5}\)")]
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
    [GeneratedRegex(@"(?<=^[0-9A-F]{4,5}\s+(\S+:)?\s*)00 (\.\.\. 00 |00 )*")]
    private static partial Regex NopRun();

    [GeneratedRegex(@"string=""\{TIME:00\}Hold .*? to skip"" \[")]
    private static partial Regex IntroLine();
}
