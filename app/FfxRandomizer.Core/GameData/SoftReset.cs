using System.Text.RegularExpressions;
using FfxRandomizer.Core.GameData.Events;
using FfxRandomizer.Core.Vbf;

namespace FfxRandomizer.Core.GameData;

/// <summary>
/// Soft reset: holding L1 + R1 + Start for about a second in the field returns to the title screen, the way the game-over
/// screen does (fade to black over 15 frames, then change room to 23, the title, at spawn 0).
/// <para>
/// The game has no script that stays loaded across rooms, so every field event gets the watcher. It replaces the main
/// function of an idle worker: one whose main only halts and that nothing else uses (its talk/scout/cross/touch handlers
/// just return and no script runs its functions). The watcher runs where the halt did, at the main's own level, so it
/// never holds up anything else in the event.
/// </para>
/// </summary>
public static partial class SoftReset
{
    /// <summary>
    /// The combos offered: button ids joined by "+" in the skip list's order (see <see cref="ComboId"/>), the first the
    /// default. A recorded one may be any 1-3 buttons.
    /// </summary>
    public static IReadOnlyList<string> Combos { get; } = ["l1+r1+start", "select+l1+r1", "select+l2+r2", "select+start"];

    public const int MaxComboButtons = 3;

    /// <summary>The buttons of a combo id; the default combo's for an id that isn't 1-3 known, different buttons.</summary>
    public static IReadOnlyList<SkipButton> ComboButtons(string? combo)
    {
        var buttons = (combo ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries)
            .Select(id => SkipButtons.All.FirstOrDefault(b => b.Id == id)).ToList();
        if (buttons.Count is 0 or > MaxComboButtons || buttons.Any(b => b is null) || buttons.Distinct().Count() != buttons.Count)
            return ComboButtons(Combos[0]);
        return buttons!;
    }

    /// <summary>A combo id for buttons, in the skip list's order so the same buttons always make the same id.</summary>
    public static string ComboId(IEnumerable<SkipButton> buttons) =>
        string.Join("+", SkipButtons.All.Where(b => buttons.Any(x => x.Id == b.Id)).Select(b => b.Id));

    /// <summary>Whether an id names 1-3 different known buttons, written as <see cref="ComboId"/> writes them.</summary>
    public static bool IsCombo(string id)
    {
        var buttons = id.Split('+').Select(b => SkipButtons.All.FirstOrDefault(x => x.Id == b)).ToList();
        return buttons.Count is >= 1 and <= MaxComboButtons && buttons.All(b => b is not null) && ComboId(buttons!) == id;
    }

    /// <summary>A combo's game button numbers (the default combo's for null or an invalid id).</summary>
    public static int[] ComboIndexes(string? combo) => [.. ComboButtons(combo).Select(b => b.Index)];

    public static string ComboLabel(string? combo) => string.Join(" + ", ComboButtons(combo).Select(b => b.Label));

    /// <summary>
    /// A warning when holding the combo during a cutscene would also fill the cutscene skip's bar: hold-to-skip is on and
    /// its button is in the combo.
    /// </summary>
    public static string? OverlapWarning(string? combo, string cutsceneMode, SkipButton skipButton) =>
        cutsceneMode == Cutscenes.Hold && ComboButtons(combo).Any(b => b.Id == skipButton.Id)
            ? $"{skipButton.Label} is also the cutscene skip button, so holding this combo during a scene may skip the scene before it resets."
            : null;

    public const int PollFrames = 2, HoldFrames = 60, FadeFrames = 15, TitleRoom = 23, TitleSpawn = 0;
    public const int SetRoomFadeoutTime = 0x00BB, SetRoomFadeoutMode = 0x00BC, TransitionToRoom = 0x0011, FadeBlack = 0;
    private const int Halt = 0x005F, SetWorkerAsPlayer = 0x0043, MainFunction = 1, Scenario = 4, FieldObject = 1;

    /// <summary>Calls that take a worker and could hang on, or switch off, a worker that never goes idle: waitForWorker, deactivate, activate.</summary>
    private static readonly HashSet<int> WorkerCalls = [0x00F6, 0x00A7, 0x00A8];

    /// <summary>The field events: rooms named like "bsil0300"; not the test, debug, sample or Chinese/Vita copies.</summary>
    public static IEnumerable<string> FieldEvents(VbfArchive archive) =>
        archive.Names
            .Where(n => n.StartsWith(GamePaths.Master + "jppc/event/obj/", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".ebp", StringComparison.OrdinalIgnoreCase))
            .Select(n => Path.GetFileNameWithoutExtension(n))
            .Where(IsFieldEvent)
            .Distinct()
            .Order(StringComparer.Ordinal);

