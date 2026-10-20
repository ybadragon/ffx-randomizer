using FfxRandomizer.Core.Game;
using FfxRandomizer.Core.GameData;
using FfxRandomizer.Core.GameData.Events;
using FfxRandomizer.Core.Patching;
using FfxRandomizer.Core.Randomization;
using FfxRandomizer.Core.Randomization.Features;
using FfxRandomizer.Core.Storage;
using FfxRandomizer.Core.Vbf;

namespace FfxRandomizer.Tests;

/// <summary>Soft reset. Reads the installed game (read-only) and passes trivially without it.</summary>
public class SoftResetTests
{
    // Soft reset isn't released; these tests switch it on.
    public SoftResetTests() => SoftResetFeature.Available = true;

    private static readonly Lazy<VbfArchive?> Original = new(() =>
        GameInstall.FindSteamInstall() is { } game ? new PatchManager(game.DataArchivePath, AppPaths.Default).OpenOriginal() : null);

    /// <summary>The default combo, L1 + R1 + Start.</summary>
    private static readonly int[] Default = SoftReset.ComboIndexes(null);

    private static RandomizerSettings Settings(params string[] options)
    {
        var s = new RandomizerSettings();
        foreach (string o in options)
            if (o.Split('=') is [var group, var choice]) s.Choices[group] = choice;
            else s.Toggles[o] = true;
        return s;
    }

    [Fact]
    public void Every_event_file_reads_and_writes_back_unchanged()
    {
        if (Original.Value is not { } original) return;

        // Including slik1100, whose chunk 1 is empty and starts where chunk 2 does.
        foreach (string path in original.Names.Where(n => n.EndsWith(".ebp", StringComparison.OrdinalIgnoreCase)))
        {
            byte[] bytes = original.ReadFile(path);
            Assert.True(EventFile.Read(bytes).ToBytes().AsSpan().SequenceEqual(bytes), path);
        }
    }

    [Theory]
    [InlineData("slik1100")] // an empty chunk
    [InlineData("znkd1300")] // has a skippable scene
    [InlineData("bltz0002")] // holds the Blitzball tables
    public void The_watcher_replaces_an_unused_Scenario_main_and_leaves_the_rest_alone(string eventId)
    {
        if (Original.Value is not { } original) return;

        byte[] before = original.ReadFile(Cutscenes.EventPath(eventId));
        var (after, reason) = SoftReset.Apply(before, SoftReset.CutsceneWorkers(eventId), Default);
        Assert.True(after is not null, reason);
        Assert.True(SoftResetFeature.HasWatcher(after, Default));
        Assert.Empty(CutsceneSkipFeature.CheckEvent(before, after, Cutscenes.Original));
        Assert.Equal(VbfArchive.BlocksFor((ulong)before.Length), VbfArchive.BlocksFor((ulong)after.Length));
        // Only the code and the tables after it grow (the watcher is under 1 KB).
        Assert.InRange(after.Length - before.Length, 1, 1024);
        // The host is a Scenario the scene skip doesn't use.
        var tables = new ScriptTables(EventFile.Read(after).Chunk(EventFile.ScriptChunk)!);
        var script = tables.Editor;
        var host = Enumerable.Range(0, script.WorkerCount).Single(w => script.FunctionCount(w) > 1 && script.JumpCount(w) > 0
            && script.FunctionEntry(w, 1) == script.JumpTarget(w, script.JumpCount(w) - 1) && script.FunctionEntry(w, 1) >= script.CodeLength - SoftReset.WatcherCode(0, Default).Length);
        Assert.Equal(4, tables.WorkerType(host));
        Assert.DoesNotContain(host, SoftReset.CutsceneWorkers(eventId));
    }

