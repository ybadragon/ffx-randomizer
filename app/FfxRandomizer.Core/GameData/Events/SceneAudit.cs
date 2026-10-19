namespace FfxRandomizer.Core.GameData.Events;

/// <summary>What skipping past one statement means.</summary>
public enum SkipEffect
{
    /// <summary>Presentation (camera, motion, text, sound) or state the exit path sets again.</summary>
    Ok,
    /// <summary>Lasting state (story flags, items, party, music...): the skip must run the statement itself.</summary>
    Replay,
    /// <summary>Must not be skipped across: a battle, a choice, a menu, or a room change before the exit.</summary>
    Block,
}

/// <summary>How a scene's exit ends: moving to another room, starting a battle, or staying in the room.</summary>
public enum SceneExitKind { Warp, Battle, SameRoom, Unknown }

/// <summary>One side-effecting statement on the skipped path: which worker runs it, where, and what to do about it.</summary>
public sealed record SkippedStatement(int Worker, int Offset, string What, SkipEffect Effect, byte[] Bytes);

/// <summary>The audit of one scene skip: every statement the skip bypasses that does more than presentation.</summary>
public sealed record SceneAuditResult(
    IReadOnlyList<SkippedStatement> Skipped, SceneExitKind ExitKind, string ExitWhat, bool ReachesExit, IReadOnlyList<string> Notes,
    IReadOnlyList<(int Worker, int Function)> RunTargets)
{
    public IEnumerable<SkippedStatement> Blocks => Skipped.Where(s => s.Effect == SkipEffect.Block);
    public IEnumerable<SkippedStatement> Replays => Skipped.Where(s => s.Effect == SkipEffect.Replay);

    /// <summary>A skip is safe when nothing blocks it, it reaches its exit and the exit leaves the room.</summary>
    public bool Safe => !Blocks.Any() && ReachesExit && ExitKind is SceneExitKind.Warp or SceneExitKind.Battle;

    /// <summary>The statements the skip runs before jumping to the exit, in the order the scene would have run them.</summary>
    public byte[] ReplayCode => [.. Replays.SelectMany(r => r.Bytes)];

    public IEnumerable<string> Problems()
    {
        if (!ReachesExit) yield return "the first-visit path doesn't reach the exit";
        if (ExitKind is not (SceneExitKind.Warp or SceneExitKind.Battle)) yield return $"the exit doesn't leave the room ({ExitWhat})";
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
    /// <summary>Calls that end the skip's usefulness or can't be skipped: battles, choices, menus, and room changes.</summary>
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

    private const int MaxDepth = 3, MaxSteps = 20000;

    /// <summary>
    /// Audits skipping from <paramref name="start"/> to <paramref name="exit"/> in <paramref name="worker"/>'s code with
    /// GameMoment = <paramref name="moment"/>. The exit path is followed from <paramref name="exit"/> to see how the scene
    /// leaves; replays the exit path repeats are downgraded to OK.
    /// </summary>
    public static SceneAuditResult Audit(ScriptTables tables, int worker, int start, int exit, int moment)
    {
        var skipped = new List<SkippedStatement>();
        var notes = new List<string>();
        var runs = new HashSet<(int, int)>();
        // Everything from the exit to the end of its function runs on the skip path too, so reaching any of it counts as
        // reaching the exit.
        int exitRegionEnd = Enumerable.Range(0, tables.WorkerCount)
            .SelectMany(w => Enumerable.Range(0, tables.Editor.FunctionCount(w)).Select(f => tables.Editor.FunctionEntry(w, f)))
            .Where(e => e > exit).DefaultIfEmpty(tables.Code.Length).Min();
        var path = Walk(tables, worker, start, exit, moment, depth: 0, skipped, notes, runs, out bool reached, out _, exitRegionEnd, (worker, exit, exitRegionEnd));
        // A run function reached twice (a loop, or both sides of a branch) is listed once.
        skipped = skipped.DistinctBy(s => (s.Worker, s.Offset, s.What)).ToList();
        var exitPath = Walk(tables, worker, exit, stopAt: -1, MomentAfter(tables, path, moment), depth: MaxDepth, [], [], [], out _, out var end);
        var (kind, what) = end;

        // Music the scene changes on its way out doesn't matter when the exit changes room: the next room sets its own.
        if (kind is SceneExitKind.Warp or SceneExitKind.Battle)
            for (int i = 0; i < skipped.Count; i++)
                if (MusicCalls.Contains(skipped[i].What.Split(' ')[0]) && skipped[i].Effect != SkipEffect.Ok)
                    skipped[i] = skipped[i] with { Effect = SkipEffect.Ok, What = skipped[i].What + " (music; the next room sets its own)" };

        // A replay the exit path runs anyway needn't be replayed.
        var exitStatements = exitPath.Select(s => Convert.ToHexString(tables.Code.Slice(s.Offset, s.Length))).ToHashSet();
        for (int i = 0; i < skipped.Count; i++)
            if (skipped[i].Effect == SkipEffect.Replay && exitStatements.Contains(Convert.ToHexString(skipped[i].Bytes)))
                skipped[i] = skipped[i] with { Effect = SkipEffect.Ok, What = skipped[i].What + " (the exit does it too)" };
        // A GameMoment write is only needed if the exit doesn't set GameMoment itself.
        if (exitPath.Any(s => AtelCode.MomentWrite(s, tables) is not null))
            for (int i = 0; i < skipped.Count; i++)
                if (skipped[i].Effect == SkipEffect.Replay && skipped[i].What.StartsWith("Set GameMoment", StringComparison.Ordinal))
                    skipped[i] = skipped[i] with { Effect = SkipEffect.Ok, What = skipped[i].What + " (the exit sets GameMoment)" };
        return new SceneAuditResult(skipped, kind, what, reached, notes, [.. runs]);
    }

    private static int MomentAfter(ScriptTables tables, List<AtelStatement> path, int moment)
    {
        foreach (var s in path) if (AtelCode.MomentWrite(s, tables) is int m) moment = m;
        return moment;
    }

    /// <summary>
    /// Follows code from <paramref name="start"/> until <paramref name="stopAt"/> (reached = true), a return or a warp,
    /// evaluating GameMoment branches and following both sides of anything else. Returns the statements of
    /// <paramref name="worker"/>'s own path in order (functions run on other workers are audited into
    /// <paramref name="skipped"/> but not returned).
    /// </summary>
    public static List<AtelStatement> Walk(ScriptTables tables, int worker, int start, int stopAt, int moment, int depth,
        List<SkippedStatement> skipped, List<string> notes, HashSet<(int, int)> runs, out bool reached, out (SceneExitKind Kind, string What) end,
        int stopRegionEnd = -1, (int Worker, int Exit, int End)? sceneExit = null)
    {
        var code = tables.Code;
        var path = new List<AtelStatement>();
        var seen = new HashSet<(int, int)>();
        var queue = new Queue<(int At, int Moment)>();
        // Code that only runs on one side of a branch the audit can't decide: from the branch to its forward target
        // (taken as the join). A replay there might not have happened, so it can't be replayed blindly.
        var undecided = new List<(int From, int To)>();
        queue.Enqueue((start, moment));
        reached = false;
        end = (SceneExitKind.Unknown, "no end found");
        int steps = 0;
        while (queue.Count > 0 && steps++ < MaxSteps)
        {
            var (at, gm) = queue.Dequeue();
            while (true)
            {
                if (at == stopAt || (stopAt >= 0 && at > stopAt && at < stopRegionEnd)) { reached = true; break; }
                if (!seen.Add((at, gm))) break;
                if (at < 0 || at >= code.Length || AtelCode.StatementAt(code, at) is not { } s) { notes.Add($"w{worker:X2}: code runs off at {at:X4}"); break; }
                path.Add(s);
                int before = skipped.Count;
                Classify(tables, worker, s, gm, depth, skipped, notes, runs, ref end, sceneExit);
                if (undecided.Any(r => s.Offset >= r.From && s.Offset < r.To))
                    for (int i = before; i < skipped.Count; i++)
                        if (skipped[i].Effect == SkipEffect.Replay)
                            skipped[i] = skipped[i] with { Effect = SkipEffect.Block, What = skipped[i].What + " (on a branch the audit can't decide)" };
                if (AtelCode.MomentWrite(s, tables) is int written) gm = written;
                var last = s.Last;
                if (AtelCode.IsReturn(last.Op)) { if (end.Kind == SceneExitKind.Unknown) end = (SceneExitKind.SameRoom, "returns in the room"); break; }
                if (end.Kind is SceneExitKind.Warp or SceneExitKind.Battle && stopAt < 0) { queue.Clear(); break; }
                if (last.Op == AtelCode.Jmp || last.Op == AtelCode.PopXJmp) { at = Target(tables, worker, last.Operand); continue; }
                if (last.Op is AtelCode.PopXCJmp or AtelCode.PopXNCJmp)
                {
                    bool? value = AtelCode.EvaluateMomentCondition(s, gm, tables);
                    int target = Target(tables, worker, last.Operand);
                    bool jumpsWhen = last.Op == AtelCode.PopXCJmp;
                    if (value is bool v) { at = v == jumpsWhen ? target : s.End; continue; }
                    queue.Enqueue((target, gm));
                    if (target > s.End) undecided.Add((s.End, target));
                    at = s.End;
                    continue;
                }
                if (last.Op is AtelCode.CJmp or AtelCode.NCJmp) { queue.Enqueue((Target(tables, worker, last.Operand), gm)); at = s.End; continue; }
                at = s.End;
            }
        }
        return path;
    }

    private static int Target(ScriptTables tables, int worker, int jump) =>
        jump < tables.Editor.JumpCount(worker) ? tables.Editor.JumpTarget(worker, jump) : -1;

    private static void Classify(ScriptTables tables, int worker, AtelStatement s, int gm, int depth, List<SkippedStatement> skipped,
        List<string> notes, HashSet<(int, int)> runs, ref (SceneExitKind Kind, string What) end, (int Worker, int Exit, int End)? sceneExit)
    {
        byte[] Bytes() => tables.Code.Slice(s.Offset, s.Length).ToArray();
        void Add(string what, SkipEffect effect)
        {
            if (effect == SkipEffect.Replay && !Replayable(s)) { effect = SkipEffect.Block; what += " (can't be replayed: computed operands)"; }
            skipped.Add(new SkippedStatement(worker, s.Offset, what, effect, Bytes()));
        }

        foreach (var ins in s.Instructions.Where(i => i.Op is AtelCode.Call or AtelCode.CallPopA))
        {
            if (WarpCalls.Contains(ins.Operand)) end = (SceneExitKind.Warp, $"{BlockCalls[ins.Operand]} at w{worker:X2} {s.Offset:X4}");
            else if (BattleCalls.Contains(ins.Operand)) end = (SceneExitKind.Battle, $"{BlockCalls[ins.Operand]} at w{worker:X2} {s.Offset:X4}");
            if (BlockCalls.TryGetValue(ins.Operand, out string? block)) Add(block, SkipEffect.Block);
            else if (ReplayCalls.TryGetValue(ins.Operand, out string? replay)) Add(replay, SkipEffect.Replay);
        }

        var last = s.Last;
        if (last.Op is AtelCode.PopV or AtelCode.PopVL or AtelCode.PopAr or AtelCode.PopArL)
        {
            int location = tables.VariableLocation(last.Operand);
            if (AtelCode.MomentWrite(s, tables) is int m) Add($"Set GameMoment = {m}", SkipEffect.Replay);
            else if (location == 0) Add($"save-data variable {last.Operand:X2} written", SkipEffect.Replay);
        }
        if (last.Op == AtelCode.Jsr) notes.Add($"w{worker:X2} {s.Offset:X4}: jumps to subroutine worker {last.Operand:X2} (not followed)");

        if (s.Instructions.Any(i => AtelCode.IsRun(i.Op)))
        {
            if (AtelCode.RunTarget(s, tables) is { } run)
            {
                runs.Add((run.Worker, run.Function));
                if (depth < MaxDepth && run.Worker >= 0 && run.Worker < tables.WorkerCount && run.Function < tables.Editor.FunctionCount(run.Worker))
                {
                    int entry = tables.Editor.FunctionEntry(run.Worker, run.Function);
                    var inner = new List<SkippedStatement>();
                    var ignored = (SceneExitKind.Unknown, "");
                    // A function run on the scene's own worker stops at the exit too (the game's own skip function jumps there).
                    bool own = sceneExit is { } x && x.Worker == run.Worker;
                    Walk(tables, run.Worker, entry, own ? sceneExit!.Value.Exit : -1, gm, depth + 1, inner, notes, runs, out _, out ignored, own ? sceneExit!.Value.End : -1, sceneExit);
                    // What a run function does to a room only matters if it leaves it; anything else carries over.
                    skipped.AddRange(inner);
                }
            }
            else notes.Add($"w{worker:X2} {s.Offset:X4}: runs a worker function chosen at run time (not followed)");
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