    [GeneratedRegex("^[a-z]{4}[0-9]{4}$")]
    private static partial Regex FieldName();

    /// <summary>
    /// The worker to host the watcher in an event script, or null and why not. <paramref name="busy"/> are workers the
    /// cutscene skip uses there.
    /// </summary>
    public static (int? Worker, string Why) HostFor(byte[] scriptChunk, IReadOnlySet<int> busy)
    {
        var tables = new ScriptTables(scriptChunk);
        var editor = tables.Editor;
        byte[] code = tables.Code.ToArray();
        var statements = new List<AtelStatement>();
        for (int at = 0; at < code.Length && AtelCode.StatementAt(code, at) is { } s; at = s.End) statements.Add(s);
        var runTargets = new HashSet<int>();
        bool computedRuns = false;
        foreach (var s in statements)
        {
            if (!s.Instructions.Any(i => AtelCode.IsRun(i.Op))) continue;
            if (AtelCode.RunTarget(s, tables) is { } run) runTargets.Add(run.Worker);
            else computedRuns = true;
        }
        // Function entries anywhere that share code with a candidate's main would be changed by nothing (the main is
        // repointed, not edited), so only the main's own code matters.
        bool Halts(int w)
        {
            if (editor.FunctionCount(w) <= MainFunction) return false;
            int entry = editor.FunctionEntry(w, MainFunction);
            return entry + 4 <= code.Length && code[entry] == AtelCode.CallPopA && code[entry + 1] == (Halt & 0xFF) && code[entry + 2] == Halt >> 8
                && AtelCode.IsReturn(code[entry + 3]);
        }
        bool Unused(int w)
        {
            for (int f = MainFunction + 1; f < editor.FunctionCount(w); f++)
                if (!AtelCode.IsReturn(code[editor.FunctionEntry(w, f)])) return false;
            return true;
        }
        // A Scenario (in-game, a FieldObject's main got no turns during cutscenes; a Scenario's did), not the player.
        var players = statements.Where(s => s.Instructions.Any(i => i.Op == AtelCode.CallPopA && i.Operand == SetWorkerAsPlayer))
            .Select(s => tables.FunctionAt(s.Offset)?.Worker ?? -1).ToHashSet();
        var candidates = Enumerable.Range(0, editor.WorkerCount)
            .Where(w => !busy.Contains(w) && tables.WorkerType(w) == Scenario && !players.Contains(w) && Halts(w) && !Named(w)).ToList();
        if (candidates.Count == 0) return (null, "no Scenario worker whose main only halts");
        // Best: nothing uses the worker at all. Next: only the engine does (it signals talk/cross/touch at levels above
        // main, as the scripts' own runs are: none of them uses level 0). Never one a script runs or names, which could
        // wait for the worker to finish.
        var idle = candidates.Where(w => Unused(w) && !runTargets.Contains(w)).ToList();
        var engineOnly = candidates.Where(w => !runTargets.Contains(w)).ToList();
        if (computedRuns)
        {
            // A run with a computed worker could reach any of them; prefer ones with no functions past main.
            var bare = idle.Where(w => editor.FunctionCount(w) == MainFunction + 1).ToList();
            if (bare.Count > 0) return (bare[0], "idle (no functions past main)");
        }
        if (idle.Count > 0) return (idle[0], "idle");
        if (engineOnly.Count > 0) return (engineOnly[0], "the engine signals it (talk/scout/cross/touch)");
        return (null, "every Scenario worker whose main only halts is run by the event's scripts");

        // Named, or possibly named (a computed worker), by a call that waits for, stops or starts a worker.
        bool Named(int w) => statements.Any(s =>
        {
            var ins = s.Instructions;
            for (int i = 1; i < ins.Count; i++)
                if (ins[i].Op is AtelCode.CallPopA or AtelCode.Call && WorkerCalls.Contains(ins[i].Operand)
                    && (AtelCode.Constant(ins[i - 1], tables) is not int named || named == w))
                    return true;
            return false;
        });
    }

