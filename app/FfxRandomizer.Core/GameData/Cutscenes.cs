using FfxRandomizer.Core.GameData.Events;
using FfxRandomizer.Core.GameData.Text;
using FfxRandomizer.Core.Vbf;

namespace FfxRandomizer.Core.GameData;

/// <summary>A code offset in an event script and the bytes that must be there before it is edited.</summary>
public sealed record SceneCode(int Offset, byte[] Expected);

/// <summary>
/// Where a scene exits to: an existing jump label (<see cref="Jump"/> &gt;= 0), or a statement at
/// <see cref="CodeOffset"/> that gets a new label (<see cref="Jump"/> = -1), checked against <see cref="Expected"/>.
/// </summary>
public sealed record EventJump(int Worker, int Jump, int CodeOffset, byte[]? Expected = null);

/// <summary>
/// The game's own skip watcher for a scene, when it has one: <see cref="Worker"/>'s <see cref="WatchFunction"/> is
/// started by the scene at <see cref="Start"/> and polls a button; on a press it runs the scene worker's
/// <see cref="SkipFunction"/> at level 2, which preempts the scene. <see cref="StopFunction"/> (a bare direct return) is
/// run at level 2 to end the watcher when the scene finishes. The skip watcher reuses all three, starting it at the
/// scene's first instruction instead (the call at <see cref="Start"/> becomes no-ops), so the scene never has two.
/// </summary>
public sealed record BuiltInWatcher(
    int Worker, int WatchFunction, SceneCode Start, int StopFunction, SceneCode StopStart, int SkipFunction, SceneCode SkipStart, byte[] BeforeSkip);

/// <summary>
/// A cutscene that can be skipped. <see cref="SceneStart"/> is the scene function's first instruction; <see cref="Exit"/>
/// where its first-visit path leaves the scene (setting GameMoment and changing room); <see cref="MomentFrom"/> to
/// <see cref="MomentBelow"/> limits the skip to that first visit. Without a <see cref="BuiltIn"/> watcher, the watcher
/// and its stop function are added to <see cref="HostWorker"/> (a worker idle during the scene), a skip function to the
/// scene worker (run at <see cref="SkipLevel"/>, above the level the scene runs at), the watcher is started at the
/// scene's start when the moment matches and stopped at its exit. What the scene does that lasts (see
/// <see cref="Events.SceneAudit"/>) is replayed by the skip before it jumps to the exit.
/// </summary>
public sealed record SkippableScene(
    string Name, string Area, string EventId, int SceneWorker, SceneCode SceneStart, EventJump Exit, int MomentBelow,
    int MessageWindow, BuiltInWatcher? BuiltIn = null, int HostWorker = -1, int MomentFrom = 0, int SkipLevel = 2, bool StaysInRoom = false)
{
    // StaysInRoom: the scene returns control in the same room, so a skip partway through would leave its actors playing
    // on. Hold-to-skip is decided as the scene starts instead: holding the button then fills the bar before anything
    // moves, and a full bar jumps to the end.

    /// <summary>The GameMoment a jump-to-scene save holds so the scene plays.</summary>
    public int Trigger => MomentBelow - 1;
}

/// <summary>
/// The scenes "Skip cutscenes" covers, and how their files are edited. Jumping from a scene's start to its exit comes from
/// CSR2, the Fahrenheit cutscene remover (MIT, The Fahrenheit contributors, https://github.com/fahrenheit-crew/fh-mods-csr).
/// </summary>
public static partial class Cutscenes
{
    public const string Original = "orig", Hold = "hold", Always = "always";

    /// <summary>
    /// Holding skips after <see cref="HoldFrames"/> frames (60 a second). The button is checked every
    /// <see cref="PollFrames"/> frames, and the prompt's bar fills in <see cref="Steps"/> steps.
    /// </summary>
    public const int HoldFrames = 60, PollFrames = 2, Steps = 5;

    /// <summary>When the watcher starts it shows "Hold [button] to skip" this long, checking the button this often.</summary>
    public const int IntroFrames = 180, IntroPollFrames = 6;

    /// <summary>Strings each scene's text gains: the intro prompt and one per bar step (empty to full).</summary>
    public const int PromptCount = Steps + 2;

    // Where the prompt sits: top left, clear of the scene's own text (which uses window 0, low on the screen).
    private const int PromptX = 16, PromptY = 16;