    [Theory]
    [InlineData("kami0000")]
    [InlineData("bsil0600")] // has a skippable scene
    [InlineData("bsvr0000")] // 46 references to renumber
    public void Without_an_unused_Scenario_the_watcher_gets_a_worker_of_its_own(string eventId)
    {
        if (Original.Value is not { } original) return;

        byte[] before = original.ReadFile(Cutscenes.EventPath(eventId));
        var (after, reason) = SoftReset.Apply(before, SoftReset.CutsceneWorkers(eventId), Default);
        Assert.True(after is not null, reason);
        Assert.True(SoftResetFeature.HasWatcher(after, Default));
        Assert.Empty(SoftReset.CheckOwnWorker(before, after, Default));
        Assert.Equal(VbfArchive.BlocksFor((ulong)before.Length), VbfArchive.BlocksFor((ulong)after.Length));

        var b = new ScriptTables(EventFile.Read(before).Chunk(EventFile.ScriptChunk)!);
        var a = new ScriptTables(EventFile.Read(after).Chunk(EventFile.ScriptChunk)!);
        int index = SoftReset.NewWorkerPlan(b).Index;
        Assert.Equal(b.WorkerCount + 1, a.WorkerCount);
        Assert.Equal(4, a.WorkerType(index));
        // It goes after the last FieldObject or Scenario, so the types stay in order.
        Assert.All(Enumerable.Range(0, index), w => Assert.Contains(a.WorkerType(w), new[] { 1, 4 }));
        Assert.All(Enumerable.Range(index + 1, a.WorkerCount - index - 1), w => Assert.DoesNotContain(a.WorkerType(w), new[] { 1, 4 }));

        // Breaking one renumbered reference is noticed.
        var patch = SoftReset.NewWorkerPlan(b).Patches.First();
        var file = EventFile.Read(after);
        byte[] chunk = file.Chunk(EventFile.ScriptChunk)!;
        int at = a.Editor.CodeStart + patch.Offset + 1;
        chunk[at]--;
        file.SetChunk(EventFile.ScriptChunk, chunk);
        Assert.NotEmpty(SoftReset.CheckOwnWorker(before, file.ToBytes(), Default));
    }

    [Theory]
    [InlineData("select+l2+r2", new[] { 8, 0, 1 })]
    [InlineData("l1+r1+start", new[] { 2, 3, 11 })] // the default
    [InlineData("select", new[] { 8 })]
    public void The_watcher_waits_a_second_on_the_combo_then_goes_to_the_title(string combo, int[] buttons)
    {
        Assert.Equal(buttons, SoftReset.ComboIndexes(combo));
        byte[] code = SoftReset.WatcherCode(5, buttons);
        int polls = SoftReset.HoldFrames / SoftReset.PollFrames + 1;
        // One "button is down" test per button per poll, never the press-only 0x0046.
        foreach (int button in buttons)
            Assert.Equal(polls, Occurrences(code, [.. Atel.IsDown(button)]));
        Assert.Equal(polls, Occurrences(code, Atel.Call(Atel.Wait, SoftReset.PollFrames)));
        Assert.Equal(0, Occurrences(code, [0xB5, 0x46, 0x00]));
        Assert.Equal(1, Occurrences(code, Atel.Call(SoftReset.SetRoomFadeoutTime, 15)));
        Assert.Equal(1, Occurrences(code, Atel.Call(SoftReset.SetRoomFadeoutMode, 0)));
        Assert.Equal(1, Occurrences(code, Atel.Call(SoftReset.TransitionToRoom, 23, 0)));
    }

    [Fact]
    public void A_recorded_combo_is_kept_and_odd_ones_fall_back_to_the_default()
    {
        // Any 1-3 different buttons, written in the skip list's order.
        Assert.Equal("select+r1+start", SoftReset.ComboId([SkipButtons.ById("start"), SkipButtons.ById("select"), SkipButtons.ById("r1")]));
        Assert.True(SoftReset.IsCombo("select+r1+start"));
        Assert.False(SoftReset.IsCombo("r1+select")); // not in order
        Assert.False(SoftReset.IsCombo("l1+r1+l2+r2")); // four
        Assert.False(SoftReset.IsCombo("l1+l1"));
        Assert.False(SoftReset.IsCombo("dpad"));

        var settings = Settings("softreset", "softresetbuttons=select+r1+start");
        Assert.Equal("select+r1+start", SoftResetFeature.Combo(settings));
        Assert.Equal([8, 3, 11], SoftResetFeature.Buttons(settings));
        Assert.Equal("l1+r1+start", SoftResetFeature.Combo(Settings("softreset", "softresetbuttons=nonsense")));
        Assert.Equal([2, 3, 11], SoftResetFeature.Buttons(Settings("softreset")));
    }

