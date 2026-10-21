namespace FfxRandomizer.Core.GameData.Events;

/// <summary>What skipping past one statement means.</summary>
public enum SkipEffect
{
    /// <summary>Presentation (camera, motion, text, sound) or state the exit path sets again.</summary>
    Ok,
    /// <summary>Lasting state (story flags, items, party...): the skip must run the statement itself.</summary>
    Replay,
    /// <summary>Must not be skipped across: a battle, a choice, a menu, or a room change before the exit.</summary>
    Block,
}

/// <summary>How a scene's exit ends: moving to another room, starting a battle, or staying in the room.</summary>
public enum SceneExitKind { Warp, Battle, SameRoom, Unknown }

/// <summary>One side-effecting statement on the skipped path: which worker runs it, where, and what to do about it.</summary>
/// <remarks><see cref="OwnWorker"/>: the statement acts on the worker running it (a placement), so its replay runs there.</remarks>
public sealed record SkippedStatement(int Worker, int Offset, string What, SkipEffect Effect, byte[] Bytes, bool OwnWorker = false);

/// <summary>One statement as the scene runs it, in order: functions it runs on other workers appear inline.</summary>
public sealed record TraceStep(int Worker, AtelStatement Statement, int Depth, bool Undecided);

/// <summary>The audit of one scene skip: every statement the skip bypasses that does more than presentation.</summary>
public sealed record SceneAuditResult(
    IReadOnlyList<SkippedStatement> Skipped, SceneExitKind ExitKind, string ExitWhat, bool ReachesExit, IReadOnlyList<string> Notes,
    IReadOnlyList<(int Worker, int Function)> RunTargets)
{
    public IEnumerable<SkippedStatement> Blocks => Skipped.Where(s => s.Effect == SkipEffect.Block);
    public IEnumerable<SkippedStatement> Replays => Skipped.Where(s => s.Effect == SkipEffect.Replay);

    /// <summary>A skip is safe when nothing blocks it and it reaches its exit, which leaves the room or returns in it.</summary>
    public bool Safe => !Blocks.Any() && ReachesExit && ExitKind is not SceneExitKind.Unknown;

    /// <summary>The statements the skip runs before jumping to the exit, in the order the scene would have run them.</summary>
    public byte[] ReplayCode => [.. Replays.SelectMany(r => r.Bytes)];

    public IEnumerable<string> Problems()
    {
        if (!ReachesExit) yield return "the first-visit path doesn't reach the exit";
        if (ExitKind == SceneExitKind.Unknown) yield return $"the exit's end wasn't found ({ExitWhat})";
        foreach (var b in Blocks) yield return $"w{b.Worker:X2} {b.Offset:X4}: {b.What} can't be skipped";
    }
}

/// <summary>
/// Layer 1 of cutscene testing: follows a scene's first-visit path from its start to its exit (evaluating GameMoment
/// branches, entering the functions it runs on other workers) and classifies every statement the skip would bypass.
/// Call-target meanings are FFXDataParser's (ScriptCallTargetLib).
/// </summary>
public static class SceneAudit
{
    /// <summary>Calls that can't be skipped across: battles, choices, menus, and room changes before the exit.</summary>
    public static readonly IReadOnlyDictionary<int, string> BlockCalls = new Dictionary<int, string>
    {
        [0x7002] = "launchBattle", [0xC024] = "launchBattleAlwaysWin", [0x013B] = "displayFieldChoice", [0x007D] = "awaitPlayerDialogueChoice",
        [0x0139] = "requestNumericInput", [0x401D] = "showModularMenu",
        [0x010B] = "warpToRoom", [0x0011] = "transitionToRoom", [0x010C] = "transitionToRoom (0x010C)",
    };