    /// <summary>
    /// The watcher, looping forever: poll every <see cref="PollFrames"/> frames until all the combo's buttons (game button
    /// numbers) are down, then keep polling for <see cref="HoldFrames"/> frames (any release starts over), then fade and
    /// go to the title.
    /// </summary>
    public static byte[] WatcherCode(int loopJump, IReadOnlyList<int> buttons)
    {
        var allDown = new List<byte>(Atel.IsDown(buttons[0]));
        foreach (int button in buttons.Skip(1)) allDown.AddRange([.. Atel.IsDown(button), Atel.LogicalAnd]);
        byte[] poll = [.. Atel.Call(Atel.Wait, PollFrames), .. allDown, .. Atel.GotoUnless(loopJump)];
        var code = new List<byte>();
        for (int i = 0; i <= HoldFrames / PollFrames; i++) code.AddRange(poll);
        code.AddRange(Atel.Call(SetRoomFadeoutTime, FadeFrames));
        code.AddRange(Atel.Call(SetRoomFadeoutMode, FadeBlack));
        code.AddRange(Atel.Call(TransitionToRoom, TitleRoom, TitleSpawn));
        // Then stop, as the game-over screen does after its own transition, so a held combo can't start it again.
        code.AddRange(Atel.Call(Halt));
        code.Add(Atel.Return);
        return [.. code];
    }

    /// <summary>
    /// Adds the watcher to an event file (original or already edited by other features); returns the new bytes, or null
    /// and why it was left alone.
    /// </summary>
    public static (byte[]? Bytes, string? Reason) Apply(byte[] ebp, IReadOnlySet<int> busy, IReadOnlyList<int> buttons)
    {
        var file = EventFile.Read(ebp);
        byte[] chunk = file.Chunk(EventFile.ScriptChunk)!;
        var script = new EventScriptEditor(chunk);
        var (host, reason) = HostFor(chunk, busy);
        if (host is int w)
        {
            // An unused Scenario worker: its main becomes the watcher.
            int loop = script.JumpCount(w);
            int start = script.AppendCode(WatcherCode(loop, buttons));
            script.AddJump(w, start);
            script.SetFunctionEntry(w, MainFunction, start);
        }
        else
        {
            // Otherwise a Scenario worker of its own, after the last one (workers are ordered by type), with the
            // workers after it renumbered wherever the code names them.
            var tables = new ScriptTables(chunk);
            var (index, patches, risk) = NewWorkerPlan(tables);
            if (risk is not null) return (null, $"{reason}; can't add a worker: {risk}");
            foreach (var (offset, op, operand) in patches) script.SetOperand(offset, op, operand);
            int init = script.AppendCode([Atel.Return]);
            int main = script.AppendCode(WatcherCode(0, buttons));
            script.AddWorker(index, Scenario, [init, main], [main]);
        }
        file.SetChunk(EventFile.ScriptChunk, script.ToBytes());
        byte[] bytes = file.ToBytes();
        if (VbfArchive.BlocksFor((ulong)bytes.Length) != VbfArchive.BlocksFor((ulong)ebp.Length))
            return (null, $"doesn't fit: {bytes.Length - ebp.Length} more bytes would cross a 64 KB block");
        return (bytes, null);
    }

    /// <summary>
    /// Calls that take a worker, with how many arguments they take and which are workers (from FFXDataParser's call
    /// list). getWorkerIndex (0x0033) takes a worker too; its result is the worker's number at run time, so comparing it
    /// with other workers' numbers still works after renumbering (which moves every later worker up by the same one).
    /// </summary>
    private static readonly Dictionary<int, (int Arguments, int[] Workers)> WorkerArgumentCalls = new()
    {
        [0x0018] = (3, [2]), [0x0019] = (3, [2]), [0x0033] = (1, [0]), [0x0036] = (1, [0]), [0x0038] = (1, [0]), [0x0039] = (1, [0]), [0x003A] = (1, [0]),
        [0x0077] = (1, [0]), [0x0078] = (1, [0]), [0x0092] = (1, [0]), [0x00A3] = (4, [0]), [0x00A4] = (2, [0, 1]), [0x00A5] = (2, [0, 1]),
        [0x00A7] = (1, [0]), [0x00A8] = (1, [0]), [0x00F6] = (2, [1]), [0x0128] = (2, [0]), [0x0132] = (2, [0]), [0x0133] = (2, [0]),
        [0x01BA] = (2, [0]), [0x502D] = (2, [0]), [0x5031] = (1, [0]), [0x5043] = (6, [0]), [0x5044] = (6, [0]), [0x5045] = (6, [0]),
        [0x5046] = (6, [0]), [0x5047] = (2, [0]), [0x5048] = (2, [0]), [0x5049] = (1, [0]), [0x504A] = (1, [0]), [0x504B] = (1, [0]),
        [0x504C] = (2, [0]), [0x5057] = (2, [0]), [0x5075] = (1, [0]), [0x507A] = (1, [0]), [0x508A] = (6, [0]), [0x508C] = (6, [0]),
    };