    [Fact]
    public void The_combo_warns_when_it_holds_the_cutscene_skip_button()
    {
        Assert.NotNull(SoftReset.OverlapWarning("l1+r1+start", Cutscenes.Hold, SkipButtons.ById("r1")));
        Assert.Null(SoftReset.OverlapWarning("l1+r1+start", Cutscenes.Always, SkipButtons.ById("r1")));
        Assert.Null(SoftReset.OverlapWarning("l1+r1+start", Cutscenes.Hold, SkipButtons.Default));
    }

    [Fact]
    public void Stick_clicks_are_not_offered()
    {
        // Never seen held on PC (as buttons 9 and 10).
        Assert.Null(SkipButtons.FromXbox("LeftThumb"));
        Assert.Null(SkipButtons.FromXbox("RightThumb"));
        Assert.DoesNotContain(SkipButtons.All, b => b.Index is 9 or 10);
    }

    [Fact]
    public void Soft_reset_combines_with_cutscene_skips_and_Blitzball()
    {
        if (Original.Value is not { } original) return;

        var result = RandomizerEngine.Run(original, Settings("softreset", "cutscenes=hold", "blitz=random"), new Seed("softreset"));
        Assert.Empty(result.Problems);
        foreach (var (path, bytes) in result.Files)
            Assert.Equal(VbfArchive.BlocksFor((ulong)original.ReadFile(path).Length), VbfArchive.BlocksFor((ulong)bytes.Length));

        // A skippable scene's event has both edits; the Blitzball event has the watcher and the new tables, moved along.
        string scene = Cutscenes.EventPath("bsil0600");
        Assert.True(SoftResetFeature.HasWatcher(result.Files[scene], Default));
        // bsil0600 gets a worker of its own, added to the cutscene skip's edit (whose runs get renumbered with the rest).
        byte[] build = Cutscenes.Build(original, [.. Cutscenes.All.Where(s => s.EventId == "bsil0600")], Cutscenes.Hold, SkipButtons.Default)[scene];
        Assert.Empty(SoftReset.CheckOwnWorker(build, result.Files[scene], Default));
        var copy = BlitzballStats.FindCopies(original).First(c => c.Path == BlitzballStats.EventPath(BlitzballStats.ReferenceEvent));
        byte[] blitz = result.Files[copy.Path];
        Assert.True(SoftResetFeature.HasWatcher(blitz, Default));
        var tables = BlitzballStats.FromCopy(blitz, copy.In(original.ReadFile(copy.Path), blitz));
        Assert.NotEqual(BlitzballStats.Read(original)[5, BlitzStat.HP], tables[5, BlitzStat.HP]);
    }

    [Fact]
    public void Validate_notices_a_changed_watcher()
    {
        if (Original.Value is not { } original) return;

        var context = new RandomizerContext(original, Settings("softreset"), new Seed("softreset"));
        var feature = new SoftResetFeature();
        feature.Apply(context);
        Assert.Empty(feature.Validate(context));

        // Send one event's reset to room 24 instead of the title.
        string path = Cutscenes.EventPath("kami0000");
        byte[] bytes = (byte[])context.ChangedFiles[path].Clone();
        byte[] call = Atel.Call(SoftReset.TransitionToRoom, 23, 0);
        int at = bytes.AsSpan().IndexOf(call);
        bytes[at + 1] = 24;
        context.Replace(path, bytes);
        Assert.Contains(feature.Validate(context), p => p.Contains("kami0000"));
    }

    private static int Occurrences(byte[] haystack, byte[] needle)
    {
        int count = 0;
        for (int at = haystack.AsSpan().IndexOf(needle); at >= 0; at = haystack.AsSpan(at + 1).IndexOf(needle) is int next and >= 0 ? at + 1 + next : -1) count++;
        return count;
    }
}
