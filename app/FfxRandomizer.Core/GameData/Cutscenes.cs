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
    int MessageWindow, BuiltInWatcher? BuiltIn = null, int HostWorker = -1, int MomentFrom = 0, int SkipLevel = 2)
{
    /// <summary>The GameMoment a jump-to-scene save holds so the scene plays.</summary>
    public int Trigger => MomentBelow - 1;
}

/// <summary>
/// The scenes "Skip cutscenes" covers, and how their files are edited. Jumping from a scene's start to its exit comes from
/// CSR2, the Fahrenheit cutscene remover (MIT, The Fahrenheit contributors, https://github.com/fahrenheit-crew/fh-mods-csr).
/// </summary>
public static class Cutscenes
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

    public static IReadOnlyList<SkippableScene> All { get; } =
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

        var plans = new List<(SkippableScene Scene, int Function, int ExitJump, byte[] Replay)>();
        foreach (var scene in scenes)
        {
            Check(script, tables, scene);
            var audit = Audit(tables, scene);
            if (!audit.Safe) throw new UnsafeSkipException(scene, string.Join("; ", audit.Problems()));
            plans.Add((scene, ScenesFunction(script, scene), -1, audit.ReplayCode));
        }
        // Labels first, at original offsets; insertions move them with their code from here on.
        for (int i = 0; i < plans.Count; i++)
        {
            var exit = plans[i].Scene.Exit;
            plans[i] = plans[i] with { ExitJump = exit.Jump >= 0 ? exit.Jump : script.AddJump(exit.Worker, exit.CodeOffset) };
        }

        int firstString = mode == Hold ? AddPrompts(read, eventId, button, ebp, files) : -1;
        foreach (var (scene, function, exitJump, replay) in plans)
        {
            // Where the skip goes: straight to the exit, or first through the statements it must replay.
            int target = exitJump;
            if (replay.Length > 0) target = script.AddJump(scene.SceneWorker, script.AppendCode([.. replay, .. Atel.Goto(exitJump)]));
            if (mode == Always) script.InsertCode(script.FunctionEntry(scene.SceneWorker, function), Guard(scene, target));
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
        SceneAudit.Audit(tables, scene.SceneWorker, scene.SceneStart.Offset, scene.Exit.CodeOffset, scene.Trigger);

    /// <summary>Pushes whether GameMoment is in the scene's first-visit range.</summary>
    public static byte[] MomentCondition(SkippableScene scene) => scene.MomentFrom > 0
        ? [.. Atel.Get(Atel.GameMomentVariable), .. Atel.Push(scene.MomentFrom), AtelCode.OpGte, .. Atel.MomentBelow(scene.MomentBelow), Atel.LogicalAnd]
        : Atel.MomentBelow(scene.MomentBelow);

    /// <summary>if (GameMoment is in the first-visit range) jump to <paramref name="jump"/>: the exit, or the replay before it.</summary>
    public static byte[] Guard(SkippableScene scene, int jump) => [.. MomentCondition(scene), .. Atel.GotoIf(jump)];

    /// <summary>The guard with the scene's own exit label (scenes whose exit is an existing label).</summary>
    public static byte[] Guard(SkippableScene scene) => Guard(scene, scene.Exit.Jump);

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
        if (scene.Exit.Worker != scene.SceneWorker) throw new EventMismatchException(scene, "the exit must be in the scene's own worker");
        if (tables.FunctionAt(scene.Exit.CodeOffset) is not { Worker: var owner } || owner != scene.SceneWorker)
            throw new EventMismatchException(scene, $"the exit at {scene.Exit.CodeOffset:X4} isn't in w{scene.SceneWorker:X2}'s code");
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
            skipFunction = script.AddFunction(scene.SceneWorker, script.AppendCode([.. Guard(scene, target), Atel.Return]));
            watchFunction = -1;
            beforeSkip = [];
        }

        int count = script.JumpCount(hostWorker);
        var labels = new WatcherLabels(Loop: count, Held: count + 1, Cancel: count + 2, Idle: count + 3);
        byte[] watcher = WatcherCode(window, button.Index, firstString, firstString + 1, beforeSkip, scene.SceneWorker, skipFunction, labels, out var at,
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
            script.InsertCode(script.JumpTarget(scene.SceneWorker, exitJump), Atel.Run(2, hostWorker, stop, awaitEnd: false));
            // Start it with the scene's first instruction, only when the scene plays (the function may run at other
            // moments too): if the moment is right run the watcher, then carry on with the original first instruction.
            byte[] startWatcher = Atel.Run(1, hostWorker, watchFunction, awaitEnd: false);
            byte[] condition = MomentCondition(scene);
            int length = condition.Length + 3 + startWatcher.Length;
            int after = script.JumpCount(scene.SceneWorker);
            script.InsertCode(entry, [.. condition, .. Atel.GotoUnless(after), .. startWatcher]);
            script.AddJump(scene.SceneWorker, entry + length);
        }
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
        tables.Add(("jp", () => ebp.Chunk(EventFile.JapaneseTextChunk)!, bytes => ebp.SetChunk(EventFile.JapaneseTextChunk, bytes)));
        tables.Add(("us", () => ebp.Chunk(EventFile.EnglishTextChunk)!, bytes => ebp.SetChunk(EventFile.EnglishTextChunk, bytes)));

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
