using System.Buffers.Binary;
using FfxRandomizer.Core.Game;
using FfxRandomizer.Core.GameData;
using FfxRandomizer.Core.GameData.Events;
using FfxRandomizer.Core.GameData.Text;
using FfxRandomizer.Core.Patching;
using FfxRandomizer.Core.Randomization.Features;
using FfxRandomizer.Core.Storage;
using FfxRandomizer.Core.Vbf;

namespace FfxRandomizer.Tests;

/// <summary>Event file editing: chunks, the script re-layout, string tables and text encoding.</summary>
public class EventEditingTests
{
    private static readonly Lazy<VbfArchive?> Original = new(() =>
        GameInstall.FindSteamInstall() is { } game ? new PatchManager(game.DataArchivePath, AppPaths.Default).OpenOriginal() : null);

    // Events of different sizes and shapes: Zanarkand, Besaid, Luca (Blitzball tables), the Blitzball stadium (5 MB),
    // the airship, Thunder Plains and Kilika.
    private static readonly string[] SampleEvents = ["znkd0600", "bsil0300", "luca0400", "bltz0002", "azit0000", "kami0000", "klyt1000"];

    /// <summary>
    /// A script chunk shaped like the game's: one worker with two functions (code 0 and 4) and two jumps (4 and 6),
    /// 8 bytes of code then 0x18 bytes of zero padding, 16 bytes of shared data, 4 bytes of event data, the tables,
    /// and 4 bytes of private data.
    /// </summary>
    private static byte[] TinyScript()
    {
        const int worker = 0x3C, code = 0x70, shared = 0x90, eventData = 0xA0, entries = 0xA4, jumps = 0xAC, priv = 0xB4, end = 0xC0;
        var s = new byte[end];
        void U32(int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(s.AsSpan(at), value);
        void U16(int at, int value) => BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(at), (ushort)value);
        U32(0x00, 8);
        U32(0x10, end);
        U32(0x20, eventData);
        U32(0x30, code);
        U16(0x34, 1);
        U32(0x38, worker);
        U16(worker + 0x08, 2);
        U16(worker + 0x0A, 2);
        U32(worker + 0x20, entries);
        U32(worker + 0x24, jumps);
        U32(worker + 0x2C, priv);
        U32(worker + 0x30, shared);
        U32(entries, 0);
        U32(entries + 4, 4);
        U32(jumps, 4);
        U32(jumps + 4, 6);
        byte[] program = [0xAE, 0x01, 0x00, 0x3C, 0xB0, 0x01, 0x00, 0x3C];
        program.CopyTo(s, code);
        s[shared] = 0x5A;
        s[eventData] = 0x6B;
        s[priv] = 0x7C;
        return s;
    }