    /// <summary>Calls with lasting effects that the skip must run itself.</summary>
    public static readonly IReadOnlyDictionary<int, string> ReplayCalls = new Dictionary<int, string>
    {
        [0x00CA] = "addPartyMember", [0x00CB] = "removePartyMember", [0x00E7] = "putPartyMemberInSlot", [0x01F9] = "removePartyMemberCompletely",
        [0x0111] = "StorePartyMemberSetup", [0x0112] = "RestorePartyMemberSetup", [0x0240] = "setPartyMemberEquipmentHidden",
        [0x0136] = "obtainGil", [0x0137] = "tryPayGil", [0x015B] = "obtainTreasure", [0x01A7] = "obtainTreasureSilently",
        [0x015F] = "addKeyItem", [0x0161] = "removeKeyItem", [0x010D] = "SetPrimerCollected", [0x70ED] = "giveItem",
        [0x01FC] = "teachAbilityToPartyMemberSilently", [0x0216] = "teachAbilityToPartyMemberWithMsg", [0x021D] = "equipBrotherhoodToTidus",
        [0x01B7] = "applyBrotherhoodPowerup", [0x0254] = "setSphereGrid", [0x0171] = "fillPartyMemberHp", [0x0172] = "fillPartyMemberMp",
        [0x00AA] = "SetRandomEncountersActive", [0x016F] = "updateSavedRoomAndSpawnpoint", [0x0170] = "updateSavedRoomAndSpawnpoint",
        [0x00E9] = "setMenuEnabled", [0x014A] = "SetBattleFlags", [0x01D9] = "ActivateRikkuName",
        [0x01AD] = "incrementMonsterArenaCaptures", [0x01AE] = "decrementMonsterArenaCaptures", [0x01AF] = "changeMonsterArenaCaptures",
        [0x0219] = "changeMonsterArenaCaptures2",
        [0x0102] = "setBgmToLoad", [0x0103] = "unloadBgm", [0x0104] = "playBgm", [0x0105] = "loadBgm", [0x017E] = "playBgmWithVol",
        [0x0180] = "changeCurrentBgmVol", [0x0188] = "fadeoutBgm",
    };

    private static readonly HashSet<string> MusicCalls = ["setBgmToLoad", "unloadBgm", "playBgm", "loadBgm", "playBgmWithVol", "changeCurrentBgmVol", "fadeoutBgm"];

    /// <summary>The calls a scene's exit leaves through.</summary>
    public static readonly IReadOnlySet<int> WarpCalls = new HashSet<int> { 0x010B, 0x0011, 0x010C };
    public static readonly IReadOnlySet<int> BattleCalls = new HashSet<int> { 0x7002, 0xC024 };

    /// <summary>Presentation that makes a stretch of code a scene: text, voice, camera and character motion.</summary>
    public static bool IsContentCall(int target) =>
        target is 0x0064 or 0x006A or 0x00D5 or 0x0015 or 0x0018 or 0x0019 || (target >= 0x5000 && target <= 0x50FF) || (target >= 0x6000 && target <= 0x60FF);

    public static bool HasContent(AtelStatement s) => s.Instructions.Any(i => i.Op is AtelCode.Call or AtelCode.CallPopA && IsContentCall(i.Operand));

    public static bool IsWarp(AtelStatement s) => s.Instructions.Any(i => i.Op is AtelCode.Call or AtelCode.CallPopA && WarpCalls.Contains(i.Operand));
    public static bool IsBattle(AtelStatement s) => s.Instructions.Any(i => i.Op is AtelCode.Call or AtelCode.CallPopA && BattleCalls.Contains(i.Operand));

    private const int MaxDepth = 3, MaxSteps = 20000;

    /// <summary>
    /// Follows code the way the scene runs it, from <paramref name="start"/> in <paramref name="worker"/>, with GameMoment
    /// = <paramref name="moment"/>: GameMoment branches are decided, others followed both ways (code on one side of an
    /// undecided forward branch is marked undecided up to its target), functions run on other workers are followed inline
    /// (up to 3 deep). Stops at <paramref name="stop"/> (that worker's code from an offset to the end of its function:
    /// <see cref="Trace.ReachedStop"/>), at a room change or battle (unless <paramref name="throughExits"/>), or when
    /// every branch returns.
    /// </summary>
    public sealed class Trace
    {
        public List<TraceStep> Steps { get; } = [];
        public List<string> Notes { get; } = [];
        public HashSet<(int, int)> Runs { get; } = [];
        public bool ReachedStop { get; set; }
        public TraceStep? End { get; set; }
        public SceneExitKind EndKind { get; set; } = SceneExitKind.Unknown;
        public int FinalMoment { get; set; }
    }

    public static Trace Follow(ScriptTables tables, int worker, int start, int moment, (int Worker, int From, int To)? stop = null, bool throughExits = false)
    {
        var trace = new Trace { FinalMoment = moment };
        Follow(tables, worker, start, moment, 0, stop, throughExits, trace);
        return trace;
    }

