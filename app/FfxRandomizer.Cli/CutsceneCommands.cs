using FfxRandomizer.Core;
using FfxRandomizer.Core.GameData;
using FfxRandomizer.Core.GameData.Events;
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

    private static string Emit(SceneCandidate c) =>
        $"        new(\"{c.EventId} scene\", \"{c.EventId[..4]}\", \"{c.EventId}\", SceneWorker: 0x{c.Worker:X2}, " +
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