    /// <summary>The {TIME:00} text control code the game's own button prompts start with: show the whole text at once.</summary>
    private static readonly byte[] ShowInstantly = [0x09, 0x30];

    /// <summary>Every scene the option covers: those played through in-game, then the generic ones (Cutscenes.Generic.cs).</summary>
    public static IReadOnlyList<SkippableScene> All => _all ??= [.. Confirmed, .. Generic];

    private static IReadOnlyList<SkippableScene>? _all;

    /// <summary>Scenes whose skip has been played through in-game.</summary>
    private static IReadOnlyList<SkippableScene> Confirmed { get; } =
    [
        // znkd0600: w0D::f02 plays the campfire scene on the first visit (GameMoment < 2) and the late-game return (2700).
        // Its first-visit exit j13 hides the title card, sets GameMoment = 2 and warps to the Zanarkand harbor.
        // The game already watches for Select here (w13::f02 -> w0D::f03, stopped by w13::f03 at j0A); see BuiltInWatcher.
        new("Zanarkand Ruins campfire (opening)", "Zanarkand", "znkd0600",
            SceneWorker: 0x0D,
            SceneStart: new(0x3DC4, [0xAE, 0x01, 0x00, 0xAE, 0x01, 0x00, 0xD8, 0x0D, 0x80]), // Map.setGfxVisibility(1, true)
            Exit: new(Worker: 0x0D, Jump: 0x13, CodeOffset: 0x4426),
            MomentBelow: 2,
            MessageWindow: 3, // the event only uses window 0
            BuiltIn: new(Worker: 0x13, WatchFunction: 2,
                Start: new(0x3EBA, Atel.Run(1, 0x13, 2, awaitEnd: false)), // after the title card, about 5 s in
                StopFunction: 3,
                StopStart: new(0x4C12, [Atel.DirectReturn]),
                SkipFunction: 3,
                SkipStart: new(0x437D, [0xAE, 0x1E, 0x00, 0xD8, 0x05, 0x40]), // SgEvent.fadeoutToBlack(30)
                BeforeSkip: Atel.Set(0x23, 1))), // eventVar0014 = true, as the game's watcher does

        // Found by `ffxr cutscenes find`, checked by SceneAudit and played through in-game. MomentFrom..MomentBelow keeps
        // late-game revisits vanilla.
        // CSR2 removes 1AAF6-1AED0 here; the skip lands at the same exit tail.
        new("Zanarkand stadium (opening)", "Zanarkand", "znkd1300", SceneWorker: 0x2B,
            SceneStart: new(0x1AAED, [0xD8, 0x5F, 0x00, 0xAE, 0x00, 0x00, 0xD8, 0x56, 0x00]),
            Exit: new(Worker: 0x2B, Jump: -1, CodeOffset: 0x1AEB2, Expected: [0xF6, 0x02, 0x00, 0xAE, 0x05, 0x00]),
            MomentBelow: 6, MessageWindow: 3, HostWorker: 0x25),
        new("Besaid promontory (first visit)", "Besaid", "bsil0600", SceneWorker: 0x0A,
            SceneStart: new(0x3DAD, [0x9F, 0x00, 0x00, 0x2C, 0xB0, 0x06, 0x00, 0xD8, 0x5E]),
            Exit: new(Worker: 0x0A, Jump: -1, CodeOffset: 0x3EA6, Expected: [0xAE, 0x00, 0x00, 0xAE, 0x06, 0x00]),
            MomentBelow: 125, MessageWindow: 3, HostWorker: 0x10, MomentFrom: 124),
        new("Besaid village (GameMoment 184)", "Besaid", "bsvr0300", SceneWorker: 0x10,
            SceneStart: new(0x1E59, [0x9F, 0x00, 0x00, 0x2C, 0xB0, 0x00, 0x00, 0xD8, 0x5E]),
            Exit: new(Worker: 0x10, Jump: -1, CodeOffset: 0x1EC7, Expected: [0xAE, 0x3C, 0x00, 0xD8, 0x00, 0x00]),
            MomentBelow: 185, MessageWindow: 3, HostWorker: 0x13, MomentFrom: 184),
        new("Besaid (GameMoment 191)", "Besaid", "bsmm0000", SceneWorker: 0x06,
            SceneStart: new(0x0A86, [0xAE, 0x00, 0x00, 0xD8, 0xF2, 0x00, 0xAE, 0x00, 0x00]),
            Exit: new(Worker: 0x06, Jump: -1, CodeOffset: 0x1167, Expected: [0xAE, 0x07, 0x00, 0xAE, 0x1B, 0x00]),
            MomentBelow: 192, MessageWindow: 3, HostWorker: 0x0B),
        new("Besaid village (GameMoment 192)", "Besaid", "bsvr0300", SceneWorker: 0x10,
            SceneStart: new(0x1E59, [0x9F, 0x00, 0x00, 0x2C, 0xB0, 0x00, 0x00, 0xD8, 0x5E]),
            Exit: new(Worker: 0x10, Jump: -1, CodeOffset: 0x23E7, Expected: [0xAE, 0x3C, 0x00, 0xD8, 0x05, 0x40]),
            MomentBelow: 193, MessageWindow: 3, HostWorker: 0x13, MomentFrom: 192),
        new("Besaid village, departure (GameMoment 200)", "Besaid", "bsvr0000", SceneWorker: 0x31,
            SceneStart: new(0x14ED5, [0x9F, 0x00, 0x00, 0x2C, 0xB0, 0x00, 0x00, 0xD8, 0x5E]),
            Exit: new(Worker: 0x31, Jump: -1, CodeOffset: 0x15504, Expected: [0xAE, 0x14, 0x00, 0xD8, 0x00, 0x00]),
            MomentBelow: 201, MessageWindow: 3, HostWorker: 0x46, MomentFrom: 200),
    ];

