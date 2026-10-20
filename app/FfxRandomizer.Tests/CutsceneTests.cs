using FfxRandomizer.Core.Game;
using FfxRandomizer.Core.GameData;
using FfxRandomizer.Core.GameData.Events;
using FfxRandomizer.Core.GameData.Text;
using FfxRandomizer.Core.Patching;
using FfxRandomizer.Core.Randomization;
using FfxRandomizer.Core.Randomization.Features;
using FfxRandomizer.Core.Storage;
using FfxRandomizer.Core.Vbf;

namespace FfxRandomizer.Tests;

/// <summary>Skip cutscenes. Reads the installed game (read-only) and passes trivially without it.</summary>
public class CutsceneTests
{
    private static readonly Lazy<VbfArchive?> Original = new(() =>
        GameInstall.FindSteamInstall() is { } game ? new PatchManager(game.DataArchivePath, AppPaths.Default).OpenOriginal() : null);

    private static RandomizerSettings Settings(string mode, string? button = null)
    {
        var s = new RandomizerSettings();
        s.Choices["cutscenes"] = mode;
        if (button is not null) s.Choices["cutscenebutton"] = button;
        return s;
    }

    private static readonly SkippableScene Campfire = Cutscenes.All[0];

    private static int Occurrences(byte[] haystack, byte[] needle)
    {
        int count = 0;
        for (int at = haystack.AsSpan().IndexOf(needle); at >= 0; at = haystack.AsSpan(at + 1).IndexOf(needle) is int next and >= 0 ? at + 1 + next : -1) count++;
        return count;
    }

    [Theory]
    [InlineData("hold", "select")]
    [InlineData("hold", "r1")]
    [InlineData("hold", "start")]
    [InlineData("hold", "x")]
    [InlineData("always", null)]
    public void Every_mode_and_button_passes_every_check(string mode, string? button)
    {
        if (Original.Value is not { } original) return;

        var result = RandomizerEngine.Run(original, Settings(mode, button), new Seed("cutscenes"));
        Assert.Empty(result.Problems);
        // Each event, plus every language's text for its prompts.
        Assert.Equal(Cutscenes.ByEvent.Count() * (mode == "hold" ? 9 : 1), result.Files.Count);
        foreach (var (path, bytes) in result.Files)
            Assert.Equal(VbfArchive.BlocksFor((ulong)original.ReadFile(path).Length), VbfArchive.BlocksFor((ulong)bytes.Length));
    }

    [Fact]
    public void Every_scene_passes_the_skip_safety_audit()
    {
        if (Original.Value is not { } original) return;

        foreach (var scene in Cutscenes.All)
        {
            var audit = Cutscenes.AuditOf(original, scene);
            Assert.True(audit.Safe, $"{scene.EventId} {scene.Name}: {string.Join("; ", audit.Problems())}");
        }
    }

    [Fact]
    public void A_scene_whose_skip_would_lose_progress_is_refused()
    {
        if (Original.Value is not { } original) return;

        // znkd0801's GameMoment 12 path writes a computed save variable, which a skip can't replay. (The event also has a
        // word the re-layout gate refuses, so Build would refuse it for that first; this checks the audit.)
        var scene = new SkippableScene("Dream Zanarkand highway, before the first battle", "Zanarkand", "znkd0801", SceneWorker: 0x45,
            SceneStart: new(0x7327, [0xAE, 0x00, 0x00, 0xD8, 0xE9, 0x00, 0x9F, 0x00, 0x00]),
            Exit: new(Worker: 0x46, Jump: -1, CodeOffset: 0x7D6E, Expected: [0xAE, 0x1D, 0x00, 0xAE, 0x22, 0x00]),
            MomentBelow: 13, MessageWindow: 3, HostWorker: 0x4E, MomentFrom: 12);
        Assert.False(Cutscenes.AuditOf(original, scene).Safe);
        Assert.Throws<UnsafeSkipException>(() => Cutscenes.Build(original, scene, Cutscenes.Always, SkipButtons.Default));
    }