    /// <summary>
    /// Where a new Scenario worker goes (after the last FieldObject or Scenario; every event orders its workers by type)
    /// and the code operands to change so references to the workers after it still reach them: subroutine jumps, run
    /// targets and worker arguments, each a constant. Or why that can't be done safely: a worker reference that isn't a
    /// constant.
    /// </summary>
    public static (int Index, List<(int Offset, byte Op, int Operand)> Patches, string? Risk) NewWorkerPlan(ScriptTables tables)
    {
        var patches = new List<(int, byte, int)>();
        int index = 0;
        while (index < tables.WorkerCount && tables.WorkerType(index) is FieldObject or Scenario) index++;
        for (int w = index; w < tables.WorkerCount; w++)
            if (tables.WorkerType(w) is FieldObject or Scenario) return (index, patches, "its workers aren't ordered by type");

        byte[] code = tables.Code.ToArray();
        for (int at = 0; at < code.Length && AtelCode.StatementAt(code, at) is { } s; at = s.End)
        {
            var ins = s.Instructions;
            for (int i = 0; i < ins.Count; i++)
            {
                var x = ins[i];
                if (x.Op == AtelCode.Jsr)
                {
                    if (x.Operand >= tables.WorkerCount) return (index, patches, $"a subroutine jump at {x.Offset:X4} names worker {x.Operand}, which doesn't exist");
                    if (x.Operand >= index) patches.Add((x.Offset, AtelCode.Jsr, x.Operand + 1));
                }
                else if (AtelCode.IsRun(x.Op))
                {
                    if (i < 3 || AtelCode.Constant(ins[i - 2], tables) is not int worker) return (index, patches, $"a run at {x.Offset:X4} names its worker with a computed value");
                    if (worker >= tables.WorkerCount) return (index, patches, $"a run at {x.Offset:X4} names worker {worker}, which doesn't exist");
                    if (worker >= index) patches.Add((ins[i - 2].Offset, AtelCode.PushII, worker + 1));
                }
                else if (x.Op is AtelCode.CallPopA or AtelCode.Call)
                {
                    if (!WorkerArgumentCalls.TryGetValue(x.Operand, out var call)) continue;
                    // Arguments, last first: each a plain push, or getWorkerIndex(plain push) (whose own argument is
                    // handled where that call is).
                    var arguments = new List<(AtelInstruction First, bool WorkerIndex)>();
                    for (int a = i - 1; arguments.Count < call.Arguments && a >= 0; a--)
                    {
                        if (ins[a].Op is AtelCode.Call && ins[a].Operand == 0x0033 && a >= 1 && IsPush(ins[a - 1])) { arguments.Add((ins[a - 1], true)); a--; }
                        else if (IsPush(ins[a])) arguments.Add((ins[a], false));
                        else break;
                    }
                    if (arguments.Count < call.Arguments) return (index, patches, $"call {x.Operand:X4} at {x.Offset:X4} has a computed argument");
                    arguments.Reverse();
                    foreach (int p in call.Workers)
                    {
                        var (arg, viaIndex) = arguments[p];
                        if (viaIndex) continue;
                        if (AtelCode.Constant(arg, tables) is not int worker) return (index, patches, $"call {x.Operand:X4} at {x.Offset:X4} takes a worker from a variable");
                        // Below 0: the calling worker itself.
                        if (worker >= tables.WorkerCount) return (index, patches, $"call {x.Operand:X4} at {x.Offset:X4} takes {worker}, not a worker number");
                        if (worker >= index) patches.Add((arg.Offset, AtelCode.PushII, worker + 1));
                    }
                }
            }
        }
        return (index, patches, null);
    }

