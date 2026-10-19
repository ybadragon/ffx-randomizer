namespace FfxRandomizer.Core.GameData.Events;

/// <summary>
/// A cutscene found in an event: the function that plays it, the GameMoment values it plays at (first visit), where its
/// exit tail starts, and what skipping it would bypass.
/// </summary>
public sealed record SceneCandidate(
    string EventId, int Worker, int Function, int Entry, int MomentFrom, int MomentBelow, int Exit, int Content,
    SceneAuditResult Audit, int HostWorker, int MessageWindow, int SkipLevel, byte[] EntryBytes, int ExitMoment)
{
    /// <summary>The GameMoment a save should hold so the scene plays when the room loads.</summary>
    public int Trigger => MomentBelow - 1;
}

/// <summary>
/// Finds cutscenes in an event script: for each function, tries the GameMoment values its conditions compare against,
/// follows the path, and keeps paths that play real content (text, voice, camera, motion, also inside functions they
/// run) and then set GameMoment higher than they started.
/// </summary>
public static class SceneFinder
{
    /// <summary>A path needs this much presentation to count as a cutscene.</summary>
    public const int MinimumContent = 12;

    public static IEnumerable<SceneCandidate> Find(string eventId, byte[] scriptChunk, int momentLimit)
    {
        var tables = new ScriptTables(scriptChunk);
        byte[] code = tables.Code.ToArray();
        var entries = Enumerable.Range(0, tables.WorkerCount)
            .SelectMany(w => Enumerable.Range(0, tables.Editor.FunctionCount(w)).Select(f => (W: w, F: f, Entry: tables.Editor.FunctionEntry(w, f))))
            .ToList();
        var allEntries = entries.Select(e => e.Entry).Distinct().Order().ToList();
        var levels = RunLevels(tables);

        foreach (var (w, f, entry) in entries)
        {
            // GameMoment constants this function (up to the next function) compares against.
            int next = allEntries.FirstOrDefault(e => e > entry, code.Length);
            var constants = new SortedSet<int>();
            for (int at = entry; at < next && AtelCode.StatementAt(code, at) is { } s; at = s.End)
                foreach (int c in AtelCode.MomentConstants(s, tables)) { constants.Add(c - 1); constants.Add(c); constants.Add(c + 1); }
            var plays = new List<(int Moment, List<AtelStatement> Path, int Exit, int Written, int Content)>();
            foreach (int v in constants.Where(v => v >= 0 && v < momentLimit))
            {
                var path = SceneAudit.Walk(tables, w, entry, -1, v, depth: 0, [], [], [], out _, out var end);
                if (end.Kind is not (SceneExitKind.Warp or SceneExitKind.Battle or SceneExitKind.SameRoom)) continue;
                int written = path.Select(s => AtelCode.MomentWrite(s, tables)).LastOrDefault(m => m is not null) ?? -1;
                if (written <= v || written >= momentLimit) continue;
                int content = Content(tables, w, path, v, 0);
                if (content < MinimumContent) continue;
                plays.Add((v, path, ExitOf(tables, path), written, content));
            }
            // Group the values that play the same scene to the same exit; keep the first-visit (lowest) group.
            foreach (var group in plays.GroupBy(p => p.Exit).OrderBy(g => g.Min(p => p.Moment)).Take(1))
            {
                int from = group.Min(p => p.Moment), below = group.Max(p => p.Moment) + 1;
                var first = group.OrderByDescending(p => p.Moment).First();
                var audit = SceneAudit.Audit(tables, w, entry, first.Exit, below - 1);
                int host = HostFor(tables, w, audit);
                int window = FreeWindow(tables);
                int level = Math.Max(2, levels.GetValueOrDefault((w, f), 1) + 1);
                yield return new SceneCandidate(eventId, w, f, entry, from, below, first.Exit, first.Content, audit, host, window, level,
                    code.AsSpan(entry, Math.Min(9, code.Length - entry)).ToArray(), first.Written);
            }
        }
    }