    [Fact]
    public void A_skippable_scene_in_an_event_with_Blitzball_tables_keeps_the_randomized_tables()
    {
        if (Original.Value is not { } original) return;

        var settings = Settings("hold");
        settings.Choices["blitz"] = "random";
        var result = RandomizerEngine.Run(original, settings, new Seed("blitz and cutscenes"));
        Assert.Empty(result.Problems);
        // hiku1900 (Airship - Bridge) holds the tables and gets a skip: the skip's code moves them, and they stay randomized.
        var copy = BlitzballStats.FindCopies(original).Single(c => c.Path == Cutscenes.EventPath("hiku1900"));
        Assert.Contains(Cutscenes.All, s => s.EventId == "hiku1900");
        byte[] written = result.Files[copy.Path];
        Assert.NotEqual(original.ReadFile(copy.Path).Length, written.Length);
        var tables = BlitzballStats.FromCopy(written, copy.In(original.ReadFile(copy.Path), written));
        var reference = BlitzballStats.FindCopies(original).Single(c => c.Path == BlitzballStats.EventPath(BlitzballStats.ReferenceEvent));
        var randomized = BlitzballStats.FromCopy(result.Files[reference.Path], reference.In(original.ReadFile(reference.Path), result.Files[reference.Path]));
        Assert.All(Enum.GetValues<BlitzStat>(), stat => Assert.Equal(randomized.Encode(stat), tables.Encode(stat)));
        Assert.NotEqual(BlitzballStats.Read(original).Encode(BlitzStat.HP), tables.Encode(BlitzStat.HP));
    }

    [Theory]
    [InlineData("bsil0300")] // meta +0x20 pointer
    [InlineData("znkd0801")] // a word that looks like an untracked pointer
    public void Events_the_re_layout_gate_refuses_are_not_edited(string eventId)
    {
        if (Original.Value is not { } original) return;

        var script = new EventScriptEditor(EventFile.Read(original.ReadFile(Cutscenes.EventPath(eventId))).Chunk(EventFile.ScriptChunk)!);
        Assert.NotNull(Cutscenes.RelayoutRisk(script));
        Assert.DoesNotContain(Cutscenes.All, s => s.EventId == eventId);
    }

    [Fact]
    public void Every_known_pointer_still_points_at_the_same_bytes_after_code_is_inserted()
    {
        if (Original.Value is not { } original) return;

        // Including the meta struct's +0x20, which bsil0300 crashed without (it holds a pointer in 25 events).
        foreach (string path in SoftReset.FieldEvents(original).Select(Cutscenes.EventPath))
        {
            byte[] chunk = EventFile.Read(original.ReadFile(path)).Chunk(EventFile.ScriptChunk)!;
            var before = new EventScriptEditor(chunk);
            var fields = before.KnownPointerFields;
            var targets = fields.Select(f => BitConverter.ToInt32(chunk, f)).ToList();
            // Function entry and jump tables hold code offsets, which move with inserted code on purpose.
            var codeTables = Enumerable.Range(0, before.WorkerCount).SelectMany(w => new[] { 0x20, 0x24 }.Select(o => BitConverter.ToInt32(chunk, 0x38 + w * 4) + o)).ToHashSet();
            // (An empty structure can share its address with one of those tables.)
            var codeTableStarts = codeTables.Select(f => BitConverter.ToInt32(chunk, f)).ToHashSet();
            var after = new EventScriptEditor(chunk);
            after.InsertCode(0, new byte[0x40]);
            byte[] edited = after.ToBytes();
            // Checked on its own too, not just as one of the fields the editor lists: the meta struct's +0x20.
            if (before.MetaPointer20 is int was and > 0)
            {
                int now = BitConverter.ToInt32(edited, BitConverter.ToInt32(edited, 0x2C) + 0x20);
                Assert.True(chunk.AsSpan(was, 8).SequenceEqual(edited.AsSpan(now, 8)), $"{path}: the meta struct's +0x20 pointer lost its data");
            }
            for (int i = 0; i < fields.Count; i++)
            {
                // 0x10 is the chunk's end, not a structure; 0x30 the code start, where the inserted code now is.
                if (fields[i] is 0x10 or 0x30 || codeTables.Contains(fields[i]) || codeTableStarts.Contains(targets[i]) || targets[i] <= 0 || targets[i] >= chunk.Length) continue;
                int field = after.Map(fields[i]), target = BitConverter.ToInt32(edited, field);
                int length = Math.Min(8, Math.Min(chunk.Length - targets[i], edited.Length - target));
                Assert.True(length > 0 && chunk.AsSpan(targets[i], length).SequenceEqual(edited.AsSpan(target, length)), $"{path}: the pointer at {fields[i]:X} lost its data");
            }
        }
    }