    private static bool Follow(ScriptTables tables, int worker, int start, int moment, int depth, (int Worker, int From, int To)? stop, bool throughExits, Trace trace)
    {
        var code = tables.Code;
        var seen = new HashSet<(int, int)>();
        var queue = new Queue<(int At, int Moment, bool Switch)>();
        var undecided = new List<(int From, int To)>();
        queue.Enqueue((start, moment, false));
        int steps = 0;
        bool reached = false;
        while (queue.Count > 0 && steps++ < MaxSteps)
        {
            var (at, gm, ySwitch) = queue.Dequeue();
            while (true)
            {
                if (stop is { } x && x.Worker == worker && at >= x.From && at < x.To) { trace.ReachedStop = reached = true; break; }
                if (!seen.Add((at, gm))) break;
                if (at < 0 || at >= code.Length || AtelCode.StatementAt(code, at) is not { } s) { trace.Notes.Add($"w{worker:X2}: code runs off at {at:X4}"); break; }
                bool isUndecided = undecided.Any(r => s.Offset >= r.From && s.Offset < r.To);
                var step = new TraceStep(worker, s, depth, isUndecided);
                trace.Steps.Add(step);
                if (AtelCode.MomentWrite(s, tables) is int written) { gm = written; trace.FinalMoment = written; }
                if (s.Last.Op == AtelCode.PopY) ySwitch = AtelCode.SwitchesOnMoment(s);
                if (IsWarp(s) || IsBattle(s))
                {
                    if (trace.End is null) { trace.End = step; trace.EndKind = IsWarp(s) ? SceneExitKind.Warp : SceneExitKind.Battle; }
                    if (!throughExits) { queue.Clear(); return reached; }
                }
                // Follow functions this runs on other workers (or this one) inline.
                if (s.Instructions.Any(i => AtelCode.IsRun(i.Op)))
                {
                    if (AtelCode.RunTarget(s, tables) is { } run)
                    {
                        trace.Runs.Add((run.Worker, run.Function));
                        if (depth < MaxDepth && run.Worker >= 0 && run.Worker < tables.WorkerCount && run.Function >= 0 && run.Function < tables.Editor.FunctionCount(run.Worker))
                        {
                            // Once the run reaches the exit, the rest of this path never runs (the exit leaves the room).
                            bool inner = Follow(tables, run.Worker, tables.Editor.FunctionEntry(run.Worker, run.Function), gm, depth + 1, stop, throughExits, trace);
                            if (trace.End is not null && !throughExits) { queue.Clear(); return reached; }
                            if (inner) { reached = true; break; }
                            gm = trace.FinalMoment;
                        }
                    }
                    else trace.Notes.Add($"w{worker:X2} {s.Offset:X4}: runs a worker function chosen at run time (not followed)");
                }
                if (s.Last.Op == AtelCode.Jsr) trace.Notes.Add($"w{worker:X2} {s.Offset:X4}: jumps to subroutine worker {s.Last.Operand:X2} (not followed)");
                var last = s.Last;
                if (AtelCode.IsReturn(last.Op))
                {
                    if (depth == 0 && trace.End is null && trace.EndKind == SceneExitKind.Unknown) { trace.End = step; trace.EndKind = SceneExitKind.SameRoom; }
                    break;
                }
                if (last.Op is AtelCode.Jmp or AtelCode.PopXJmp) { at = Target(tables, worker, last.Operand); continue; }
                if (last.Op is AtelCode.PopXCJmp or AtelCode.PopXNCJmp)
                {
                    bool? value = AtelCode.EvaluateMomentCondition(s, gm, tables, ySwitch);
                    int target = Target(tables, worker, last.Operand);
                    if (value is bool v) { at = v == (last.Op == AtelCode.PopXCJmp) ? target : s.End; continue; }
                    queue.Enqueue((target, gm, ySwitch));
                    if (target > s.End) undecided.Add((s.End, target));
                    at = s.End;
                    continue;
                }
                if (last.Op is AtelCode.CJmp or AtelCode.NCJmp) { queue.Enqueue((Target(tables, worker, last.Operand), gm, ySwitch)); at = s.End; continue; }
                at = s.End;
            }
        }
        return reached;
    }

    private static int Target(ScriptTables tables, int worker, int jump) =>
        jump >= 0 && jump < tables.Editor.JumpCount(worker) ? tables.Editor.JumpTarget(worker, jump) : -1;