    /// <summary>
    /// Checks an event the watcher was added to as a worker of its own, without trusting the editor or the plan: one more
    /// worker, a Scenario whose init returns and whose main is the watcher (both appended after the code); every other
    /// worker the same, those after it one place up; the code the same except operands that name a worker after it, each
    /// now one more; the header's counts one more; the shared data and the other chunks unchanged.
    /// </summary>
    public static IEnumerable<string> CheckOwnWorker(byte[] beforeEbp, byte[] afterEbp, IReadOnlyList<int> buttons)
    {
        var beforeFile = EventFile.Read(beforeEbp);
        var afterFile = EventFile.Read(afterEbp);
        if (beforeFile.ChunkCount != afterFile.ChunkCount) { yield return "the number of chunks changed."; yield break; }
        for (int i = 0; i < beforeFile.ChunkCount; i++)
            if (i != EventFile.ScriptChunk && !(beforeFile.Chunk(i) ?? []).AsSpan().SequenceEqual(afterFile.Chunk(i) ?? []))
                yield return $"chunk {i} changed.";
        byte[] bChunk = beforeFile.Chunk(EventFile.ScriptChunk)!, aChunk = afterFile.Chunk(EventFile.ScriptChunk)!;
        var b = new ScriptTables(bChunk);
        var a = new ScriptTables(aChunk);
        if (a.WorkerCount != b.WorkerCount + 1) { yield return "the worker count isn't one more."; yield break; }

        int codeLength = b.Editor.CodeLength, init = codeLength, main = codeLength + 1;
        byte[] watcher = WatcherCode(0, buttons);
        int k = Enumerable.Range(0, a.WorkerCount).FirstOrDefault(w => a.WorkerType(w) == Scenario && a.Editor.FunctionCount(w) == 2
            && a.Editor.JumpCount(w) == 1 && a.Editor.FunctionEntry(w, 0) == init && a.Editor.FunctionEntry(w, 1) == main && a.Editor.JumpTarget(w, 0) == main, -1);
        if (k < 0) { yield return "no added Scenario worker with the watcher as its main."; yield break; }
        if (a.Editor.CodeLength != codeLength + 1 + watcher.Length || a.Code[init] != Atel.Return || !a.Code.Slice(main, watcher.Length).SequenceEqual(watcher))
            yield return "the code doesn't end with the added worker's init and the watcher.";

        for (int w = 0; w < b.WorkerCount; w++)
        {
            int n = w < k ? w : w + 1;
            if (a.WorkerType(n) != b.WorkerType(w) || a.Editor.FunctionCount(n) != b.Editor.FunctionCount(w) || a.Editor.JumpCount(n) != b.Editor.JumpCount(w))
            { yield return $"w{w:X2} isn't w{n:X2} with the same type, functions and jumps."; continue; }
            for (int f = 0; f < b.Editor.FunctionCount(w); f++)
                if (a.Editor.FunctionEntry(n, f) != b.Editor.FunctionEntry(w, f)) yield return $"w{w:X2}::f{f:X2} moved.";
            for (int j = 0; j < b.Editor.JumpCount(w); j++)
                if (a.Editor.JumpTarget(n, j) != b.Editor.JumpTarget(w, j)) yield return $"w{w:X2} j{j:X2} moved.";
        }

        // Instruction by instruction through the original code: the same, or a worker number after the new one, plus one.
        byte[] bc = b.Code.ToArray(), ac = a.Code.ToArray();
        for (int at = 0; at < codeLength; at += bc[at] >= 0x80 ? 3 : 1)
        {
            int length = bc[at] >= 0x80 ? 3 : 1;
            if (bc.AsSpan(at, length).SequenceEqual(ac.AsSpan(at, length))) continue;
            var was = AtelCode.At(bc, at);
            var now = AtelCode.At(ac, at);
            int? value = was.Op == AtelCode.Jsr ? was.Operand : AtelCode.Constant(was, b);
            bool renumbered = value is int v && v >= k && v < b.WorkerCount && now.Operand == v + 1
                && (was.Op == AtelCode.Jsr ? now.Op == AtelCode.Jsr : was.Op is AtelCode.PushII or AtelCode.PushI && now.Op == AtelCode.PushII);
            if (!renumbered) yield return $"code at {at:X4} changed ({Convert.ToHexString(bc, at, length)} -> {Convert.ToHexString(ac, at, length)}), not a worker renumbered.";
        }

        // And every reference that needed renumbering got it: each subroutine jump to a later worker, and every operand
        // the plan names (run targets and worker arguments).
        for (int at = 0; at < codeLength; at += bc[at] >= 0x80 ? 3 : 1)
            if (bc[at] == AtelCode.Jsr && AtelCode.At(bc, at).Operand >= k && AtelCode.At(ac, at).Operand != AtelCode.At(bc, at).Operand + 1)
                yield return $"the subroutine jump at {at:X4} wasn't renumbered.";
        var plan = NewWorkerPlan(b);
        if (plan.Risk is not null || plan.Index != k) yield return $"the event can't be renumbered for a worker at {k:X2} ({plan.Risk}).";
        else
            foreach (var (offset, op, operand) in plan.Patches)
                if (AtelCode.At(ac, offset) is var now && (now.Op != op || now.Operand != operand))
                    yield return $"the worker reference at {offset:X4} wasn't renumbered.";

        static int U16(byte[] s, int at) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan(at));
        static int U32(byte[] s, int at) => (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(at));
        if (U16(aChunk, 0x16) != U16(bChunk, 0x16) + 1 || U16(aChunk, 0x36) != U16(bChunk, 0x36) + 1)
            yield return "the header's Scenario and actor counts aren't one more.";
        if (U16(aChunk, 0x14) != U16(bChunk, 0x14) || U16(aChunk, 0x1C) != U16(bChunk, 0x1C))
            yield return "the header's edge counts changed.";
        int mainScript = U16(bChunk, 0x18);
        if (U16(aChunk, 0x18) != (mainScript >= k && mainScript != 0xFFFF ? mainScript + 1 : mainScript)) yield return $"the header's main script index isn't renumbered ({mainScript} -> {U16(aChunk, 0x18)}, new worker {k}, {b.WorkerCount} workers).";
        // The data after the code, up to the meta struct: the same, except pointers (aligned words) into the chunk,
        // which move with it.
        int bShared = b.Editor.SharedDataStart, aShared = a.Editor.SharedDataStart, delta = aShared - bShared;
        int bMeta = U32(bChunk, 0x2C);
        int sharedLength = (bMeta > bShared ? bMeta : b.Editor.WorkerTablesStart) - bShared;
        for (int i = 0; i < sharedLength; i += 4)
        {
            int was = U32(bChunk, bShared + i), now = U32(aChunk, aShared + i);
            if (was != now && !(was >= bShared && was < bChunk.Length && now == was + delta && (bShared + i) % 4 == 0))
            {
                yield return $"the data after the code changed at +{i:X}.";
                break;
            }
        }
    }

    /// <summary>
    /// For checking: an event with its worker references renumbered as adding a worker of its own would (but no worker
    /// added), and where that worker goes; null when the event gets the watcher in an existing worker or not at all.
    /// </summary>
    public static (byte[] Bytes, int Index)? RenumberedOnly(byte[] ebp, IReadOnlySet<int> busy)
    {
        var file = EventFile.Read(ebp);
        byte[] chunk = file.Chunk(EventFile.ScriptChunk)!;
        if (HostFor(chunk, busy).Worker is not null) return null;
        var (index, patches, risk) = NewWorkerPlan(new ScriptTables(chunk));
        if (risk is not null) return null;
        var script = new EventScriptEditor(chunk);
        foreach (var (offset, op, operand) in patches) script.SetOperand(offset, op, operand);
        file.SetChunk(EventFile.ScriptChunk, script.ToBytes());
        return (file.ToBytes(), index);
    }

    private static bool IsPush(AtelInstruction i) => i.Op is AtelCode.PushII or AtelCode.PushI or AtelCode.PushF or AtelCode.PushV;

    public static bool IsFieldEvent(string eventId) => FieldName().IsMatch(eventId);

    /// <summary>
    /// An event file (at <paramref name="path"/>, as other features left it) with the watcher added, exactly as the soft
    /// reset feature adds it; unchanged for events it leaves alone.
    /// </summary>
    public static byte[] WithWatcher(string path, byte[] ebp, IReadOnlyList<int> buttons)
    {
        string id = Path.GetFileNameWithoutExtension(path);
        if (!IsFieldEvent(id) || !path.Equals(Cutscenes.EventPath(id), StringComparison.OrdinalIgnoreCase)) return ebp;
        return Apply(ebp, CutsceneWorkers(id), buttons).Bytes ?? ebp;
    }

    /// <summary>Workers the cutscene skip uses in an event (whatever mode it runs in).</summary>
    public static IReadOnlySet<int> CutsceneWorkers(string eventId) =>
        Cutscenes.All.Where(s => s.EventId.Equals(eventId, StringComparison.OrdinalIgnoreCase))
            .SelectMany(s => new[] { s.SceneWorker, s.Exit.Worker, s.HostWorker, s.BuiltIn?.Worker ?? -1 })
            .Where(w => w >= 0).ToHashSet();
}