    [Fact]
    public void Hold_reuses_the_games_own_select_watcher()
    {
        if (Original.Value is not { } original) return;

        byte[] ebp = Cutscenes.Build(original, Campfire, Cutscenes.Hold, SkipButtons.ById("r1"))[Cutscenes.EventPath("znkd0600")];
        var before = new EventScriptEditor(EventFile.Read(original.ReadFile(Cutscenes.EventPath("znkd0600"))).Chunk(0)!);
        var after = new EventScriptEditor(EventFile.Read(ebp).Chunk(0)!);
        var watcher = Campfire.BuiltIn!;

        // Same functions; the watch function now starts in new code that shows the intro prompt, tests whether R1
        // (button 3) is down, polls it, never uses the press-only 0x0046 test, and runs w0D::f03 at level 2.
        Assert.Equal(before.FunctionCount(watcher.Worker), after.FunctionCount(watcher.Worker));
        Assert.Equal(before.JumpCount(watcher.Worker) + 4, after.JumpCount(watcher.Worker));
        int start = after.FunctionEntry(watcher.Worker, watcher.WatchFunction);
        Assert.True(start >= before.CodeLength);
        byte[] code = after.Code(start, after.CodeLength - start).ToArray();
        byte[] show = Atel.Call(Atel.ShowMessage, Campfire.MessageWindow, 0);
        int firstShow = code.AsSpan().IndexOf(show);
        Assert.True(firstShow > 0 && firstShow < code.AsSpan().IndexOf((ReadOnlySpan<byte>)Atel.IsDown(3)), "the intro prompt is shown before the first button test");
        Assert.Equal(Cutscenes.IntroFrames / Cutscenes.IntroPollFrames + 1 + Cutscenes.HoldFrames / Cutscenes.PollFrames, Occurrences(code, Atel.IsDown(3)));
        Assert.Equal(0, Occurrences(code, [0xB5, 0x46, 0x00]));
        // Shown for the intro, shown with an empty bar, then redrawn once per bar step; closed and waited for after the
        // intro, on the skip and on release.
        Assert.Equal(Cutscenes.Steps + 2, Occurrences(code, show));
        byte[] closeAndWait = [.. Atel.Call(Atel.CloseMessage, Campfire.MessageWindow), .. Atel.Call(Atel.WaitForText, Campfire.MessageWindow, 1)];
        Assert.Equal(3, Occurrences(code, closeAndWait));
        // The scene starts the watcher at its first instruction; the game's own start, later in the scene, is blanked out.
        byte[] startWatcher = Atel.Run(1, watcher.Worker, watcher.WatchFunction, awaitEnd: false);
        Assert.Equal(startWatcher, after.Code(after.FunctionEntry(0x0D, 2), startWatcher.Length).ToArray());
        var changes = CutsceneSkipFeature.ChangedCode(original.ReadFile(Cutscenes.EventPath("znkd0600")), ebp);
        var nopped = Assert.Single(changes.Nopped);
        Assert.Equal(watcher.Start.Offset, nopped.Original);
        Assert.Equal(new byte[watcher.Start.Expected.Length], after.Code(nopped.Offset, nopped.Length).ToArray());
        byte[] skip = Atel.Run(2, 0x0D, watcher.SkipFunction, awaitEnd: true);
        int skipAt = code.AsSpan().IndexOf(skip);
        Assert.True(code.AsSpan(0, skipAt).LastIndexOf(closeAndWait) > code.AsSpan(0, skipAt).LastIndexOf(Atel.Call(Atel.ShowMessage, Campfire.MessageWindow, 0)),
            "the prompt is closed after its last redraw and before the skip");
        Assert.True(code.AsSpan().IndexOf((ReadOnlySpan<byte>)Atel.Run(2, 0x0D, watcher.SkipFunction, awaitEnd: true)) > 0);
        // The game's skip function starts with the first-visit guard; its stop function closes the prompt first.
        Assert.Equal(Cutscenes.Guard(Campfire), after.Code(after.FunctionEntry(0x0D, watcher.SkipFunction), Cutscenes.Guard(Campfire).Length).ToArray());
        Assert.Equal(Atel.Call(Atel.CloseMessage, Campfire.MessageWindow), after.Code(after.FunctionEntry(watcher.Worker, watcher.StopFunction), 6).ToArray());
        // The scene's exit moved with its code and the guard jumps there.
        Assert.Equal(after.JumpTarget(0x0D, 0x13), CutsceneSkipFeature.ChangedCode(original.ReadFile(Cutscenes.EventPath("znkd0600")), ebp).Inserted
            .Aggregate(0x4426, (at, r) => r.Offset <= at ? at + r.Length : at));
    }

