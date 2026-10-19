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
    /// <summary>L1, R1 and Start, as the 0x004C "button is down" test numbers them.</summary>
    public static readonly int[] Buttons = [2, 3, 11];

    public const int PollFrames = 2, HoldFrames = 60, FadeFrames = 15, TitleRoom = 23, TitleSpawn = 0;
    public const int SetRoomFadeoutTime = 0x00BB, SetRoomFadeoutMode = 0x00BC, TransitionToRoom = 0x0011, FadeBlack = 0;
    private const int Halt = 0x005F, MainFunction = 1;

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
    public static (int? Worker, string? Reason) HostFor(byte[] scriptChunk, IReadOnlySet<int> busy)
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
        var candidates = Enumerable.Range(0, editor.WorkerCount).Where(w => !busy.Contains(w) && Halts(w) && !Named(w)).ToList();
        if (candidates.Count == 0) return (null, "no worker whose main only halts");
        // Best: nothing uses the worker at all. Next: only the engine does (it signals talk/cross/touch at levels above
        // main, as the scripts' own runs are: none of them uses level 0). Never one a script runs or names, which could
        // wait for the worker to finish.
        var idle = candidates.Where(w => Unused(w) && !runTargets.Contains(w)).ToList();
        var engineOnly = candidates.Where(w => !runTargets.Contains(w)).ToList();
        if (computedRuns)
        {
            // A run with a computed worker could reach any of them; prefer ones with no functions past main.
            var bare = idle.Where(w => editor.FunctionCount(w) == MainFunction + 1).ToList();
            if (bare.Count > 0) return (bare[0], null);
        }
        if (idle.Count > 0) return (idle[0], null);
        if (engineOnly.Count > 0) return (engineOnly[0], null);
        return (null, "every worker whose main only halts is run by the event's scripts");

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
    /// The watcher, looping forever: poll every <see cref="PollFrames"/> frames until all three buttons are down, then
    /// keep polling for <see cref="HoldFrames"/> frames (any release starts over), then fade and go to the title.
    /// </summary>
    public static byte[] WatcherCode(int loopJump)
    {
        byte[] allDown = [.. Atel.IsDown(Buttons[0]), .. Atel.IsDown(Buttons[1]), Atel.LogicalAnd, .. Atel.IsDown(Buttons[2]), Atel.LogicalAnd];
        byte[] poll = [.. Atel.Call(Atel.Wait, PollFrames), .. allDown, .. Atel.GotoUnless(loopJump)];
        var code = new List<byte>();
        for (int i = 0; i <= HoldFrames / PollFrames; i++) code.AddRange(poll);
        code.AddRange(Atel.Call(SetRoomFadeoutTime, FadeFrames));
        code.AddRange(Atel.Call(SetRoomFadeoutMode, FadeBlack));
        code.AddRange(Atel.Call(TransitionToRoom, TitleRoom, TitleSpawn));
        code.AddRange(Atel.Goto(loopJump));
        return [.. code];
    }

    /// <summary>
    /// Adds the watcher to an event file (original or already edited by other features); returns the new bytes, or null
    /// and why it was left alone.
    /// </summary>
    public static (byte[]? Bytes, string? Reason) Apply(byte[] ebp, IReadOnlySet<int> busy)
    {
        var file = EventFile.Read(ebp);
        byte[] chunk = file.Chunk(EventFile.ScriptChunk)!;
        var (host, reason) = HostFor(chunk, busy);
        if (host is not int w) return (null, reason);
        var script = new EventScriptEditor(chunk);
        int loop = script.JumpCount(w);
        int start = script.AppendCode(WatcherCode(loop));
        script.AddJump(w, start);
        script.SetFunctionEntry(w, MainFunction, start);
        file.SetChunk(EventFile.ScriptChunk, script.ToBytes());
        byte[] bytes = file.ToBytes();
        if (VbfArchive.BlocksFor((ulong)bytes.Length) != VbfArchive.BlocksFor((ulong)ebp.Length))
            return (null, $"doesn't fit: {bytes.Length - ebp.Length} more bytes would cross a 64 KB block");
        return (bytes, null);
    }

    public static bool IsFieldEvent(string eventId) => FieldName().IsMatch(eventId);

    /// <summary>
    /// An event file (at <paramref name="path"/>, as other features left it) with the watcher added, exactly as the soft
    /// reset feature adds it; unchanged for events it leaves alone.
    /// </summary>
    public static byte[] WithWatcher(string path, byte[] ebp)
    {
        string id = Path.GetFileNameWithoutExtension(path);
        if (!IsFieldEvent(id) || !path.Equals(Cutscenes.EventPath(id), StringComparison.OrdinalIgnoreCase)) return ebp;
        return Apply(ebp, CutsceneWorkers(id)).Bytes ?? ebp;
    }

    /// <summary>Workers the cutscene skip uses in an event (whatever mode it runs in).</summary>
    public static IReadOnlySet<int> CutsceneWorkers(string eventId) =>
        Cutscenes.All.Where(s => s.EventId.Equals(eventId, StringComparison.OrdinalIgnoreCase))
            .SelectMany(s => new[] { s.SceneWorker, s.Exit.Worker, s.HostWorker, s.BuiltIn?.Worker ?? -1 })
            .Where(w => w >= 0).ToHashSet();
}