    /// <summary>The end of the function code that holds <paramref name="offset"/>: the next function entry of any worker.</summary>
    public static int FunctionRegionEnd(ScriptTables tables, int offset) =>
        Enumerable.Range(0, tables.WorkerCount)
            .SelectMany(w => Enumerable.Range(0, tables.Editor.FunctionCount(w)).Select(f => tables.Editor.FunctionEntry(w, f)))
            .Where(e => e > offset).DefaultIfEmpty(tables.Code.Length).Min();

    /// <summary>
    /// Audits skipping a scene that starts at <paramref name="start"/> in <paramref name="startWorker"/> and exits at
    /// <paramref name="exit"/> in <paramref name="exitWorker"/>, with GameMoment = <paramref name="moment"/>. Code from the
    /// exit to the end of its function runs on the skip path too, so reaching it anywhere counts as reaching the exit.
    /// </summary>
    public static SceneAuditResult Audit(ScriptTables tables, int startWorker, int start, int exitWorker, int exit, int moment)
    {
        var stop = (exitWorker, exit, FunctionRegionEnd(tables, exit));
        var before = Follow(tables, startWorker, start, moment, stop, throughExits: true);
        var after = Follow(tables, exitWorker, exit, before.FinalMoment);
        var (kind, what) = after.End is { } e
            ? (after.EndKind, $"{(IsWarp(e.Statement) ? "changes room" : IsBattle(e.Statement) ? "starts a battle" : "returns")} at w{e.Worker:X2} {e.Statement.Offset:X4}")
            : (SceneExitKind.Unknown, "no end found");

        var skipped = new List<SkippedStatement>();
        bool sameRoom = kind == SceneExitKind.SameRoom;
        int player = PlayerWorker(tables);
        foreach (var step in before.Steps) Classify(tables, step, skipped, sameRoom, player, exitWorker);
        skipped = skipped.DistinctBy(s => (s.Worker, s.Offset, s.What)).ToList();
        if (sameRoom) skipped.AddRange(PlayerPlacement(tables, before, player, exitWorker));

        var exitBytes = after.Steps.Select(s => Convert.ToHexString(tables.Code.Slice(s.Statement.Offset, s.Statement.Length))).ToHashSet();
        bool exitSetsMoment = after.Steps.Any(s => AtelCode.MomentWrite(s.Statement, tables) is not null);
        for (int i = 0; i < skipped.Count; i++)
        {
            var s = skipped[i];
            if (s.Effect == SkipEffect.Ok) continue;
            string? why = null;
            if (kind is SceneExitKind.Warp or SceneExitKind.Battle && MusicCalls.Contains(s.What.Split(' ')[0])) why = "music; the next room sets its own";
            else if (s.Effect == SkipEffect.Replay && exitBytes.Contains(Convert.ToHexString(s.Bytes))) why = "the exit does it too";
            else if (s.Effect == SkipEffect.Replay && exitSetsMoment && s.What.StartsWith("Set GameMoment", StringComparison.Ordinal)) why = "the exit sets GameMoment";
            if (why is not null) skipped[i] = s with { Effect = SkipEffect.Ok, What = $"{s.What} ({why})" };
        }
        return new SceneAuditResult(skipped, kind, what, before.ReachedStop, [.. before.Notes.Distinct()], [.. before.Runs]);
    }

    /// <summary>Calls that place the worker running them: where the player stands after a scene that stays in the room.</summary>
    private static readonly IReadOnlyDictionary<int, string> PositionCalls = new Dictionary<int, string>
    {
        [0x0126] = "warpToPoint", [0x0013] = "setPosition",
    };

    private const int SetDestination = 0x0015, WarpToPoint = 0x0126;

    /// <summary>
    /// Where the player ends up after a scene that stays in the room: only the last placement the player's own worker
    /// makes counts (warpToPoint, setPosition, or the destination of its last walk, replayed as a warpToPoint). It can only
    /// be replayed when the exit's worker is the player, since a placement moves whoever runs it.
    /// </summary>
    private static IEnumerable<SkippedStatement> PlayerPlacement(ScriptTables tables, Trace before, int player, int exitWorker)
    {
        var placements = before.Steps
            .Where(s => s.Worker == player && s.Statement.Last.Op == AtelCode.CallPopA && s.Statement.Last.Operand is WarpToPoint or 0x0013 or SetDestination)
            .ToList();
        if (placements.Count == 0) yield break;
        var last = placements[^1];
        var s = last.Statement;
        byte[] bytes = tables.Code.Slice(s.Offset, s.Length).ToArray();
        string what = $"player's last placement ({(s.Last.Operand == SetDestination ? "walk destination, replayed as warpToPoint" : PositionCalls[s.Last.Operand])})";
        if (s.Last.Operand == SetDestination) bytes[^2] = WarpToPoint & 0xFF;
        if (s.Last.Operand == SetDestination) bytes[^1] = WarpToPoint >> 8;
        bool replayable = !last.Undecided && s.Instructions.Take(s.Instructions.Count - 1).All(i => i.Op is AtelCode.PushF or AtelCode.PushII or AtelCode.PushI);
        yield return new SkippedStatement(last.Worker, s.Offset, replayable ? what : what + " (the place is computed or on an undecided branch)",
            replayable ? SkipEffect.Replay : SkipEffect.Block, bytes, OwnWorker: last.Worker != exitWorker);
    }