    [Fact]
    public void The_prompt_has_one_string_per_step_at_the_same_index_in_every_language()
    {
        if (Original.Value is not { } original) return;

        var files = Cutscenes.Build(original, Campfire, Cutscenes.Hold, SkipButtons.Default);
        var us = FfxCharset.FromTableFile(original.ReadFile(FfxCharset.TablePath("us")));
        foreach (string folder in GamePaths.LanguageFolders)
        {
            byte[] before = original.ReadFile(Cutscenes.TextPath(folder, "znkd0600"));
            byte[] after = files[Cutscenes.TextPath(folder, "znkd0600")];
            Assert.Equal(EventText.Count(before) + Cutscenes.PromptCount, EventText.Count(after));
            Assert.Empty(CutsceneSkipFeature.CheckText(before, after, Cutscenes.PromptCount));
        }
        byte[] english = files[Cutscenes.TextPath("new_uspc", "znkd0600")];
        int last = EventText.Count(english) - 1;
        int offset = BitConverter.ToUInt16(english, last * 8);
        Assert.Equal([0x09, 0x30], english[offset..][..2]);                                    // {TIME:00}: show at once
        Assert.Equal([0x0B, 0x39], english[(offset + 2 + us.Encode("Hold ").Length)..][..2]); // the Select button icon
        Assert.Equal("Hold to skip [#####]", us.Decode(english, offset)); // the icon isn't text, so it decodes to nothing
    }

    [Fact]
    public void Original_changes_nothing()
    {
        if (Original.Value is not { } original) return;
        Assert.Empty(RandomizerEngine.Run(original, Settings("orig"), new Seed("cutscenes")).Files);
    }

    [Fact]
    public void An_event_that_isnt_the_expected_version_is_refused()
    {
        if (Original.Value is not { } original) return;

        string path = Cutscenes.EventPath("znkd0600");
        byte[] changed = original.ReadFile(path);
        changed[0x40 + BitConverter.ToInt32(changed, 0x40 + 0x30) + Campfire.SceneStart.Offset] ^= 1;
        byte[]? Read(string p) => p == path ? changed : original.IndexOf(p) >= 0 ? original.ReadFile(p) : null;

        var error = Assert.Throws<EventMismatchException>(() => Cutscenes.Build(Read, [Campfire], Cutscenes.Hold, SkipButtons.Default));
        Assert.Contains("znkd0600", error.Message);
    }

    [Fact]
    public void The_checks_catch_a_jump_left_behind()
    {
        if (Original.Value is not { } original) return;

        string path = Cutscenes.EventPath("znkd0600");
        byte[] before = original.ReadFile(path);
        byte[] after = Cutscenes.Build(original, Campfire, Cutscenes.Hold, SkipButtons.Default)[path];
        Assert.Empty(CutsceneSkipFeature.CheckEvent(before, after, Cutscenes.Hold));

        // Put w0D j13 back at its old address, as a relocation bug would.
        var file = EventFile.Read(after);
        byte[] script = file.Chunk(0)!;
        var editor = new EventScriptEditor(script);
        int moved = editor.JumpTarget(0x0D, 0x13);
        int table = BitConverter.ToInt32(script, BitConverter.ToInt32(script, 0x38 + 0x0D * 4) + 0x24);
        BitConverter.TryWriteBytes(script.AsSpan(table + 0x13 * 4), 0x4426);
        file.SetChunk(0, script);
        Assert.NotEqual(0x4426, moved);
        Assert.Contains(CutsceneSkipFeature.CheckEvent(before, file.ToBytes(), Cutscenes.Hold), p => p.Contains("j13"));
    }

    [Fact]
    public void The_skip_button_shows_in_the_summary_only_for_hold()
    {
        Assert.Contains(SpoilerLog.Describe(Settings("hold", "r1")), l => l == "Skip cutscenes: Hold button to skip, skip button R1 (RB)");
        Assert.Contains(SpoilerLog.Describe(Settings("always", "r1")), l => l == "Skip cutscenes: Always skip");
        Assert.False(Settings("hold").SameAs(Settings("hold", "r1")));
        Assert.True(Settings("hold").SameAs(Settings("hold", "select")));
    }

    [Fact]
    public void Xbox_buttons_map_to_the_games_buttons()
    {
        Assert.Equal("select", SkipButtons.FromXbox("Back")!.Id);
        Assert.Equal("x", SkipButtons.FromXbox("A")!.Id);
        Assert.Equal("square", SkipButtons.FromXbox("X")!.Id);
        Assert.Equal(8, SkipButtons.ById("select").Index);
        Assert.Null(SkipButtons.FromXbox("LeftThumb"));
        Assert.Null(SkipButtons.FromXbox("DPadUp"));
    }
}
