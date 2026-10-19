using FfxRandomizer.Core.GameData.Events;
using FfxRandomizer.Core.GameData.Text;
using FfxRandomizer.Core.Vbf;

namespace FfxRandomizer.Core.GameData;

/// <summary>A code offset in an event script and the bytes that must be there before it is edited.</summary>
public sealed record SceneCode(int Offset, byte[] Expected);

/// <summary>A jump label an edit relies on, checked before editing.</summary>
public sealed record EventJump(int Worker, int Jump, int CodeOffset);

/// <summary>
/// The game's own skip watcher for a scene, when it has one: <see cref="Worker"/>'s <see cref="WatchFunction"/> is
/// started alongside the scene and polls a button; on a press it runs the scene worker's <see cref="SkipFunction"/> at
/// level 2, which preempts the scene. <see cref="StopFunction"/> (a bare direct return) is run at level 2 to end the
/// watcher when the scene finishes. The skip watcher reuses all three, so the scene never has two watchers.
/// </summary>
public sealed record BuiltInWatcher(
    int Worker, int WatchFunction, int StopFunction, SceneCode StopStart, int SkipFunction, SceneCode SkipStart, byte[] BeforeSkip);

/// <summary>
/// A cutscene that can be skipped. <see cref="SceneStart"/> is the scene function's first instruction; <see cref="Exit"/>
/// the label its first-visit path ends at (which sets GameMoment and warps on); <see cref="MomentBelow"/> limits the skip
/// to that first visit. Without a <see cref="BuiltIn"/> watcher, the watcher and its stop function are added to
/// <see cref="HostWorker"/> (a worker idle during the scene), a skip function to the scene worker, the watcher is
/// started at the scene's start and stopped at its exit.
/// </summary>
public sealed record SkippableScene(
    string Name, string Area, string EventId, int SceneWorker, SceneCode SceneStart, EventJump Exit, int MomentBelow,
    int MessageWindow, BuiltInWatcher? BuiltIn = null, int HostWorker = -1);

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
            BuiltIn: new(Worker: 0x13, WatchFunction: 2, StopFunction: 3,
                StopStart: new(0x4C12, [Atel.DirectReturn]),
                SkipFunction: 3,
                SkipStart: new(0x437D, [0xAE, 0x1E, 0x00, 0xD8, 0x05, 0x40]), // SgEvent.fadeoutToBlack(30)
                BeforeSkip: Atel.Set(0x23, 1))), // eventVar0014 = true, as the game's watcher does
    ];

    public static string EventPath(string eventId) => BlitzballStats.EventPath(eventId);

    public static string TextPath(string languageFolder, string eventId) =>
        $"{GamePaths.Master}{languageFolder}/event/obj_ps3/{eventId[..2]}/{eventId}/{eventId}.bin";

    /// <summary>Every file a scene's edit writes: the event and, for the hold-to-skip prompt, each language's text.</summary>
    public static IReadOnlyDictionary<string, byte[]> Build(VbfArchive original, SkippableScene scene, string mode, SkipButton button) =>
        Build(path => original.IndexOf(path) >= 0 ? original.ReadFile(path) : null, scene, mode, button);

    /// <summary>As above, reading game files through <paramref name="read"/> (null for a file the game doesn't have).</summary>
    public static IReadOnlyDictionary<string, byte[]> Build(Func<string, byte[]?> read, SkippableScene scene, string mode, SkipButton button)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var ebp = EventFile.Read(read(EventPath(scene.EventId)) ?? throw new FileNotFoundException($"The game has no {scene.EventId} event."));
        var script = new EventScriptEditor(ebp.Chunk(EventFile.ScriptChunk)!);
        Check(script, scene);

        if (mode == Always)
        {
            script.InsertCode(scene.SceneStart.Offset, Guard(scene));
        }
        else if (mode == Hold)
        {
            int firstString = AddPrompts(read, scene, button, ebp, files);
            AddWatcher(script, scene, button, firstString);
        }
        else throw new ArgumentException($"Unknown cutscene mode \"{mode}\".", nameof(mode));

        ebp.SetChunk(EventFile.ScriptChunk, script.ToBytes());
        files[EventPath(scene.EventId)] = ebp.ToBytes();
        return files;
    }

    /// <summary>if (GameMoment &lt; first-visit limit) jump to the scene's exit.</summary>
    public static byte[] Guard(SkippableScene scene) => [.. Atel.MomentBelow(scene.MomentBelow), .. Atel.GotoIf(scene.Exit.Jump)];

    private static void Check(EventScriptEditor script, SkippableScene scene)
    {
        var checks = new List<SceneCode> { scene.SceneStart };
        if (scene.BuiltIn is { } b) checks.AddRange([b.StopStart, b.SkipStart]);
        foreach (var code in checks)
            if (code.Offset + code.Expected.Length > script.CodeLength || !script.Code(code.Offset, code.Expected.Length).SequenceEqual(code.Expected))
                throw new EventMismatchException(scene, $"the code at {code.Offset:X4} isn't {Convert.ToHexString(code.Expected)}");
        if (script.JumpTarget(scene.Exit.Worker, scene.Exit.Jump) != scene.Exit.CodeOffset)
            throw new EventMismatchException(scene, $"w{scene.Exit.Worker:X2} j{scene.Exit.Jump:X2} doesn't point at {scene.Exit.CodeOffset:X4}");
        if (scene.BuiltIn is { } w)
        {
            if (script.FunctionEntry(scene.SceneWorker, w.SkipFunction) != w.SkipStart.Offset || script.FunctionEntry(w.Worker, w.StopFunction) != w.StopStart.Offset)
                throw new EventMismatchException(scene, "the game's skip watcher isn't where it should be");
        }
    }

    /// <summary>
    /// The watcher, in the shape the game's own button prompts use (the S.S. Winno Jecht Shot tutorial): open the prompt
    /// once, then poll the button with short waits.
    /// <list type="bullet">
    /// <item>Idle: if the skip button isn't down, wait a frame and look again.</item>
    /// <item>Held: show the prompt (<see cref="ShowPrompt"/>), then check the button every <see cref="PollFrames"/>
    /// frames, redrawing the prompt with the next bar step at even intervals so it is full one step before the end.
    /// Letting go closes the prompt, waits for it to go, and returns to idle.</item>
    /// <item>After <see cref="HoldFrames"/> frames held: close the prompt, wait until it has gone, then run the scene's
    /// skip function at level 2, which preempts the scene and jumps to its exit. The skip only counts polls, so it
    /// can't depend on the prompt.</item>
    /// </list>
    /// The polls are written out one after another, so no variable is needed to count them.
    /// </summary>
    internal static byte[] WatcherCode(int window, int button, int firstString, byte[] beforeSkip, int sceneWorker, int skipFunction,
        int loopJump, int cancelJump, int idleJump, out int cancelAt, out int idleAt)
    {
        var code = new List<byte>();
        code.AddRange([.. Atel.IsDown(button), .. Atel.GotoUnless(idleJump)]);
        code.AddRange(ShowPrompt(window, firstString));
        // The bar reaches full one step before the skip, so the last redraw never lands on the frame the box closes.
        int polls = HoldFrames / PollFrames, pollsPerStep = polls / (Steps + 1);
        for (int poll = 1; poll <= polls; poll++)
        {
            code.AddRange(Atel.Call(Atel.Wait, PollFrames));
            code.AddRange([.. Atel.IsDown(button), .. Atel.GotoUnless(cancelJump)]);
            if (poll % pollsPerStep == 0 && poll / pollsPerStep <= Steps) code.AddRange(ShowPrompt(window, firstString + poll / pollsPerStep));
        }
        code.AddRange(ClosePrompt(window));
        code.AddRange(beforeSkip);
        code.AddRange(Atel.Run(2, sceneWorker, skipFunction, awaitEnd: true));
        code.Add(Atel.Return);
        cancelAt = code.Count;
        code.AddRange(ClosePrompt(window));
        idleAt = code.Count;
        code.AddRange(Atel.Call(Atel.Wait, 1));
        code.AddRange(Atel.Goto(loopJump));
        code.Add(Atel.Return);
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

    private static void AddWatcher(EventScriptEditor script, SkippableScene scene, SkipButton button, int firstString)
    {
        int window = scene.MessageWindow;
        byte[] closePrompt = Atel.Call(Atel.CloseMessage, window);
        int hostWorker, skipFunction;
        byte[] beforeSkip;

        if (scene.BuiltIn is { } b)
        {
            // The game's skip function gets the first-visit guard; its watcher-stop function closes our prompt first.
            script.InsertCode(b.SkipStart.Offset, Guard(scene));
            script.InsertCode(script.FunctionEntry(b.Worker, b.StopFunction), closePrompt);
            hostWorker = b.Worker;
            skipFunction = b.SkipFunction;
            beforeSkip = b.BeforeSkip;
        }
        else
        {
            if (scene.HostWorker < 0) throw new InvalidOperationException($"{scene.Name} needs a host worker for its skip watcher.");
            hostWorker = scene.HostWorker;
            skipFunction = script.AddFunction(scene.SceneWorker, script.AppendCode([.. Guard(scene), Atel.Return]));
            beforeSkip = [];
        }

        int jumps = script.JumpCount(hostWorker);
        byte[] watcher = WatcherCode(window, button.Index, firstString, beforeSkip, scene.SceneWorker, skipFunction,
            loopJump: jumps, cancelJump: jumps + 1, idleJump: jumps + 2, out int cancelAt, out int idleAt);
        int start = script.AppendCode(watcher);
        script.AddJump(hostWorker, start);
        script.AddJump(hostWorker, start + cancelAt);
        script.AddJump(hostWorker, start + idleAt);

        if (scene.BuiltIn is { } builtIn)
        {
            // The scene already starts this function and stops it; point it at the new watcher.
            script.SetFunctionEntry(hostWorker, builtIn.WatchFunction, start);
        }
        else
        {
            int watch = script.AddFunction(hostWorker, start);
            int stop = script.AddFunction(hostWorker, script.AppendCode([.. closePrompt, Atel.DirectReturn, Atel.Return]));
            // Start the watcher with the scene, and stop it when the scene reaches its exit.
            script.InsertCode(script.FunctionEntry(scene.SceneWorker, ScenesFunction(script, scene)), Atel.Run(1, hostWorker, watch, awaitEnd: false));
            script.InsertCode(script.JumpTarget(scene.Exit.Worker, scene.Exit.Jump), Atel.Run(2, hostWorker, stop, awaitEnd: false));
        }
    }

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
    private static int AddPrompts(Func<string, byte[]?> read, SkippableScene scene, SkipButton button, EventFile ebp, Dictionary<string, byte[]> files)
    {
        var tables = new List<(string Charset, Func<byte[]> Read, Action<byte[]> Write)>();
        foreach (string folder in GamePaths.LanguageFolders)
        {
            string path = TextPath(folder, scene.EventId);
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
    /// "Hold [button] to skip [###--]", one string per bar step. English in every language for now; the Japanese, Chinese
    /// and Korean fonts get it in fullwidth letters with a ＊/－ bar, since they have no [ ] or #.
    /// </summary>
    internal static IEnumerable<byte[]> PromptStrings(FfxCharset charset, string charsetId, SkipButton button)
    {
        bool latin = charsetId == "us";
        byte[] buttonText = button.Icon is { } icon ? [0x0B, icon] : charset.Encode(button.Name);
        for (int step = 0; step <= Steps; step++)
        {
            string bar = latin ? $"[{new string('#', step)}{new string('-', Steps - step)}]" : new string('*', step) + new string('-', Steps - step);
            yield return [.. ShowInstantly, .. charset.Encode("Hold "), .. buttonText, .. charset.Encode(" to skip  " + bar)];
        }
    }
}

public sealed class EventMismatchException(SkippableScene scene, string problem)
    : InvalidOperationException($"{scene.EventId} ({scene.Name}) isn't the version this edit was made for: {problem}. Nothing was changed.");
