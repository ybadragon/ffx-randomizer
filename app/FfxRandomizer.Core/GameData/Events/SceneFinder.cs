namespace FfxRandomizer.Core.GameData.Events;

/// <summary>
/// A cutscene found in an event: the function that starts it (<see cref="Worker"/>::<see cref="Function"/>), the GameMoment
/// values it plays at, where its exit tail starts (possibly in a function it runs on another worker), and what skipping
/// it would bypass.
/// </summary>
public sealed record SceneCandidate(
    string EventId, int Worker, int Function, int Entry, int MomentFrom, int MomentBelow, int ExitWorker, int Exit, int Content,
    SceneAuditResult Audit, int HostWorker, int MessageWindow, int SkipLevel, byte[] EntryBytes, byte[] ExitBytes, int ExitMoment,
    IReadOnlyList<string> AlsoStartedBy);

/// <summary>A path that plays content but can't be skipped this way, and why.</summary>
public sealed record SceneRejection(string EventId, int Worker, int Function, int Moment, int Content, string Reason);

/// <summary>
/// Finds cutscenes in an event script: for every function and every GameMoment value the event compares against, follows
/// the path the way the scene runs it (including functions it runs on other workers) and keeps paths that play real
/// content (text, voice, camera, motion), set GameMoment higher than they started, and then leave the room.
/// </summary>
public static class SceneFinder
{
    /// <summary>A path needs this many presentation statements to count as a cutscene.</summary>
    public const int MinimumContent = 12;

    /// <summary>Signal levels go up to 7; the skip has to preempt the scene, so it runs one above it.</summary>
    public const int MaxLevel = 7;

    /// <summary>Receives a line per function and GameMoment tried, for working out why a scene isn't found.</summary>
    public static Action<string>? Trace { get; set; }

    public static (List<SceneCandidate> Found, List<SceneRejection> Rejected) Find(string eventId, byte[] scriptChunk, int momentFrom, int momentLimit)
    {
        var tables = new ScriptTables(scriptChunk);
        byte[] code = tables.Code.ToArray();
        var statements = new List<AtelStatement>();
        for (int at = 0; at < code.Length && AtelCode.StatementAt(code, at) is { } s; at = s.End) statements.Add(s);
        var eventMoments = new SortedSet<int>();
        foreach (var s in statements)
        {
            foreach (int c in AtelCode.MomentConstants(s, tables)) { eventMoments.Add(c - 1); eventMoments.Add(c); eventMoments.Add(c + 1); }
            // A scene that never compares GameMoment still plays at the value just before the one it writes.
            if (AtelCode.MomentWrite(s, tables) is int written) eventMoments.Add(written - 1);
        }
        var levels = RunLevels(tables, statements);
        var functionEntries = Enumerable.Range(0, tables.WorkerCount)
            .SelectMany(w => Enumerable.Range(0, tables.Editor.FunctionCount(w)).Select(f => (W: w, F: f, Entry: tables.Editor.FunctionEntry(w, f))))
            .ToList();

        var plays = new List<(int W, int F, int Entry, int Moment, bool OwnConditions, int ExitWorker, int Exit, int Written, int Content, SceneExitKind Kind)>();
        var rejected = new List<SceneRejection>();
        foreach (var (w, f, entry) in functionEntries)
        {
            int regionEnd = SceneAudit.FunctionRegionEnd(tables, entry);
            bool own = statements.Any(s => s.Offset >= entry && s.Offset < regionEnd && AtelCode.MomentConstants(s, tables).Any());
            foreach (int v in eventMoments.Where(v => v >= momentFrom && v < momentLimit))
            {
                var trace = SceneAudit.Follow(tables, w, entry, v);
                int content = trace.Steps.Count(s => SceneAudit.HasContent(s.Statement));
                int written = trace.FinalMoment;
                Trace?.Invoke($"{eventId} w{w:X2}::f{f:X2} @{entry:X4} GameMoment {v}: {trace.EndKind}, writes {written}, content {content}, {trace.Steps.Count} statements");
                if (content < MinimumContent || written <= v || written >= momentLimit) continue;
                if (trace.EndKind == SceneExitKind.SameRoom && trace.End is { } e && (e.Depth != 0 || e.Worker != w))
                {
                    rejected.Add(new SceneRejection(eventId, w, f, v, content, "it ends in the room inside a function it runs on another worker"));
                    continue;
                }
                if (trace.EndKind == SceneExitKind.Unknown)
                {
                    rejected.Add(new SceneRejection(eventId, w, f, v, content, "no end found"));
                    continue;
                }
                var (exitWorker, exit) = ExitOf(trace);
                plays.Add((w, f, entry, v, own, exitWorker, exit, written, content, trace.EndKind));
            }
        }

        // One scene per exit: the trigger that plays the most of it (the function the engine starts, not one it runs).
        var found = new List<SceneCandidate>();
        foreach (var byExit in plays.GroupBy(p => (p.ExitWorker, p.Exit)))
        {
            // Prefer a root (a function nothing in the event runs, so the engine starts it), then the most content.
            var runByEvent = statements.Select(s => AtelCode.RunTarget(s, tables)).OfType<(int Level, int Worker, int Function)>().Select(r => (r.Worker, r.Function)).ToHashSet();
            var triggers = byExit.GroupBy(p => (p.W, p.F))
                .OrderBy(g => runByEvent.Contains(g.Key) ? 1 : 0).ThenByDescending(g => g.Max(p => p.Content)).ThenBy(g => g.Key.W).ToList();
            var main = triggers[0].ToList();
            var (w, f) = triggers[0].Key;
            int entry = main[0].Entry;
            int below = main.Max(p => p.Moment) + 1;
            int from = main[0].OwnConditions ? main.Min(p => p.Moment) : 0;
            var best = main.OrderByDescending(p => p.Moment).First();
            var audit = SceneAudit.Audit(tables, w, entry, best.ExitWorker, best.Exit, below - 1);
            int level = Math.Max(2, levels.GetValueOrDefault(best.ExitWorker, 1) + 1);
            int host = HostFor(tables, statements, [w, best.ExitWorker], audit);
            var problems = audit.Problems().ToList();
            if (level > MaxLevel) problems.Add($"w{best.ExitWorker:X2} already runs at level {level - 1}, so nothing can preempt it");
            if (host < 0) problems.Add("no idle worker to run the skip watcher on");
            if (problems.Count > 0)
            {
                rejected.Add(new SceneRejection(eventId, w, f, below - 1, best.Content, string.Join("; ", problems)));
                continue;
            }
            found.Add(new SceneCandidate(eventId, w, f, entry, from, below, best.ExitWorker, best.Exit, best.Content, audit, host,
                FreeWindow(tables, statements), level, code.AsSpan(entry, Math.Min(9, code.Length - entry)).ToArray(),
                code.AsSpan(best.Exit, Math.Min(6, code.Length - best.Exit)).ToArray(), best.Written,
                [.. triggers.Skip(1).Select(t => $"w{t.Key.W:X2}::f{t.Key.F:X2}")]));
        }
        // A scene another found scene runs is part of that one (skipping the outer one skips it).
        var nested = found.Where(c => found.Any(o => o != c && o.Audit.RunTargets.Contains((c.Worker, c.Function)))).ToList();
        foreach (var c in nested)
            rejected.Add(new SceneRejection(eventId, c.Worker, c.Function, c.MomentBelow - 1, c.Content, "part of a longer scene that runs it"));
        found.RemoveAll(nested.Contains);
        return (found, rejected);
    }