    private static int U32(byte[] b, int at) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at));

    [Fact]
    public void Small_code_insertions_are_absorbed_by_the_padding_after_the_code()
    {
        var editor = new EventScriptEditor(TinyScript());
        editor.InsertCode(4, [0x00, 0x00, 0x00]);
        byte[] s = editor.ToBytes();

        Assert.Equal(11, editor.CodeLength);
        Assert.Equal([0xAE, 0x01, 0x00, 0x3C, 0x00, 0x00, 0x00, 0xB0, 0x01, 0x00, 0x3C], editor.Code(0, 11).ToArray());
        Assert.Equal(0, editor.FunctionEntry(0, 0));
        Assert.Equal(4, editor.FunctionEntry(0, 1)); // exactly at the insertion: now runs the inserted code first
        Assert.Equal(4, editor.JumpTarget(0, 0));
        Assert.Equal(9, editor.JumpTarget(0, 1));    // past it: moved with its code
        Assert.Equal(0x90, U32(s, 0x3C + 0x30));     // nothing after the code moved
        Assert.Equal(0x5A, s[0x90]);
        Assert.Equal(0xC0, s.Length);
    }

    [Fact]
    public void Larger_insertions_move_everything_after_the_code_by_a_multiple_of_16()
    {
        var editor = new EventScriptEditor(TinyScript());
        int at = editor.AppendCode(new byte[20]);
        byte[] s = editor.ToBytes();

        Assert.Equal(8, at);
        Assert.Equal(28, editor.CodeLength);
        int shared = U32(s, 0x3C + 0x30);
        Assert.Equal(0, (shared - 0x90) % 16);
        Assert.True(shared >= 0x70 + 28 + 0x10, "keeps at least 0x10 bytes of padding after the code");
        Assert.Equal(0x5A, s[shared]);
        Assert.Equal(0x6B, s[U32(s, 0x20)]);
        Assert.Equal(0x7C, s[U32(s, 0x3C + 0x2C)]);
        Assert.Equal(shared - 0x90, editor.Map(0x90) - 0x90);
        Assert.Equal(0, s.Length % 16);
    }

    [Fact]
    public void Adding_functions_and_jumps_grows_the_tables_and_moves_what_follows()
    {
        var editor = new EventScriptEditor(TinyScript());
        int start = editor.AppendCode([0x3C]);
        Assert.Equal(2, editor.AddFunction(0, start));
        Assert.Equal(2, editor.AddJump(0, start));
        byte[] s = editor.ToBytes();

        Assert.Equal(3, editor.FunctionCount(0));
        Assert.Equal(3, editor.JumpCount(0));
        Assert.Equal(start, editor.FunctionEntry(0, 2));
        Assert.Equal(start, editor.JumpTarget(0, 2));
        Assert.Equal([0, 4], new[] { editor.FunctionEntry(0, 0), editor.FunctionEntry(0, 1) });
        Assert.Equal([4, 6], new[] { editor.JumpTarget(0, 0), editor.JumpTarget(0, 1) });
        Assert.Equal(0x7C, s[U32(s, 0x3C + 0x2C)]); // private data followed the tables
        Assert.Equal(0, U32(s, 0x10) % 16);
    }

    [Fact]
    public void Only_zero_padding_is_ever_reclaimed()
    {
        byte[] script = TinyScript();
        script[0x70 + 8 + 2] = 0xEE; // something non-zero right after the code
        var editor = new EventScriptEditor(script);
        editor.InsertCode(0, [0x00]);
        byte[] s = editor.ToBytes();
        Assert.Single(s, b => b == 0xEE);                // still there
        Assert.Equal(0x90 + 16, U32(s, 0x3C + 0x30));    // so the data after it moved instead
    }

    [Fact]
    public void Events_and_string_tables_round_trip_byte_for_byte()
    {
        if (Original.Value is not { } original) return;
        foreach (string id in SampleEvents)
        {
            byte[] ebp = original.ReadFile(Cutscenes.EventPath(id));
            Assert.Equal(ebp, EventFile.Read(ebp).ToBytes());
            var file = EventFile.Read(ebp);
            byte[] script = file.Chunk(EventFile.ScriptChunk)!;
            byte[] rebuilt = new EventScriptEditor(script).ToBytes();
            Assert.Equal(script.AsSpan(0, rebuilt.Length).ToArray(), rebuilt);
            Assert.All(script.AsSpan(rebuilt.Length).ToArray(), b => Assert.Equal(0, b)); // only padding after the end
        }
    }

    [Fact]
    public void Real_events_keep_every_original_byte_and_label_through_insertions_and_new_tables()
    {
        if (Original.Value is not { } original) return;
        foreach (string id in SampleEvents)
        {
            byte[] ebp = original.ReadFile(Cutscenes.EventPath(id));
            var file = EventFile.Read(ebp);
            var editor = new EventScriptEditor(file.Chunk(EventFile.ScriptChunk)!);
            int last = editor.WorkerCount - 1;
            editor.InsertCode(editor.FunctionEntry(0, 0), [0x2B, 0x2B, 0x2B]);           // before a function
            editor.InsertCode(editor.FunctionEntry(last, editor.FunctionCount(last) - 1), new byte[40]);
            int added = editor.AppendCode(Enumerable.Repeat((byte)0x3C, 300).ToArray()); // more than any padding
            editor.AddFunction(last, added);
            editor.AddJump(0, added + 10);
            editor.AddJump(last, added + 20);
            file.SetChunk(EventFile.ScriptChunk, editor.ToBytes());
            byte[] patched = file.ToBytes();

            Assert.Equal(VbfArchive.BlocksFor((ulong)ebp.Length), VbfArchive.BlocksFor((ulong)patched.Length));
            Assert.Empty(CutsceneSkipFeature.CheckEvent(ebp, patched, Cutscenes.Always));
        }
    }

    [Fact]
    public void Appended_strings_follow_the_originals_which_stay_intact()
    {
        if (Original.Value is not { } original) return;
        byte[] table = original.ReadFile(Cutscenes.TextPath("new_uspc", "znkd0600"));
        var charset = FfxCharset.FromTableFile(original.ReadFile(FfxCharset.TablePath("us")));
        byte[] grown = EventText.Append(table, [charset.Encode("One"), charset.Encode("Two")]);

        Assert.Equal(EventText.Count(table) + 2, EventText.Count(grown));
        Assert.Empty(CutsceneSkipFeature.CheckText(table, grown, 2));
        int two = BinaryPrimitives.ReadUInt16LittleEndian(grown.AsSpan((EventText.Count(grown) - 1) * 8));
        Assert.Equal("Two", charset.Decode(grown, two));
    }

    [Theory]
    [InlineData("us", "Hold to skip [###--] 0123 abc")]
    [InlineData("jp", "Hold to skip ***-- 0123")]
    [InlineData("ch", "Hold to skip ***--")]
    [InlineData("kr", "Hold to skip ***--")]
    public void Text_encodes_and_decodes_back(string charsetId, string text)
    {
        if (Original.Value is not { } original) return;
        var charset = FfxCharset.FromTableFile(original.ReadFile(FfxCharset.TablePath(charsetId)));
        byte[] encoded = [.. charset.Encode(text), 0];
        string decoded = charset.Decode(encoded, 0)!;
        // Fullwidth fonts give fullwidth letters back.
        Assert.Equal(text, charsetId == "us" ? decoded : string.Concat(decoded.Select(c => c is >= '！' and <= '～' ? (char)(c - '！' + '!') : c == '　' ? ' ' : c)));
    }

    [Fact]
    public void Text_the_font_cant_show_is_refused()
    {
        var charset = new FfxCharset("ABC");
        Assert.Throws<ArgumentException>(() => charset.Encode("ABD"));
        Assert.Equal([0x30, 0x32], charset.Encode("AC"));
    }
}
