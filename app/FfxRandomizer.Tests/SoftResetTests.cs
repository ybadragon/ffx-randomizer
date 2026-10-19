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
    private static readonly Lazy<VbfArchive?> Original = new(() =>
        GameInstall.FindSteamInstall() is { } game ? new PatchManager(game.DataArchivePath, AppPaths.Default).OpenOriginal() : null);

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
    [InlineData("kami0000")]
    [InlineData("bsil0600")] // has a skippable scene
    [InlineData("bltz0002")] // holds the Blitzball tables
    public void The_watcher_replaces_an_idle_main_and_leaves_the_rest_alone(string eventId)
    {
        if (Original.Value is not { } original) return;

        byte[] before = original.ReadFile(Cutscenes.EventPath(eventId));
        var (after, reason) = SoftReset.Apply(before, SoftReset.CutsceneWorkers(eventId));
        Assert.True(after is not null, reason);
        Assert.True(SoftResetFeature.HasWatcher(after));
        Assert.Empty(CutsceneSkipFeature.CheckEvent(before, after, Cutscenes.Original));
        Assert.Equal(VbfArchive.BlocksFor((ulong)before.Length), VbfArchive.BlocksFor((ulong)after.Length));
        // Only the code and the tables after it grow (the watcher is under 1 KB).
        Assert.InRange(after.Length - before.Length, 1, 1024);
        // The watcher's main isn't a worker the scene skip uses there.
        var script = new EventScriptEditor(EventFile.Read(after).Chunk(EventFile.ScriptChunk)!);
        var host = Enumerable.Range(0, script.WorkerCount).Single(w => script.FunctionCount(w) > 1 && script.JumpCount(w) > 0
            && script.FunctionEntry(w, 1) == script.JumpTarget(w, script.JumpCount(w) - 1) && script.FunctionEntry(w, 1) >= script.CodeLength - SoftReset.WatcherCode(0).Length);
        Assert.DoesNotContain(host, SoftReset.CutsceneWorkers(eventId));
    }

    [Fact]
    public void The_watcher_waits_a_second_on_L1_R1_Start_then_goes_to_the_title()
    {
        byte[] code = SoftReset.WatcherCode(5);
        int polls = SoftReset.HoldFrames / SoftReset.PollFrames + 1;
        // One "button is down" test per button per poll, never the press-only 0x0046.
        foreach (int button in SoftReset.Buttons)
            Assert.Equal(polls, Occurrences(code, [.. Atel.IsDown(button)]));
        Assert.Equal(polls, Occurrences(code, Atel.Call(Atel.Wait, SoftReset.PollFrames)));
        Assert.Equal(0, Occurrences(code, [0xB5, 0x46, 0x00]));
        Assert.Equal(1, Occurrences(code, Atel.Call(SoftReset.SetRoomFadeoutTime, 15)));
        Assert.Equal(1, Occurrences(code, Atel.Call(SoftReset.SetRoomFadeoutMode, 0)));
        Assert.Equal(1, Occurrences(code, Atel.Call(SoftReset.TransitionToRoom, 23, 0)));
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
        Assert.True(SoftResetFeature.HasWatcher(result.Files[scene]));
        Assert.True(CutsceneSkipFeature.ChangedCode(original.ReadFile(scene), result.Files[scene]).Inserted.Count > 1);
        var copy = BlitzballStats.FindCopies(original).First(c => c.Path == BlitzballStats.EventPath(BlitzballStats.ReferenceEvent));
        byte[] blitz = result.Files[copy.Path];
        Assert.True(SoftResetFeature.HasWatcher(blitz));
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