    /// <summary>
    /// The exit: the start of the path's tail that has no presentation in it, up to how the path ends. Jumping there
    /// leaves out the scene but keeps whatever the scene does on its way out (set GameMoment, fade, change room).
    /// </summary>
    internal static int ExitOf(ScriptTables tables, List<AtelStatement> path)
    {
        int exit = path[^1].Offset;
        for (int i = path.Count - 1; i >= 0; i--)
        {
            var s = path[i];
            bool content = s.Instructions.Any(ins => ins.Op is AtelCode.Call or AtelCode.CallPopA && SceneAudit.IsContentCall(ins.Operand))
                || (s.Instructions.Any(ins => AtelCode.AwaitsEnd(ins.Op)));
            // Only a straight run of statements counts as the tail: stop at a jump back or a branch.
            bool branch = s.Last.Op is AtelCode.Jmp or AtelCode.PopXJmp or AtelCode.PopXCJmp or AtelCode.PopXNCJmp or AtelCode.CJmp or AtelCode.NCJmp;
            if (content || (branch && i < path.Count - 1)) break;
            if (i < path.Count - 1 && path[i].End != path[i + 1].Offset) break;
            exit = s.Offset;
        }
        return exit;
    }

    private static int Content(ScriptTables tables, int worker, List<AtelStatement> path, int moment, int depth)
    {
        int count = 0;
        foreach (var s in path)
        {
            count += s.Instructions.Count(i => i.Op is AtelCode.Call or AtelCode.CallPopA && SceneAudit.IsContentCall(i.Operand));
            if (depth < 2 && AtelCode.RunTarget(s, tables) is { } run && run.Worker < tables.WorkerCount && run.Function < tables.Editor.FunctionCount(run.Worker))
            {
                var inner = SceneAudit.Walk(tables, run.Worker, tables.Editor.FunctionEntry(run.Worker, run.Function), -1, moment, 3, [], [], [], out _, out _);
                count += Content(tables, run.Worker, inner, moment, depth + 1);
            }
        }
        return count;
    }

    /// <summary>The highest level any statement runs each function at.</summary>
    private static Dictionary<(int, int), int> RunLevels(ScriptTables tables)
    {
        var levels = new Dictionary<(int, int), int>();
        byte[] code = tables.Code.ToArray();
        for (int at = 0; at < code.Length && AtelCode.StatementAt(code, at) is { } s; at = s.End)
            if (AtelCode.RunTarget(s, tables) is { } run)
                levels[(run.Worker, run.Function)] = Math.Max(levels.GetValueOrDefault((run.Worker, run.Function)), run.Level);
        return levels;
    }

    /// <summary>
    /// A worker the skip watcher can run on: idle during the scene, so an edge the player can't touch while the scene
    /// has control (type 5, then 2, then a scenario), never one the scene itself runs or the scene's own worker.
    /// </summary>
    private static int HostFor(ScriptTables tables, int sceneWorker, SceneAuditResult audit)
    {
        var used = audit.RunTargets.Select(r => r.Worker).ToHashSet();
        byte[] code = tables.Code.ToArray();
        // Workers any statement in the event runs functions on at all are busy somewhere; prefer ones nobody runs.
        var runAnywhere = new HashSet<int>();
        for (int at = 0; at < code.Length && AtelCode.StatementAt(code, at) is { } s; at = s.End)
            if (AtelCode.RunTarget(s, tables) is { } run) runAnywhere.Add(run.Worker);
        foreach (int type in new[] { 5, 2, 4 })
            for (int w = 0; w < tables.WorkerCount; w++)
                if (w != sceneWorker && tables.WorkerType(w) == type && !used.Contains(w) && !runAnywhere.Contains(w)) return w;
        foreach (int type in new[] { 5, 2, 4 })
            for (int w = 0; w < tables.WorkerCount; w++)
                if (w != sceneWorker && tables.WorkerType(w) == type && !used.Contains(w)) return w;
        return -1;
    }

    /// <summary>A message window the event never uses (3 if free, else the lowest free one up to 6).</summary>
    private static int FreeWindow(ScriptTables tables)
    {
        var used = new HashSet<int>();
        byte[] code = tables.Code.ToArray();
        for (int at = 0; at < code.Length && AtelCode.StatementAt(code, at) is { } s; at = s.End)
        {
            var ins = s.Instructions;
            for (int i = 0; i < ins.Count; i++)
                if (ins[i].Op is AtelCode.CallPopA or AtelCode.Call && ins[i].Operand is 0x0064 or 0x0065 or 0x006A or 0x006B && FirstArgument(ins, i) is int window)
                    used.Add(window);
        }
        foreach (int w in new[] { 3, 4, 5, 6, 2, 1 }) if (!used.Contains(w)) return w;
        return 3;
    }

    /// <summary>The window argument of a message call: the first constant pushed in its statement.</summary>
    private static int? FirstArgument(IReadOnlyList<AtelInstruction> ins, int call)
    {
        for (int i = 0; i < call; i++) if (ins[i].Op == AtelCode.PushII) return (short)ins[i].Operand;
        return null;
    }
}