    /// <summary>
    /// The exit: the start of the straight run of statements before the room change (or battle) that plays nothing,
    /// in the function the change happens in. Jumping there leaves out the scene but keeps whatever it does on its way
    /// out (set GameMoment, fade, change room).
    /// </summary>
    internal static (int Worker, int Offset) ExitOf(SceneAudit.Trace trace)
    {
        var steps = trace.Steps;
        int endIndex = steps.IndexOf(trace.End!);
        var end = steps[endIndex];
        int exit = end.Statement.Offset;
        for (int i = endIndex - 1; i >= 0; i--)
        {
            var s = steps[i];
            if (s.Worker != end.Worker || s.Depth != end.Depth || s.Statement.End != steps[i + 1].Statement.Offset) break;
            var last = s.Statement.Last;
            bool branch = last.Op is AtelCode.Jmp or AtelCode.PopXJmp or AtelCode.PopXCJmp or AtelCode.PopXNCJmp or AtelCode.CJmp or AtelCode.NCJmp;
            if (branch || SceneAudit.HasContent(s.Statement) || s.Statement.Instructions.Any(ins => AtelCode.IsRun(ins.Op))) break;
            exit = s.Statement.Offset;
        }
        return (end.Worker, exit);
    }

    /// <summary>The highest level anything runs a function of each worker at.</summary>
    private static Dictionary<int, int> RunLevels(ScriptTables tables, List<AtelStatement> statements)
    {
        var levels = new Dictionary<int, int>();
        foreach (var s in statements)
            if (AtelCode.RunTarget(s, tables) is { } run)
                levels[run.Worker] = Math.Max(levels.GetValueOrDefault(run.Worker), run.Level);
        return levels;
    }

    /// <summary>
    /// A worker the skip watcher can run on: idle during the scene, so an edge the player can't touch while the scene
    /// has control (type 5, then 2, then a scenario), never one the scene runs or starts or exits from.
    /// </summary>
    private static int HostFor(ScriptTables tables, List<AtelStatement> statements, int[] sceneWorkers, SceneAuditResult audit)
    {
        var used = audit.RunTargets.Select(r => r.Worker).Concat(sceneWorkers).ToHashSet();
        var runAnywhere = statements.Select(s => AtelCode.RunTarget(s, tables)).OfType<(int Level, int Worker, int Function)>().Select(r => r.Worker).ToHashSet();
        foreach (bool strict in new[] { true, false })
            foreach (int type in new[] { 5, 2, 4 })
                for (int w = 0; w < tables.WorkerCount; w++)
                    if (tables.WorkerType(w) == type && !used.Contains(w) && (!strict || !runAnywhere.Contains(w))) return w;
        return -1;
    }

    /// <summary>A message window the event never uses (3 if free, else the lowest free one up to 6).</summary>
    private static int FreeWindow(ScriptTables tables, List<AtelStatement> statements)
    {
        var used = new HashSet<int>();
        foreach (var s in statements)
        {
            var ins = s.Instructions;
            for (int i = 0; i < ins.Count; i++)
                if (ins[i].Op is AtelCode.CallPopA or AtelCode.Call && ins[i].Operand is 0x0064 or 0x0065 or 0x006A or 0x006B or 0x0084 or 0x007C
                    && ins.Take(i).FirstOrDefault(p => p.Op == AtelCode.PushII) is { Op: AtelCode.PushII } first)
                    used.Add((short)first.Operand);
        }
        foreach (int w in new[] { 3, 4, 5, 6, 2, 1 }) if (!used.Contains(w)) return w;
        return 3;
    }
}
