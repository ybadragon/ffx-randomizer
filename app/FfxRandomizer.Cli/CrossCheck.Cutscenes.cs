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
    private static IEnumerable<string> CompareCutscenes(VbfArchive original, RandomizerSettings settings, IReadOnlyDictionary<string, byte[]> files, bool blitzball)
    {
        string mode = CutsceneSkipFeature.Mode(settings);
        var button = CutsceneSkipFeature.Button(settings);
        foreach (var group in Cutscenes.ByEvent)
        {
            string eventId = group.Key;
            // Randomized Blitzball tables show up in the variables' values; the Blitzball comparison checks those.
            var (ours, listingProblems, lines, changes) = CompareListing(original, files, eventId, mode,
                ignore: blitzball ? l => l.Contains("BlitzballStatGrowthTable", StringComparison.Ordinal) : null);
            foreach (string p in listingProblems) yield return p;
            // Worker numbers as they are in the edited event (the soft reset may have added a worker before them).
            int W(int worker) => Renumbered(original, eventId, files[Cutscenes.EventPath(eventId)], worker);

            // Each scene's first-visit guard (both bounds when it has a lower one), wherever the mode puts it.
            foreach (var scene in group)
                foreach (string bound in scene.MomentFrom > 0
                    ? new[] { $"GameMoment >= {scene.MomentFrom} [{Hex(scene.MomentFrom)}h]", $"GameMoment < {scene.MomentBelow} [{Hex(scene.MomentBelow)}h]" }
                    : [$"GameMoment < {scene.MomentBelow} [{Hex(scene.MomentBelow)}h]"])
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
                    ? $"run w{W(scene.SceneWorker):X2}::f{b.SkipFunction:X2} at level 2, await end"
                    : $"run w{W(scene.Exit.Worker):X2}::f");
            foreach (string expected in expectedLines)
                if (!ours.Contains(expected, StringComparison.Ordinal)) yield return $"{eventId}: no \"{expected}\" in the inserted code";
            foreach (var scene in watched.Where(s => s.BuiltIn is null))
                if (!Regex.IsMatch(ours, $@"run w{W(scene.Exit.Worker):X2}::f[0-9A-F]{{2}} at level {scene.SkipLevel}, await end"))
                    yield return $"{eventId} {scene.Name}: no skip run on w{W(scene.Exit.Worker):X2} at level {scene.SkipLevel}";

            int polls = Cutscenes.HoldFrames / Cutscenes.PollFrames;
            int prompts = PromptLine().Matches(ours).Count;
            if (prompts != group.Count() * (Cutscenes.Steps + 1)) yield return $"{eventId}: the parser shows {prompts} hold-to-skip prompts, expected {group.Count() * (Cutscenes.Steps + 1)}";
            // The hold test must be the "button is down" call on the chosen button, never the press-only 0x0046.
            int expectedTests = watched.Count * (Cutscenes.IntroFrames / Cutscenes.IntroPollFrames + 1 + polls) + started.Count * (1 + polls);
            // Soft reset's watcher tests its combo's buttons once per poll too.
            if (SoftResetFeature.On(settings) && SoftResetFeature.Buttons(settings).Contains(button.Index)
                && SoftResetFeature.HasWatcher(files[Cutscenes.EventPath(eventId)], SoftResetFeature.Buttons(settings))) expectedTests += SoftReset.HoldFrames / SoftReset.PollFrames + 1;
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
                string start = $"run w{W(scene.BuiltIn?.Worker ?? scene.HostWorker):X2}::f";
                if (!ours.Split('\n').Any(l => l.Contains(start, StringComparison.Ordinal) && l.Contains("at level 1;", StringComparison.Ordinal)))
                    yield return $"{eventId} {scene.Name}: nothing starts the watcher on w{W(scene.BuiltIn?.Worker ?? scene.HostWorker):X2}";
            }
        }
    }

    /// <summary>
    /// Disassembles an edited event and its original with PARSE_EVENT. Returns the listing of the inserted code, the
    /// patched listing's lines and the edit's code changes, and problems: parser errors, blanked-out instructions that
    /// don't read back as NOPs, and any difference from vanilla outside the inserted code (addresses and labels aside).
    /// </summary>
    private static (string Ours, List<string> Problems, string[] Lines, CutsceneSkipFeature.CodeChanges Changes) CompareListing(
        VbfArchive original, IReadOnlyDictionary<string, byte[]> files, string eventId, string dumpName, string? vanillaOut = null, string? patchedOut = null,
        Func<string, bool>? ignore = null)
    {
        var problems = new List<string>();
        string path = Cutscenes.EventPath(eventId);
        // The listings, unless the caller parsed a batch of events already.
        // An event the soft reset gave a worker of its own has its later workers renumbered; it is compared with the
        // original renumbered the same way, so only the added code (and the added worker's header line) differ.
        var (baseline, ownWorker) = Baseline(original, eventId, files[path]);
        // The listings, unless the caller parsed a batch of events already.
        if (vanillaOut is null || patchedOut is null)
        {
            var overlay = ownWorker ? new Dictionary<string, byte[]> { [path] = baseline } : new Dictionary<string, byte[]>();
            (vanillaOut, var vanillaErrors) = RunParser(overlay, "PARSE_EVENT", eventId);
            (patchedOut, var patchedErrors) = RunParser(files, "PARSE_EVENT", eventId);
            problems.AddRange(vanillaErrors.Concat(patchedErrors).Select(e => $"{eventId}: parser error: {e}"));
        }
        if (ownWorker)
        {
            var also = ignore ?? (_ => false);
            ignore = l => also(l) || WorkerHeaderLine().IsMatch(l);
        }

        var changes = CutsceneSkipFeature.ChangedCode(baseline, files[path]);
        var lines = patchedOut.Replace("\r", "").Split('\n');
        bool Ours(string line) => CodeOffset(line) is int at && changes.Inserted.Any(r => at >= r.Offset && at < r.Offset + r.Length);
        // Instructions blanked out by NOPs: vanilla shows the original instruction, and the parser folds the NOPs into
        // the next instruction's line ("00 ... 00 AEF000 D80000"), so that line loses its NOP prefix for comparing.
        bool Blanked(string line) => CodeOffset(line) is int at && changes.Nopped.Any(n => at >= n.Original && at < n.Original + n.Length);
        string ours = string.Join("\n", lines.Where(Ours));
        foreach (var n in changes.Nopped)
            if (!lines.Any(l => CodeOffset(l) == n.Offset && NopRun().IsMatch(l)))
                problems.Add($"{eventId}: the blanked-out instruction at {n.Offset:X4} doesn't read back as NOPs");
        string Unblanked(string line) =>
            CodeOffset(line) is int at && changes.Nopped.Any(n => n.Offset == at) ? NopRun().Replace(line, "", 1) : line;
        // FFXR_CROSSCHECK_DUMP=<folder> saves both listings for reading.
        if (Environment.GetEnvironmentVariable("FFXR_CROSSCHECK_DUMP") is { Length: > 0 } dump)
        {
            Directory.CreateDirectory(dump);
            File.WriteAllText(Path.Combine(dump, $"{eventId}.vanilla.txt"), vanillaOut);
            File.WriteAllText(Path.Combine(dump, $"{eventId}.{dumpName}.txt"), patchedOut);
        }

        // Lines another feature changes on purpose (and checks in its own comparison) can be left out.
        ignore ??= _ => false;
        var vanilla = Normalize(vanillaOut.Replace("\r", "").Split('\n').Where(l => !Blanked(l) && !ignore(l))).ToList();
        var patched = Normalize(lines.Where(l => !Ours(l) && !ignore(l)).Select(Unblanked)).ToList();
        var diff = vanilla.Except(patched).Concat(patched.Except(vanilla)).ToList();
        if (diff.Count > 0 || vanilla.Count != patched.Count)
            problems.Add($"{eventId}: {diff.Count} line(s) outside the inserted code differ from vanilla (vanilla {vanilla.Count}, patched {patched.Count}), e.g. \"{string.Join("\" / \"", diff.Take(4))}\"");
        return (ours, problems, lines, changes);
    }

    /// <summary>
    /// What an edited event is compared with: the original, or for an event the soft reset gave a worker of its own,
    /// the original with the same worker references renumbered (and whether it is that).
    /// </summary>
    private static (byte[] Bytes, bool OwnWorker) Baseline(VbfArchive original, string eventId, byte[] final)
    {
        byte[] bytes = original.ReadFile(Cutscenes.EventPath(eventId));
        int Workers(byte[] ebp) => new EventScriptEditor(EventFile.Read(ebp).Chunk(EventFile.ScriptChunk)!).WorkerCount;
        if (Workers(final) == Workers(bytes)) return (bytes, false);
        return SoftReset.RenumberedOnly(bytes, SoftReset.CutsceneWorkers(eventId)) is { } renumbered ? (renumbered.Bytes, true) : (bytes, false);
    }

    /// <summary>Where a worker is in the edited event: one up from the soft reset's own worker on.</summary>
    private static int Renumbered(VbfArchive original, string eventId, byte[] final, int worker)
    {
        byte[] bytes = original.ReadFile(Cutscenes.EventPath(eventId));
        return Baseline(original, eventId, final).OwnWorker && SoftReset.RenumberedOnly(bytes, SoftReset.CutsceneWorkers(eventId)) is { } r && worker >= r.Index
            ? worker + 1 : worker;
    }

    /// <summary>Lines an added worker changes: worker headers and count, and addresses after the worker headers.</summary>
    [GeneratedRegex(@"^w[0-9A-F]{2}: \{|Workers Total|^Script Code Start Address:|^Variables \(\d+ at offset")]
    private static partial Regex WorkerHeaderLine();

    /// <summary>The type the parser shows after a worker reference, looked up by number (which the baseline renumbers).</summary>
    [GeneratedRegex(@"(?<=w[0-9A-F]{2}) ?\((Subroutine|FieldObject|PlayerEdge|PlayerZone|Scenario|Edge|Zone)\)")]
    private static partial Regex WorkerTypeName();

    private static IEnumerable<string> Normalize(IEnumerable<string> listing) => listing
        .Select(l => WorkerTypeName().Replace(l, ""))
        .Select(l => EventLabel().Replace(l, "").Replace(" ", ""))
        .Select(l => JumpTarget().Replace(l, ""))
        .Select(l => PrivateAddress().Replace(l, "priv"))
        .Where(l => l.Length > 0);

    /// <summary>A constant's hex as the parser prints it: two digits, or four from 0x100.</summary>
    private static string Hex(int value) => value > 0xFF ? $"{value:X4}" : $"{value:X2}";

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