    public static string EventPath(string eventId) => BlitzballStats.EventPath(eventId);

    public static string TextPath(string languageFolder, string eventId) =>
        $"{GamePaths.Master}{languageFolder}/event/obj_ps3/{eventId[..2]}/{eventId}/{eventId}.bin";

    /// <summary>The scenes of each event, so one event's scenes are edited together.</summary>
    public static IEnumerable<IGrouping<string, SkippableScene>> ByEvent => All.GroupBy(s => s.EventId, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every file an event's scene edits write: the event and, for the hold-to-skip prompt, each language's text.</summary>
    public static IReadOnlyDictionary<string, byte[]> Build(VbfArchive original, IReadOnlyList<SkippableScene> scenes, string mode, SkipButton button) =>
        Build(path => original.IndexOf(path) >= 0 ? original.ReadFile(path) : null, scenes, mode, button);

    public static IReadOnlyDictionary<string, byte[]> Build(VbfArchive original, SkippableScene scene, string mode, SkipButton button) =>
        Build(original, [scene], mode, button);

    /// <summary>
    /// As above, reading game files through <paramref name="read"/> (null for a file the game doesn't have). Every scene
    /// is checked against the original first; then exit labels are added, and edits find their places through function
    /// entries and jump labels, which follow the code as earlier edits move it.
    /// </summary>
    public static IReadOnlyDictionary<string, byte[]> Build(Func<string, byte[]?> read, IReadOnlyList<SkippableScene> scenes, string mode, SkipButton button)
    {
        if (scenes.Count == 0 || scenes.Any(s => !s.EventId.Equals(scenes[0].EventId, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Build one event's scenes at a time.", nameof(scenes));
        if (mode != Always && mode != Hold) throw new ArgumentException($"Unknown cutscene mode \"{mode}\".", nameof(mode));
        string eventId = scenes[0].EventId;
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var ebp = EventFile.Read(read(EventPath(eventId)) ?? throw new FileNotFoundException($"The game has no {eventId} event."));
        byte[] originalChunk = ebp.Chunk(EventFile.ScriptChunk)!;
        var script = new EventScriptEditor(originalChunk);
        var tables = new ScriptTables(originalChunk);

        var plans = new List<(SkippableScene Scene, int Function, int ExitJump, IReadOnlyList<SkippedStatement> Replay)>();
        if (RelayoutRisk(script) is { } risk && !VerifiedInGame.Contains(scenes[0].EventId)) throw new UnsafeSkipException(scenes[0], risk);
        foreach (var scene in scenes)
        {
            Check(script, tables, scene);
            var audit = Audit(tables, scene);
            if (!audit.Safe) throw new UnsafeSkipException(scene, string.Join("; ", audit.Problems()));
            plans.Add((scene, ScenesFunction(script, scene), -1, audit.Replays.ToList()));
        }
        // Labels first, at original offsets; insertions move them with their code from here on.
        for (int i = 0; i < plans.Count; i++)
        {
            var exit = plans[i].Scene.Exit;
            plans[i] = plans[i] with { ExitJump = exit.Jump >= 0 ? exit.Jump : script.AddJump(exit.Worker, exit.CodeOffset) };
        }

        int firstString = mode == Hold ? AddPrompts(read, eventId, button, ebp, files) : -1;
        foreach (var (scene, function, exitJump, replays) in plans)
        {
            // Where the skip goes: straight to the exit, or first through the statements it must replay.
            int target = exitJump;
            var replay = new List<byte>();
            foreach (var r in replays)
            {
                if (!r.OwnWorker) { replay.AddRange(r.Bytes); continue; }
                // A placement moves whoever runs it: it gets a one-statement function on its own worker, run and awaited.
                int helper = script.AddFunction(r.Worker, script.AppendCode([.. r.Bytes, Atel.Return]));
                replay.AddRange(Atel.Run(SceneAudit.LevelAbove(tables, r.Worker), r.Worker, helper, awaitEnd: true));
            }
            if (replay.Count > 0) target = script.AddJump(scene.Exit.Worker, script.AppendCode([.. replay, .. Atel.Goto(exitJump)]));
            if (mode == Always && scene.Exit.Worker == scene.SceneWorker) script.InsertCode(script.FunctionEntry(scene.SceneWorker, function), Guard(scene, target));
            else if (mode == Always)
            {
                // The exit is in another worker's code, which jump labels can't reach: the scene runs a skip function on
                // that worker instead (above the level the scene's parts run at there), which jumps to the exit.
                int skip = script.AddFunction(scene.Exit.Worker, script.AppendCode([.. Guard(scene, target), Atel.Return]));
                InsertGuardedRun(script, scene, script.FunctionEntry(scene.SceneWorker, function), Atel.Run(scene.SkipLevel, scene.Exit.Worker, skip, awaitEnd: true));
            }
            else if (scene.StaysInRoom) AddStartCheck(script, scene, function, target, button, firstString);
            else AddWatcher(script, scene, function, exitJump, target, button, firstString);
        }

        ebp.SetChunk(EventFile.ScriptChunk, script.ToBytes());
        files[EventPath(eventId)] = ebp.ToBytes();
        return files;
    }

    /// <summary>The skip-safety audit of a scene, against the original event.</summary>
    public static SceneAuditResult AuditOf(VbfArchive original, SkippableScene scene) =>
        Audit(new ScriptTables(EventFile.Read(original.ReadFile(EventPath(scene.EventId))).Chunk(EventFile.ScriptChunk)!), scene);

    private static SceneAuditResult Audit(ScriptTables tables, SkippableScene scene) =>
        SceneAudit.Audit(tables, scene.SceneWorker, scene.SceneStart.Offset, scene.Exit.Worker, scene.Exit.CodeOffset, scene.Trigger);

    /// <summary>Pushes whether GameMoment is in the scene's first-visit range.</summary>
    public static byte[] MomentCondition(SkippableScene scene) => scene.MomentFrom > 0
        ? [.. Atel.Get(Atel.GameMomentVariable), .. Atel.Push(scene.MomentFrom), AtelCode.OpGte, .. Atel.MomentBelow(scene.MomentBelow), Atel.LogicalAnd]
        : Atel.MomentBelow(scene.MomentBelow);

    /// <summary>if (GameMoment is in the first-visit range) jump to <paramref name="jump"/>: the exit, or the replay before it.</summary>
    public static byte[] Guard(SkippableScene scene, int jump) => [.. MomentCondition(scene), .. Atel.GotoIf(jump)];

    /// <summary>The guard with the scene's own exit label (scenes whose exit is an existing label).</summary>
    public static byte[] Guard(SkippableScene scene) => Guard(scene, scene.Exit.Jump);

    /// <summary>Events whose skip has been played through with the edit in place, so the re-layout gate trusts them.</summary>
    public static readonly IReadOnlySet<string> VerifiedInGame = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "znkd0600", "znkd1300", "bsil0600", "bsvr0300", "bsmm0000", "bsvr0000" };

    /// <summary>
    /// Why editing an event might break its layout, or null. Any edit moves the data after the code, and the game reads
    /// some of it through pointers: those the editor knows move along, but bsil0300 crashed on load after every edit
    /// until its meta struct's +0x20 (a pointer in 25 events, which FFXDataParser reads as a plain value) moved too.
    /// Events with that pointer, or with other words that look like untracked pointers, aren't edited until one has been
    /// played through.
    /// </summary>
    public static string? RelayoutRisk(EventScriptEditor script)
    {
        if (script.MetaPointer20 != 0) return "its meta struct has the +0x20 pointer, which no in-game test has covered yet";
        var candidates = script.UntrackedPointerCandidates();
        return candidates.Count == 0 ? null
            : $"{candidates.Count} word(s) after the code look like pointers the editor doesn't move (e.g. {candidates[0].Field:X} -> {candidates[0].Value:X})";
    }

    private static void Check(EventScriptEditor script, ScriptTables tables, SkippableScene scene)
    {
        var checks = new List<SceneCode> { scene.SceneStart };
        if (scene.BuiltIn is { } b) checks.AddRange([b.Start, b.StopStart, b.SkipStart]);
        if (scene.Exit.Expected is { } expected) checks.Add(new SceneCode(scene.Exit.CodeOffset, expected));
        foreach (var code in checks)
            if (code.Offset + code.Expected.Length > script.CodeLength || !script.Code(code.Offset, code.Expected.Length).SequenceEqual(code.Expected))
                throw new EventMismatchException(scene, $"the code at {code.Offset:X4} isn't {Convert.ToHexString(code.Expected)}");
        if (scene.Exit.Jump >= 0 && script.JumpTarget(scene.Exit.Worker, scene.Exit.Jump) != scene.Exit.CodeOffset)
            throw new EventMismatchException(scene, $"w{scene.Exit.Worker:X2} j{scene.Exit.Jump:X2} doesn't point at {scene.Exit.CodeOffset:X4}");
        if (tables.FunctionAt(scene.Exit.CodeOffset) is not { Worker: var owner } || owner != scene.Exit.Worker)
            throw new EventMismatchException(scene, $"the exit at {scene.Exit.CodeOffset:X4} isn't in w{scene.Exit.Worker:X2}'s code");
        if (scene.BuiltIn is { } w)
        {
            if (script.FunctionEntry(scene.SceneWorker, w.SkipFunction) != w.SkipStart.Offset || script.FunctionEntry(w.Worker, w.StopFunction) != w.StopStart.Offset)
                throw new EventMismatchException(scene, "the game's skip watcher isn't where it should be");
        }
    }

    /// <summary>Jump labels the watcher uses, as indexes into its host worker's jump table.</summary>
    internal sealed record WatcherLabels(int Loop, int Held, int Cancel, int Idle);

    /// <summary>
    /// The watcher, in the shape the game's own button prompts use (the S.S. Winno Jecht Shot tutorial): show a box, then
    /// poll the button with short waits.
    /// <list type="bullet">
    /// <item>Intro: as soon as it starts, show "Hold [button] to skip" for <see cref="IntroFrames"/> frames, checking the
    /// button every <see cref="IntroPollFrames"/>; holding it goes straight to the bar (the open box is redrawn), otherwise
    /// the box closes and the watcher idles.</item>
    /// <item>Idle: if the skip button isn't down, wait a frame and look again.</item>
    /// <item>Held: show the prompt with its bar (<see cref="ShowPrompt"/>), then check the button every
    /// <see cref="PollFrames"/> frames, redrawing it with the next bar step at even intervals so it is full one step before
    /// the end. Letting go closes the box, waits for it to go, and returns to idle.</item>
    /// <item>After <see cref="HoldFrames"/> frames held: close the box, wait until it has gone, then run the scene's skip
    /// function at level 2, which preempts the scene and jumps to its exit. The skip only counts polls, so it can't
    /// depend on the box.</item>
    /// </list>
    /// The polls are written out one after another, so no variable is needed to count them. Returns the code and the
    /// offsets (within it) of each label.
    /// </summary>
    internal static byte[] WatcherCode(int window, int button, int introString, int firstBarString, byte[] beforeSkip, int sceneWorker,
        int skipFunction, WatcherLabels jumps, out WatcherLabels at, int skipLevel = 2)
    {
        var code = new List<byte>();

        code.AddRange(ShowPrompt(window, introString));
        for (int poll = 0; poll < IntroFrames / IntroPollFrames; poll++)
        {
            code.AddRange(Atel.Call(Atel.Wait, IntroPollFrames));
            code.AddRange([.. Atel.IsDown(button), .. Atel.GotoIf(jumps.Held)]);
        }
        code.AddRange(ClosePrompt(window));

        int loopAt = code.Count;
        code.AddRange([.. Atel.IsDown(button), .. Atel.GotoUnless(jumps.Idle)]);
        int heldAt = code.Count;
        code.AddRange(ShowPrompt(window, firstBarString));
        // The bar reaches full one step before the skip, so the last redraw never lands on the frame the box closes.
        int polls = HoldFrames / PollFrames, pollsPerStep = polls / (Steps + 1);
        for (int poll = 1; poll <= polls; poll++)
        {
            code.AddRange(Atel.Call(Atel.Wait, PollFrames));
            code.AddRange([.. Atel.IsDown(button), .. Atel.GotoUnless(jumps.Cancel)]);
            if (poll % pollsPerStep == 0 && poll / pollsPerStep <= Steps) code.AddRange(ShowPrompt(window, firstBarString + poll / pollsPerStep));
        }
        code.AddRange(ClosePrompt(window));
        code.AddRange(beforeSkip);
        code.AddRange(Atel.Run(skipLevel, sceneWorker, skipFunction, awaitEnd: true));
        code.Add(Atel.Return);

        int cancelAt = code.Count;
        code.AddRange(ClosePrompt(window));
        int idleAt = code.Count;
        code.AddRange(Atel.Call(Atel.Wait, 1));
        code.AddRange(Atel.Goto(jumps.Loop));
        code.Add(Atel.Return);
        at = new WatcherLabels(loopAt, heldAt, cancelAt, idleAt);
        return [.. code];
    }

    /// <summary>
    /// Shows (or redraws) the prompt: the full sequence the Bikanel recruit menu repeats on its open description box each
    /// time the selection changes. Swapping the string alone doesn't redraw an open box.
    /// </summary>
    private static byte[] ShowPrompt(int window, int text) =>
    [
        .. Atel.Call(Atel.PositionMessage, window, PromptX, PromptY, 0),
        .. Atel.Call(Atel.SetMessageBoxTransparent, window, 0),
        .. Atel.Call(Atel.DisplayFieldString, window, text),
        .. Atel.Call(Atel.SetTextFlags, window, 0),
        .. Atel.Call(Atel.ShowMessage, window, 0),
    ];

    /// <summary>Closes the prompt and waits until it has gone, as the game does before showing the next box.</summary>
    private static byte[] ClosePrompt(int window) => [.. Atel.Call(Atel.CloseMessage, window), .. Atel.Call(Atel.WaitForText, window, 1)];

    /// <summary>
    /// Adds the hold-to-skip watcher for one scene. <paramref name="exitJump"/> is the scene's exit label;
    /// <paramref name="target"/> where the skip jumps (the exit, or the statements it replays first).
    /// </summary>
    private static void AddWatcher(EventScriptEditor script, SkippableScene scene, int sceneFunction, int exitJump, int target, SkipButton button, int firstString)
    {
        int window = scene.MessageWindow;
        byte[] closePrompt = Atel.Call(Atel.CloseMessage, window);
        int hostWorker, skipFunction, watchFunction;
        byte[] beforeSkip;

        if (scene.BuiltIn is { } b)
        {
            // The game starts its watcher about 5 s in; that call becomes no-ops and the watcher starts with the scene.
            script.OverwriteCode(MapCode(script, b.Start.Offset), b.Start.Expected, new byte[b.Start.Expected.Length]);
            // The game's skip function gets the first-visit guard; its watcher-stop function closes our prompt first.
            script.InsertCode(script.FunctionEntry(scene.SceneWorker, b.SkipFunction), Guard(scene, target));
            script.InsertCode(script.FunctionEntry(b.Worker, b.StopFunction), closePrompt);
            hostWorker = b.Worker;
            skipFunction = b.SkipFunction;
            watchFunction = b.WatchFunction;
            beforeSkip = b.BeforeSkip;
        }
        else
        {
            if (scene.HostWorker < 0) throw new InvalidOperationException($"{scene.Name} needs a host worker for its skip watcher.");
            hostWorker = scene.HostWorker;
            skipFunction = script.AddFunction(scene.Exit.Worker, script.AppendCode([.. Guard(scene, target), Atel.Return]));
            watchFunction = -1;
            beforeSkip = [];
        }

        int count = script.JumpCount(hostWorker);
        var labels = new WatcherLabels(Loop: count, Held: count + 1, Cancel: count + 2, Idle: count + 3);
        byte[] watcher = WatcherCode(window, button.Index, firstString, firstString + 1, beforeSkip, scene.Exit.Worker, skipFunction, labels, out var at,
            scene.BuiltIn is null ? scene.SkipLevel : 2);
        int start = script.AppendCode(watcher);
        foreach (int offset in new[] { at.Loop, at.Held, at.Cancel, at.Idle }) script.AddJump(hostWorker, start + offset);

        int entry = script.FunctionEntry(scene.SceneWorker, sceneFunction);
        if (watchFunction >= 0)
        {
            script.SetFunctionEntry(hostWorker, watchFunction, start);
            // Start the watcher with the scene's first instruction.
            script.InsertCode(entry, Atel.Run(1, hostWorker, watchFunction, awaitEnd: false));
        }
        else
        {
            watchFunction = script.AddFunction(hostWorker, start);
            int stop = script.AddFunction(hostWorker, script.AppendCode([.. closePrompt, Atel.DirectReturn, Atel.Return]));
            // Stop the watcher when the scene reaches its exit.
            script.InsertCode(script.JumpTarget(scene.Exit.Worker, exitJump), Atel.Run(2, hostWorker, stop, awaitEnd: false));
            // Start it with the scene's first instruction, only when the scene plays (the function may run at other
            // moments too). The entry is read again: the stop moves it when the exit comes first in the code.
            InsertGuardedRun(script, scene, script.FunctionEntry(scene.SceneWorker, sceneFunction), Atel.Run(1, hostWorker, watchFunction, awaitEnd: false));
        }
    }

    /// <summary>
    /// Inserts at <paramref name="entry"/>: if GameMoment is in the scene's range, <paramref name="run"/>; then the original
    /// first instruction, which gets a new jump label for the "not in range" branch.
    /// </summary>
    private static void InsertGuardedRun(EventScriptEditor script, SkippableScene scene, int entry, byte[] run)
    {
        byte[] condition = MomentCondition(scene);
        int length = condition.Length + 3 + run.Length;
        int after = script.JumpCount(scene.SceneWorker);
        script.InsertCode(entry, [.. condition, .. Atel.GotoUnless(after), .. run]);
        script.AddJump(scene.SceneWorker, entry + length);
    }

    /// <summary>
    /// Hold-to-skip for a scene that stays in the room, decided as it starts, in the scene's own worker before anything
    /// moves: if the moment is right and the skip button is down, show the bar and keep checking; letting go closes the
    /// box and plays the scene, a full bar closes it and jumps to <paramref name="target"/> (the exit, or the replay
    /// before it). Otherwise the scene starts as usual with no delay.
    /// </summary>
    private static void AddStartCheck(EventScriptEditor script, SkippableScene scene, int sceneFunction, int target, SkipButton button, int firstString)
    {
        int window = scene.MessageWindow;
        int count = script.JumpCount(scene.SceneWorker);
        int cancel = count, carryOn = count + 1;
        var code = new List<byte>();
        code.AddRange([.. MomentCondition(scene), .. Atel.GotoUnless(carryOn)]);
        code.AddRange([.. Atel.IsDown(button.Index), .. Atel.GotoUnless(carryOn)]);
        int firstBar = firstString + 1;
        code.AddRange(ShowPrompt(window, firstBar));
        int polls = HoldFrames / PollFrames, pollsPerStep = polls / (Steps + 1);
        for (int poll = 1; poll <= polls; poll++)
        {
            code.AddRange(Atel.Call(Atel.Wait, PollFrames));
            code.AddRange([.. Atel.IsDown(button.Index), .. Atel.GotoUnless(cancel)]);
            if (poll % pollsPerStep == 0 && poll / pollsPerStep <= Steps) code.AddRange(ShowPrompt(window, firstBar + poll / pollsPerStep));
        }
        code.AddRange(ClosePrompt(window));
        code.AddRange(Atel.Goto(target));
        int cancelAt = code.Count;
        code.AddRange(ClosePrompt(window));
        code.AddRange(Atel.Goto(carryOn));

        int entry = script.FunctionEntry(scene.SceneWorker, sceneFunction);
        script.InsertCode(entry, [.. code]);
        script.AddJump(scene.SceneWorker, entry + cancelAt);
        script.AddJump(scene.SceneWorker, entry + code.Count);
    }

    /// <summary>Where code at <paramref name="original"/> (an offset in the unedited code) is now.</summary>
    private static int MapCode(EventScriptEditor script, int original) => script.Map(script.CodeStart + original) - script.CodeStart;

    private static int ScenesFunction(EventScriptEditor script, SkippableScene scene)
    {
        for (int f = 0; f < script.FunctionCount(scene.SceneWorker); f++)
            if (script.FunctionEntry(scene.SceneWorker, f) == scene.SceneStart.Offset) return f;
        throw new EventMismatchException(scene, $"no function of w{scene.SceneWorker:X2} starts at {scene.SceneStart.Offset:X4}");
    }

    /// <summary>
    /// Adds the prompt strings (one per bar step) to the event's text in every language and to the event file's own
    /// Japanese and English text chunks, all at the same index; returns that index.
    /// </summary>
    private static int AddPrompts(Func<string, byte[]?> read, string eventId, SkipButton button, EventFile ebp, Dictionary<string, byte[]> files)
    {
        var tables = new List<(string Charset, Func<byte[]> Read, Action<byte[]> Write)>();
        foreach (string folder in GamePaths.LanguageFolders)
        {
            string path = TextPath(folder, eventId);
            if (read(path) is not { } text) continue;
            tables.Add((FfxCharset.CharsetFor(folder[4..6]), () => text, bytes => files[path] = bytes));
        }
        // The event file's own Japanese and English text, where it has them (some events have fewer chunks).
        foreach (var (charsetId, chunk) in new[] { ("jp", EventFile.JapaneseTextChunk), ("us", EventFile.EnglishTextChunk) })
            if (chunk < ebp.ChunkCount && ebp.Chunk(chunk) is { Length: > 0 })
                tables.Add((charsetId, () => ebp.Chunk(chunk)!, bytes => ebp.SetChunk(chunk, bytes)));

        if (tables.Count == 0) throw new InvalidOperationException($"{eventId} has no text to show the hold-to-skip prompt in.");
        var counts = tables.Select(t => EventText.Count(t.Read())).ToList();
        int first = counts.Max();
        var charsets = new Dictionary<string, FfxCharset>();
        for (int i = 0; i < tables.Count; i++)
        {
            var (charsetId, current, write) = tables[i];
            if (!charsets.TryGetValue(charsetId, out var charset))
                charsets[charsetId] = charset = FfxCharset.FromTableFile(read(FfxCharset.TablePath(charsetId)) ?? throw new FileNotFoundException($"No {charsetId} font table."));
            // Pad tables with fewer strings so the prompts share one index everywhere.
            var strings = Enumerable.Repeat(Array.Empty<byte>(), first - counts[i]).Concat(PromptStrings(charset, charsetId, button)).ToList();
            write(EventText.Append(current(), strings));
        }
        return first;
    }

    /// <summary>
    /// The intro "Hold [button] to skip", then "Hold [button] to skip [###--]", one string per bar step. English in every
    /// language for now; the Japanese, Chinese and Korean fonts get it in fullwidth letters with a ＊/－ bar, since they
    /// have no [ ] or #.
    /// </summary>
    internal static IEnumerable<byte[]> PromptStrings(FfxCharset charset, string charsetId, SkipButton button)
    {
        bool latin = charsetId == "us";
        byte[] buttonText = button.Icon is { } icon ? [0x0B, icon] : charset.Encode(button.Name);
        yield return [.. ShowInstantly, .. charset.Encode("Hold "), .. buttonText, .. charset.Encode(" to skip")];
        for (int step = 0; step <= Steps; step++)
        {
            string bar = latin ? $"[{new string('#', step)}{new string('-', Steps - step)}]" : new string('*', step) + new string('-', Steps - step);
            yield return [.. ShowInstantly, .. charset.Encode("Hold "), .. buttonText, .. charset.Encode(" to skip  " + bar)];
        }
    }
}

/// <summary>A scene whose skip would bypass something it can't (a battle, a choice...); see <see cref="Events.SceneAudit"/>.</summary>
public sealed class UnsafeSkipException(SkippableScene scene, string problems)
    : InvalidOperationException($"{scene.EventId} ({scene.Name}) can't be skipped safely: {problems}. Nothing was changed.");

public sealed class EventMismatchException(SkippableScene scene, string problem)
    : InvalidOperationException($"{scene.EventId} ({scene.Name}) isn't the version this edit was made for: {problem}. Nothing was changed.");
