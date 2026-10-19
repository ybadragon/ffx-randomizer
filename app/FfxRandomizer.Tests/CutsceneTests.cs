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
        Assert.Equal(mode == "hold" ? 9 : 1, result.Files.Count); // the event, plus every language's text for the prompt
        foreach (var (path, bytes) in result.Files)
            Assert.Equal(VbfArchive.BlocksFor((ulong)original.ReadFile(path).Length), VbfArchive.BlocksFor((ulong)bytes.Length));
    }

    [Fact]
    public void Hold_reuses_the_games_own_select_watcher()
    {
        if (Original.Value is not { } original) return;

        byte[] ebp = Cutscenes.Build(original, Campfire, Cutscenes.Hold, SkipButtons.ById("r1"))[Cutscenes.EventPath("znkd0600")];
        var before = new EventScriptEditor(EventFile.Read(original.ReadFile(Cutscenes.EventPath("znkd0600"))).Chunk(0)!);
        var after = new EventScriptEditor(EventFile.Read(ebp).Chunk(0)!);
        var watcher = Campfire.BuiltIn!;

        // Same functions; the watch function now starts in new code that tests whether R1 (button 3) is down, polls it,
        // never uses the press-only 0x0046 test, and runs w0D::f03 at level 2.
        Assert.Equal(before.FunctionCount(watcher.Worker), after.FunctionCount(watcher.Worker));
        Assert.Equal(before.JumpCount(watcher.Worker) + 3, after.JumpCount(watcher.Worker));
        int start = after.FunctionEntry(watcher.Worker, watcher.WatchFunction);
        Assert.True(start >= before.CodeLength);
        byte[] code = after.Code(start, after.CodeLength - start).ToArray();
        Assert.True(code.AsSpan().IndexOf((ReadOnlySpan<byte>)Atel.IsDown(3)) == 0);
        Assert.Equal(1 + Cutscenes.HoldFrames / Cutscenes.PollFrames, Occurrences(code, Atel.IsDown(3)));
        Assert.Equal(0, Occurrences(code, [0xB5, 0x46, 0x00]));
        // Shown, then redrawn once per bar step; closed and waited for on both the skip and the release paths.
        Assert.Equal(1 + Cutscenes.Steps, Occurrences(code, Atel.Call(Atel.ShowMessage, Campfire.MessageWindow, 0)));
        byte[] closeAndWait = [.. Atel.Call(Atel.CloseMessage, Campfire.MessageWindow), .. Atel.Call(Atel.WaitForText, Campfire.MessageWindow, 1)];
        Assert.Equal(2, Occurrences(code, closeAndWait));
        byte[] skip = Atel.Run(2, 0x0D, watcher.SkipFunction, awaitEnd: true);
        int skipAt = code.AsSpan().IndexOf(skip);
        Assert.True(code.AsSpan(0, skipAt).LastIndexOf(closeAndWait) > code.AsSpan(0, skipAt).LastIndexOf(Atel.Call(Atel.ShowMessage, Campfire.MessageWindow, 0)),
            "the prompt is closed after its last redraw and before the skip");
        Assert.True(code.AsSpan().IndexOf((ReadOnlySpan<byte>)Atel.Run(2, 0x0D, watcher.SkipFunction, awaitEnd: true)) > 0);
        // The game's skip function starts with the first-visit guard; its stop function closes the prompt first.
        Assert.Equal(Cutscenes.Guard(Campfire), after.Code(after.FunctionEntry(0x0D, watcher.SkipFunction), Cutscenes.Guard(Campfire).Length).ToArray());
        Assert.Equal(Atel.Call(Atel.CloseMessage, Campfire.MessageWindow), after.Code(after.FunctionEntry(watcher.Worker, watcher.StopFunction), 6).ToArray());
        // The scene's exit moved with its code and the guard jumps there.
        Assert.Equal(after.JumpTarget(0x0D, 0x13), CutsceneSkipFeature.InsertedCode(original.ReadFile(Cutscenes.EventPath("znkd0600")), ebp)
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
            Assert.Equal(EventText.Count(before) + Cutscenes.Steps + 1, EventText.Count(after));
            Assert.Empty(CutsceneSkipFeature.CheckText(before, after, Cutscenes.Steps + 1));
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

        var error = Assert.Throws<EventMismatchException>(() => Cutscenes.Build(Read, Campfire, Cutscenes.Hold, SkipButtons.Default));
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
    }
}
