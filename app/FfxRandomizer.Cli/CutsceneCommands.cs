using FfxRandomizer.Core;
using FfxRandomizer.Core.GameData;
using FfxRandomizer.Core.GameData.Events;
using FfxRandomizer.Core.Randomization;
using FfxRandomizer.Core.Vbf;

/// <summary>
/// Cutscene tools (read-only on the game):
///   ffxr cutscenes find --area &lt;prefix&gt;[,&lt;prefix&gt;...] [--below N] [--emit]
///       candidate scenes found by <see cref="SceneFinder"/>, with their audits; --emit prints SkippableScene entries
///   ffxr cutscenes audit [--area &lt;prefix&gt;]      the skip-safety audit of every scene in Cutscenes.All
///   ffxr cutscenes saves --from &lt;file&gt; (--slots A-B | --out &lt;folder&gt;) [--area &lt;prefix&gt;]
///       jump-to-scene test saves, one per scene: --slots writes into the game's save folder (free slots only, never
///       while the game runs, recorded for `ffxr save delete`); --out writes files to a folder instead
/// </summary>
internal static class CutsceneCommands
{
    public static int Run(RandomizerService service, string[] args)
    {
        var original = service.Patcher.OpenOriginal();
        string? Option(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        var areas = (Option("--area") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        switch (args.FirstOrDefault())
        {
            case "find":
                if (args.Contains("--trace")) SceneFinder.Trace = Console.WriteLine;
                return Find(original, areas, int.Parse(Option("--from") ?? "0"), int.Parse(Option("--below") ?? "1000"), args.Contains("--emit"));
            case "check":
            {
                // The randomizer run with only "Skip cutscenes" on (in memory): every check it makes on the edited files.
                var settings = new RandomizerSettings();
                settings.Choices["cutscenes"] = args.ElementAtOrDefault(1) ?? Cutscenes.Hold;
                var result = RandomizerEngine.Run(original, settings, new Seed("check"));
                foreach (string problem in result.Problems) Console.WriteLine(problem);
                Console.WriteLine($"{result.Files.Count} files, {result.Problems.Count} problems");
                return result.Problems.Count == 0 ? 0 : 1;
            }
            case "generic":
                return Generic(original, Option("--emit"));
            case "audit":
                return Audit(original, areas);
            case "saves":
                return SceneSaves.Write(original, Option("--from") ?? throw new ArgumentException("Give --from <save>."), Option("--slots"), Option("--out"), areas);
            default:
                Console.Error.WriteLine("Use: ffxr cutscenes find --area <prefix> [--below N] [--emit] | ffxr cutscenes audit [--area <prefix>] | ffxr cutscenes saves --from <file> (--slots A-B | --out <folder>) [--area <prefix>]");
                return 2;
        }
    }

    private static IEnumerable<string> EventIds(VbfArchive original, string[] prefixes) =>
        original.Names
            .Where(n => n.StartsWith(GamePaths.Master + "jppc/event/obj/", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".ebp", StringComparison.OrdinalIgnoreCase))
            .Select(n => Path.GetFileNameWithoutExtension(n))
            .Where(id => prefixes.Length == 0 || prefixes.Any(p => id.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .Distinct()
            .Order(StringComparer.Ordinal);

    private static int Find(VbfArchive original, string[] areas, int from, int below, bool emit)
    {
        foreach (string id in EventIds(original, areas))
        {
            var chunk = EventFile.Read(original.ReadFile(Cutscenes.EventPath(id))).Chunk(EventFile.ScriptChunk)!;
            var (found, rejected) = SceneFinder.Find(id, chunk, from, below);
            foreach (var c in found)
            {
                var a = c.Audit;
                Console.WriteLine($"{id} w{c.Worker:X2}::f{c.Function:X2} @{c.Entry:X4}  GameMoment {c.MomentFrom}..{c.MomentBelow - 1} -> {c.ExitMoment}  exit w{c.ExitWorker:X2} {c.Exit:X4} ({a.ExitKind}: {a.ExitWhat})  content {c.Content}  host w{c.HostWorker:X2} window {c.MessageWindow} level {c.SkipLevel}{(c.AlsoStartedBy.Count > 0 ? "  also from " + string.Join(",", c.AlsoStartedBy) : "")}");
                foreach (var s in a.Skipped.Where(s => s.Effect != SkipEffect.Ok)) Console.WriteLine($"    {s.Effect,-6} w{s.Worker:X2} {s.Offset:X4} {s.What}");
                foreach (string n in a.Notes.Distinct().Take(3)) Console.WriteLine($"    note: {n}");
                if (emit) Console.WriteLine(Emit(c));
            }
            foreach (var r in rejected.DistinctBy(r => (r.Worker, r.Function, r.Reason)))
                Console.WriteLine($"{id} w{r.Worker:X2}::f{r.Function:X2}  GameMoment {r.Moment}  content {r.Content}  LEFT OUT: {r.Reason}");
        }
        return 0;
    }

    /// <summary>
    /// The scenes that can be skipped as they are, game-wide: the scene ends by changing room and skipping it bypasses
    /// nothing that lasts (the audit has nothing to replay or block). Left out: temple events (the Cloister of Trials
    /// sequences span several rooms; both Besaid temple scenes failed in-game), Blitzball game events (their scenes are
    /// part of every match), events with a confirmed scene (their other scenes are late-game revisits, left as they are),
    /// events the re-layout gate refuses, and scenes whose GameMoment range overlaps another scene in the same event.
    /// Writes <paramref name="emitFile"/> as the entries' C# lines when given.
    /// </summary>
    private static int Generic(VbfArchive original, string? emitFile)
    {
        var text = GameText.Load(original);
        var kept = new System.Collections.Concurrent.ConcurrentBag<(SceneCandidate Scene, string Name, string Area)>();
        var reasons = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        void LeftOut(string reason, int count = 1) => reasons.AddOrUpdate(reason, count, (_, n) => n + count);
        var ids = SoftReset.FieldEvents(original).ToList();
        // Read every file up front (the archive reader isn't shared across threads), then scan events in parallel.
        var files = ids.ToDictionary(id => id, id => original.ReadFile(Cutscenes.EventPath(id)));
        int done = 0;
        Parallel.ForEach(ids, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, id =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var chunk = EventFile.Read(files[id]).Chunk(EventFile.ScriptChunk)!;
            // The cheap gates first, so the scene search only runs where a scene could be kept.
            string? gate = id[2..4] == "yt" ? "temple (trial sequences)"
                : id.StartsWith("bltz") ? "Blitzball game (its scenes are part of every match)"
                : Cutscenes.All.Any(s => s.EventId == id) ? "has a confirmed scene (its other scenes, late-game revisits, stay as they are)"
                : !HasText(original, id, files[id]) ? "no text to show the hold-to-skip prompt in"
                : Cutscenes.RelayoutRisk(new EventScriptEditor(chunk)) is not null && !Cutscenes.VerifiedInGame.Contains(id) ? "re-layout gate"
                : null;
            if (gate is not null) LeftOut($"event: {gate}");
            else
            {
                var (found, _) = SceneFinder.Find(id, chunk, 0, 4000);
                foreach (var c in found)
                {
                    bool Overlaps(int from, int below) => c.MomentFrom < below && from < c.MomentBelow;
                    if (c.Audit.ExitKind != SceneExitKind.Warp) { LeftOut($"ends {c.Audit.ExitKind}"); continue; }
                    if (c.Audit.Skipped.Any(s => s.Effect != SkipEffect.Ok)) { LeftOut("something to replay or block"); continue; }
                    if (found.Any(o => o != c && Overlaps(o.MomentFrom, o.MomentBelow))) { LeftOut("overlaps another scene in the event"); continue; }
                    var areaIndexes = EventLocation.FromEbp(id, files[id]).AreaNameIndexes;
                    string place = (areaIndexes.Count > 0 ? text.AreaName(areaIndexes[0]) : null) ?? id;
                    kept.Add((c, $"{place} (GameMoment {c.MomentBelow - 1})", place.Split(" - ")[0]));
                }
            }
            int n = Interlocked.Increment(ref done);
            Console.Error.WriteLine($"[{n}/{ids.Count}] {id} {clock.Elapsed.TotalSeconds:F1} s{(gate is null ? "" : $" ({gate})")}");
        });
        foreach (var group in kept.GroupBy(k => k.Area)) Console.WriteLine($"{group.Count(),4}  {group.Key}");
        Console.WriteLine($"{kept.Count} scenes kept");
        foreach (var (reason, count) in reasons.OrderByDescending(r => r.Value)) Console.WriteLine($"{count,4}  left out: {reason}");
        if (emitFile is not null)
            File.WriteAllLines(emitFile, kept.OrderBy(k => k.Scene.MomentBelow).Select(k => Emit(k.Scene, k.Name, k.Area)));
        return 0;
    }

    /// <summary>Whether an event has text (its own Japanese or English chunk, or a language's text file) for the prompt.</summary>
    private static bool HasText(VbfArchive original, string id, byte[] ebp)
    {
        var file = EventFile.Read(ebp);
        return new[] { EventFile.JapaneseTextChunk, EventFile.EnglishTextChunk }.Any(c => c < file.ChunkCount && file.Chunk(c) is { Length: > 0 })
            || GamePaths.LanguageFolders.Any(folder => original.IndexOf(Cutscenes.TextPath(folder, id)) >= 0);
    }

    private static string Emit(SceneCandidate c, string? name = null, string? area = null) =>
        $"        new(\"{(name ?? $"{c.EventId} scene").Replace("\"", "\\\"")}\", \"{area ?? c.EventId[..4]}\", \"{c.EventId}\", SceneWorker: 0x{c.Worker:X2}, " +
        $"SceneStart: new(0x{c.Entry:X4}, [{string.Join(", ", c.EntryBytes.Select(b => $"0x{b:X2}"))}]), " +
        $"Exit: new(Worker: 0x{c.ExitWorker:X2}, Jump: -1, CodeOffset: 0x{c.Exit:X4}, Expected: [{string.Join(", ", c.ExitBytes.Select(b => $"0x{b:X2}"))}]), " +
        $"MomentBelow: {c.MomentBelow}, MessageWindow: {c.MessageWindow}, HostWorker: 0x{c.HostWorker:X2}, MomentFrom: {c.MomentFrom}, SkipLevel: {c.SkipLevel}" +
        (c.Audit.ExitKind == SceneExitKind.SameRoom ? ", StaysInRoom: true" : "") + "),";

    private static int Audit(VbfArchive original, string[] areas)
    {
        int unsafeCount = 0;
        foreach (var scene in Cutscenes.All.Where(s => areas.Length == 0 || areas.Any(p => s.EventId.StartsWith(p, StringComparison.OrdinalIgnoreCase))))
        {
            var audit = Cutscenes.AuditOf(original, scene);
            Console.WriteLine($"{scene.EventId} {scene.Name}: {(audit.Safe ? "OK" : "PROBLEMS")}  exit {audit.ExitKind} ({audit.ExitWhat}), {audit.Replays.Count()} replayed, {audit.Skipped.Count(s => s.Effect == SkipEffect.Ok)} skipped as presentation/re-done");
            foreach (var s in audit.Skipped.Where(s => s.Effect != SkipEffect.Ok)) Console.WriteLine($"    {s.Effect,-6} w{s.Worker:X2} {s.Offset:X4} {s.What}");
            foreach (string p in audit.Problems()) Console.WriteLine($"    PROBLEM: {p}");
            if (!audit.Safe) unsafeCount++;
        }
        return unsafeCount == 0 ? 0 : 1;
    }
}