    /// <summary>A signal level above anything the event runs <paramref name="worker"/>'s functions at (at least 2).</summary>
    public static int LevelAbove(ScriptTables tables, int worker)
    {
        int highest = 1;
        var code = tables.Code;
        for (int at = 0; at < code.Length && AtelCode.StatementAt(code, at) is { } s; at = s.End)
            if (AtelCode.RunTarget(s, tables) is { } run && run.Worker == worker) highest = Math.Max(highest, run.Level);
        return highest + 1;
    }

    /// <summary>The worker the event makes the player (the one that calls setWorkerAsPlayer), or -1.</summary>
    public static int PlayerWorker(ScriptTables tables)
    {
        var code = tables.Code;
        for (int at = 0; at < code.Length && AtelCode.StatementAt(code, at) is { } s; at = s.End)
            if (s.Instructions.Any(i => i.Op == AtelCode.CallPopA && i.Operand == 0x0043) && tables.FunctionAt(s.Offset) is { } owner) return owner.Worker;
        return -1;
    }

    /// <summary>
    /// Classifies one statement the skip bypasses. For a scene that stays in the room, the room's own state matters too:
    /// event variables and music are replayed, and where the player ends up (replayable only when the exit's worker is
    /// the player, since a position call places whoever runs it).
    /// </summary>
    private static void Classify(ScriptTables tables, TraceStep step, List<SkippedStatement> skipped, bool sameRoom, int player, int exitWorker)
    {
        var s = step.Statement;
        void Add(string what, SkipEffect effect)
        {
            if (effect == SkipEffect.Replay && !Replayable(s)) { effect = SkipEffect.Block; what += " (can't be replayed: computed operands)"; }
            if (effect == SkipEffect.Replay && step.Undecided) { effect = SkipEffect.Block; what += " (on a branch the audit can't decide)"; }
            skipped.Add(new SkippedStatement(step.Worker, s.Offset, what, effect, tables.Code.Slice(s.Offset, s.Length).ToArray()));
        }
        foreach (var ins in s.Instructions.Where(i => i.Op is AtelCode.Call or AtelCode.CallPopA))
        {
            if (BlockCalls.TryGetValue(ins.Operand, out string? block)) Add(block, SkipEffect.Block);
            else if (ReplayCalls.TryGetValue(ins.Operand, out string? replay)) Add(replay, SkipEffect.Replay);
        }
        var last = s.Last;
        if (last.Op is AtelCode.PopV or AtelCode.PopVL or AtelCode.PopAr or AtelCode.PopArL)
        {
            int location = tables.VariableLocation(last.Operand);
            if (AtelCode.MomentWrite(s, tables) is int m) Add($"Set GameMoment = {m}", SkipEffect.Replay);
            else if (location == 0) Add($"save-data variable {last.Operand:X2} written", SkipEffect.Replay);
            else if (sameRoom && location == 6) Add($"event variable {last.Operand:X2} written", SkipEffect.Replay);
        }
    }

    /// <summary>Whether a statement can be copied into the skip: only constants, variables and plain calls feed it.</summary>
    public static bool Replayable(AtelStatement s) => s.Instructions.All(i => i.Op switch
    {
        AtelCode.PushII or AtelCode.PushI or AtelCode.PushF or AtelCode.PushV or AtelCode.PushAr or AtelCode.Call => true,
        >= 0x01 and <= 0x1C => true,
        AtelCode.PopV or AtelCode.PopVL or AtelCode.PopAr or AtelCode.PopArL or AtelCode.CallPopA => true,
        _ => false,
    });
}
