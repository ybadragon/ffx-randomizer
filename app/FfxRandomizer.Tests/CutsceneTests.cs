using System.Buffers.Binary;
using FfxRandomizer.Core.Game;
using FfxRandomizer.Core.GameData;
using FfxRandomizer.Core.Patching;
using FfxRandomizer.Core.Randomization;
using FfxRandomizer.Core.Randomization.Features;
using FfxRandomizer.Core.Storage;
using FfxRandomizer.Core.Vbf;

namespace FfxRandomizer.Tests;

/// <summary>Skip cutscenes. Code insertion runs on a built event everywhere; the rest reads the installed game (read-only).</summary>
public class CutsceneTests
{
    private static readonly Lazy<VbfArchive?> Original = new(() =>
        GameInstall.FindSteamInstall() is { } game ? new PatchManager(game.DataArchivePath, AppPaths.Default).OpenOriginal() : null);

    private static RandomizerSettings Settings(string mode)
    {
        var s = new RandomizerSettings();
        s.Choices["cutscenes"] = mode;
        return s;
    }

    /// <summary>
    /// A minimal event: one worker with two functions (at code 0 and 4) and two jumps (at 4 and 6), 8 bytes of code,
    /// 16 bytes of zero padding and then 4 bytes of shared data, laid out like a real script chunk at 0x40.
    /// </summary>
    private static byte[] TinyEvent()
    {
        const int script = 0x40, worker = 0x38 + 4, entries = worker + 0x34, jumps = entries + 8, code = jumps + 8, shared = code + 8 + 16;
        var bytes = new byte[script + shared + 4 + 12];
        void U32(int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), value);
        void U16(int at, int value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), (ushort)value);
        U32(0, 0x31305645);
        U32(4, script);
        U32(script + 0x00, 8);
        U32(script + 0x10, shared + 4);
        U32(script + 0x30, code);
        U16(script + 0x34, 1);
        U32(script + 0x38, worker);
        U16(script + worker + 0x08, 2);
        U16(script + worker + 0x0A, 2);
        U32(script + worker + 0x20, entries);
        U32(script + worker + 0x24, jumps);
        U32(script + worker + 0x30, shared);
        U32(script + entries, 0);
        U32(script + entries + 4, 4);
        U32(script + jumps, 4);
        U32(script + jumps + 4, 6);
        byte[] program = [0xAE, 0x01, 0x00, 0x3C, 0xB0, 0x01, 0x00, 0x3C];
        program.CopyTo(bytes, script + code);
        bytes[script + shared] = 0x7F;
        return bytes;
    }

    [Fact]
    public void Inserting_code_shifts_later_code_entries_and_jumps_into_the_padding()
    {
        byte[] before = TinyEvent();
        byte[] after = EventScript.InsertCode(before, 4, [0x00, 0x00, 0x00]);
        var script = new EventScript(after);

        Assert.Equal(before.Length, after.Length);
        Assert.Equal(11, script.CodeLength);
        Assert.Equal([0xAE, 0x01, 0x00, 0x3C, 0x00, 0x00, 0x00, 0xB0, 0x01, 0x00, 0x3C], script.Code(0, 11).ToArray());
        Assert.Equal(0, script.FunctionEntry(0, 0));
        Assert.Equal(4, script.FunctionEntry(0, 1));  // exactly at the insertion: now runs the inserted code first
        Assert.Equal(4, script.JumpTarget(0, 0));
        Assert.Equal(9, script.JumpTarget(0, 1));     // past it: moved with its code
        Assert.Equal(13, script.CodePadding());
        Assert.Equal(0x7F, after[Array.IndexOf(before, (byte)0x7F)]); // shared data didn't move
    }

    [Fact]
    public void Inserting_more_than_the_padding_holds_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => EventScript.InsertCode(TinyEvent(), 0, new byte[17]));
    }

    [Theory]
    [InlineData("hold")]
    [InlineData("always")]
    [InlineData("anybutton")]
    [InlineData("press")]
    public void Every_mode_inserts_the_check_and_passes_every_check(string mode)
    {
        if (Original.Value is not { } original) return;

        var result = RandomizerEngine.Run(original, Settings(mode), new Seed("cutscenes"));
        Assert.Empty(result.Problems);
        var scene = Cutscenes.All[0];
        var patch = CutsceneSkipFeature.PatchFor(scene, mode);
        byte[] before = original.ReadFile(patch.Path), after = result.Files[patch.Path];

        Assert.Equal(before.Length, after.Length);
        Assert.Equal(VbfArchive.BlocksFor((ulong)before.Length), VbfArchive.BlocksFor((ulong)after.Length));
        var script = new EventScript(after);
        Assert.Equal(patch.Bytes, script.Code(patch.CodeOffset, patch.Bytes.Length).ToArray());
        Assert.Equal(patch.Expected, script.Code(patch.CodeOffset + patch.Bytes.Length, patch.Expected.Length).ToArray());
        // Hold/always check at the start of the scene (w0D::f02); press at the start of the game's own skip handler (f03).
        Assert.Equal(patch.CodeOffset, script.FunctionEntry(0x0D, mode == "press" ? 3 : 2));
        Assert.Equal(0x4426 + patch.Bytes.Length, script.JumpTarget(0x0D, 0x13));
        Assert.Equal(mode is "hold" or "anybutton", patch.Bytes.AsSpan().IndexOf((ReadOnlySpan<byte>)[0xB5, 0x46, 0x00]) >= 0);
        if (mode == "anybutton") Assert.True(patch.Bytes.AsSpan().IndexOf((ReadOnlySpan<byte>)[0xAE, 0xFF, 0xFF, 0x05]) >= 0);
    }

    [Fact]
    public void Original_changes_nothing()
    {
        if (Original.Value is not { } original) return;
        Assert.Empty(RandomizerEngine.Run(original, Settings("orig"), new Seed("cutscenes")).Files);
    }

    [Fact]
    public void A_patch_refuses_code_that_isnt_the_version_it_was_made_for()
    {
        if (Original.Value is not { } original) return;

        var patch = Cutscenes.All[0].Always;
        byte[] once = patch.Apply(original.ReadFile(patch.Path));
        var error = Assert.Throws<EventPatchMismatchException>(() => patch.Apply(once));
        Assert.Contains("znkd0600", error.Message);
    }

    [Fact]
    public void The_checks_catch_a_jump_that_didnt_move()
    {
        if (Original.Value is not { } original) return;

        var patch = Cutscenes.All[0].HoldSelect;
        byte[] before = original.ReadFile(patch.Path);
        byte[] after = patch.Apply(before);
        Assert.Empty(CutsceneSkipFeature.CheckInsertion(before, after, patch));

        // Put w0D j13 back at its old address, as a relocation bug would.
        var script = new EventScript(before);
        int slot = script.EntryAndJumpSlots().First(s => BinaryPrimitives.ReadInt32LittleEndian(after.AsSpan(s)) == 0x4426 + patch.Bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(after.AsSpan(slot), 0x4426);
        Assert.Contains(CutsceneSkipFeature.CheckInsertion(before, after, patch), p => p.Contains("j13"));
    }
}
