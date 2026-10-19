using System.Buffers.Binary;
using FfxRandomizer.Core.GameData;
using FfxRandomizer.Core.GameData.Events;
using FfxRandomizer.Core.Vbf;

namespace FfxRandomizer.Core.Randomization.Features;

/// <summary>Lets the scenes in <see cref="Cutscenes.All"/> be skipped: by holding a button, or always.</summary>
public sealed class CutsceneSkipFeature : IRandomizerFeature
{
    public const string Section = "cutscenes";

    public bool IsEnabled(RandomizerSettings s) => Mode(s) != Cutscenes.Original;

    public static OptionGroup Group => OptionCatalog.AllGroups.First(g => g.Id == "cutscenes");
    public static string Mode(RandomizerSettings s) => s.ChoiceOf(Group);
    public static SkipButton Button(RandomizerSettings s) => SkipButtons.ById(s.ChoiceOf(Group.Dropdown!));

    public void Apply(RandomizerContext context)
    {
        string mode = Mode(context.Settings);
        var button = Button(context.Settings);
        foreach (var scene in Cutscenes.All)
        {
            foreach (var (path, bytes) in Cutscenes.Build(context.Original, scene, mode, button)) context.Replace(path, bytes);
            context.Spoil(Section, new
            {
                Scene = scene.Name,
                scene.Area,
                Event = scene.EventId,
                Skip = mode == Cutscenes.Always ? "always" : $"hold {button.Label} for about a second",
            });
        }
    }

    public IEnumerable<string> Validate(RandomizerContext context)
    {
        string mode = Mode(context.Settings);
        var button = Button(context.Settings);
        foreach (var scene in Cutscenes.All)
        {
            var expected = Cutscenes.Build(context.Original, scene, mode, button);
            foreach (var (path, bytes) in expected)
            {
                if (!context.ChangedFiles.TryGetValue(path, out byte[]? written))
                {
                    yield return $"{scene.Name}: {path} wasn't written.";
                    continue;
                }
                byte[] original = context.Original.ReadFile(path);
                if (VbfArchive.BlocksFor((ulong)written.Length) != VbfArchive.BlocksFor((ulong)original.Length))
                    yield return $"{scene.Name}: {path} changed its 64 KB block count.";
                if (!written.AsSpan().SequenceEqual(bytes))
                    yield return $"{scene.Name}: {path} isn't what the edit produces (another feature changed it?).";
                bool isEvent = path.EndsWith(".ebp", StringComparison.OrdinalIgnoreCase);
                var problems = isEvent ? CheckEvent(original, written, mode) : CheckText(original, written, Cutscenes.PromptCount);
                foreach (string problem in problems) yield return $"{scene.Name}: {path}: {problem}";
            }
        }
    }

    /// <summary>
    /// Checks an edited event against the original without trusting the editor: other chunks are byte-identical, the
    /// text chunks keep their strings, the original code survives intact around the inserted code, every original entry
    /// point and jump target moved with its code, and everything after the code moved as one block.
    /// </summary>
    internal static IEnumerable<string> CheckEvent(byte[] original, byte[] patched, string mode)
    {
        var before = EventFile.Read(original);
        var after = EventFile.Read(patched);
        if (before.ChunkCount != after.ChunkCount)
        {
            yield return "the number of chunks changed.";
            yield break;
        }
        for (int i = 0; i < before.ChunkCount; i++)
        {
            if (i is EventFile.ScriptChunk) continue;
            if (i is EventFile.JapaneseTextChunk or EventFile.EnglishTextChunk && mode == Cutscenes.Hold && before.Chunk(i) is { } text)
            {
                foreach (string p in CheckText(text, after.Chunk(i)!, Cutscenes.PromptCount)) yield return $"text chunk {i}: {p}";
                continue;
            }
            if (!(before.Chunk(i) ?? []).AsSpan().SequenceEqual(after.Chunk(i) ?? []))
                yield return $"chunk {i} changed.";
        }
        foreach (string p in CheckScript(before.Chunk(EventFile.ScriptChunk)!, after.Chunk(EventFile.ScriptChunk)!)) yield return p;
    }

    private static IEnumerable<string> CheckScript(byte[] beforeChunk, byte[] afterChunk)
    {
        var b = new EventScriptEditor(beforeChunk);
        var a = new EventScriptEditor(afterChunk);
        if (a.WorkerCount != b.WorkerCount)
        {
            yield return "the worker count changed.";
            yield break;
        }

        // Inserted code: walk both codes in step; where they differ, the patched side must hold an inserted block and
        // then continue with exactly the original bytes, or the original instruction must be replaced by NOPs.
        byte[] originalCode = b.Code(0, b.CodeLength).ToArray();
        var inserts = Alignment(originalCode, a.Code(0, a.CodeLength).ToArray(), out var nops, out string? codeProblem);
        if (codeProblem is not null)
        {
            yield return codeProblem;
            yield break;
        }
        // The only instruction an edit may blank out is a worker start ("run wXX::fYY at level N", which the watcher replaces).
        foreach (var (at, length) in nops)
            if (length != 11 || originalCode[at] != 0xAE || originalCode[at + 9] != 0x36 || originalCode[at + 10] != 0x25)
                yield return $"original code at {at:X4} ({Convert.ToHexString(originalCode, at, length)}) was blanked out, but it isn't a worker start.";
        int MapCode(int offset) => offset + inserts.Where(i => i.At < offset).Sum(i => i.Length);

        for (int w = 0; w < b.WorkerCount; w++)
        {
            if (a.FunctionCount(w) < b.FunctionCount(w) || a.JumpCount(w) < b.JumpCount(w))
                yield return $"w{w:X2} lost functions or jumps.";
            for (int f = 0; f < b.FunctionCount(w); f++)
            {
                int was = b.FunctionEntry(w, f), now = a.FunctionEntry(w, f);
                // A function may be pointed at new code (the watcher); then its entry is in inserted code.
                bool repointed = inserts.Any(i => now >= MapCode(i.At) && now < MapCode(i.At) + i.Length) && now != MapCode(was);
                if (now != MapCode(was) && !repointed) yield return $"w{w:X2}::f{f:X2} starts at {now:X4}, expected {MapCode(was):X4}.";
            }
            for (int j = 0; j < b.JumpCount(w); j++)
                if (a.JumpTarget(w, j) != MapCode(b.JumpTarget(w, j)))
                    yield return $"w{w:X2} j{j:X2} points at {a.JumpTarget(w, j):X4}, expected {MapCode(b.JumpTarget(w, j)):X4}.";
            for (int f = b.FunctionCount(w); f < a.FunctionCount(w); f++)
                if (!InInserted(a.FunctionEntry(w, f))) yield return $"new function w{w:X2}::f{f:X2} doesn't start in inserted code.";
            for (int j = b.JumpCount(w); j < a.JumpCount(w); j++)
                if (!InInserted(a.JumpTarget(w, j))) yield return $"new jump w{w:X2} j{j:X2} doesn't point into inserted code.";
        }

        // Everything from the shared data to the first worker table moved by one amount, with pointers moved by it too.
        int sharedB = Shared(beforeChunk), sharedA = Shared(afterChunk);
        int tablesB = Tables(beforeChunk), tablesA = Tables(afterChunk);
        int delta = sharedA - sharedB;
        if (delta % 16 != 0) yield return $"the shared data moved by {delta}, which breaks its 16-byte alignment.";
        if (tablesA - tablesB != delta) yield return "the data between the code and the worker tables changed size.";
        else
        {
            var pointers = MetaPointerFields(beforeChunk).ToHashSet();
            for (int i = sharedB; i < tablesB; i++)
            {
                if (pointers.Contains(i))
                {
                    uint was = BinaryPrimitives.ReadUInt32LittleEndian(beforeChunk.AsSpan(i));
                    uint now = BinaryPrimitives.ReadUInt32LittleEndian(afterChunk.AsSpan(i + delta));
                    if (now != (was == 0 ? 0 : was + (uint)delta)) yield return $"meta pointer at {i:X} wasn't moved with its data.";
                    i += 3;
                }
                else if (beforeChunk[i] != afterChunk[i + delta])
                {
                    yield return $"script byte {i:X} (data after the code) changed.";
                    yield break;
                }
            }
        }

        bool InInserted(int offset) => inserts.Any(i => offset >= MapCode(i.At) && offset < MapCode(i.At) + i.Length);
    }

    /// <summary>
    /// What an edit did to an event's code. <see cref="Inserted"/>: inserted blocks as (code offset, length) in the edited
    /// code. <see cref="Nopped"/>: original instructions replaced by NOPs (0x00) of the same length, as (offset in the
    /// original code, offset in the edited code, length).
    /// </summary>
    public sealed record CodeChanges(IReadOnlyList<(int Offset, int Length)> Inserted, IReadOnlyList<(int Original, int Offset, int Length)> Nopped);

    public static CodeChanges ChangedCode(byte[] originalEbp, byte[] patchedEbp)
    {
        var b = new EventScriptEditor(EventFile.Read(originalEbp).Chunk(EventFile.ScriptChunk)!);
        var a = new EventScriptEditor(EventFile.Read(patchedEbp).Chunk(EventFile.ScriptChunk)!);
        var inserts = Alignment(b.Code(0, b.CodeLength).ToArray(), a.Code(0, a.CodeLength).ToArray(), out var nops, out string? problem);
        if (problem is not null) throw new InvalidDataException(problem);
        int Map(int at) => at + inserts.Where(o => o.At < at).Sum(o => o.Length);
        return new CodeChanges(
            inserts.Select(i => (Map(i.At), i.Length)).ToList(),
            nops.Select(n => (n.At, Map(n.At), n.Length)).ToList());
    }

    /// <summary>
    /// Walks the original and edited code together and explains every difference, greedily, as either original code
    /// replaced by NOPs of the same length or a block inserted before the original continues (checked over 32 bytes).
    /// When the inserted block could sit at several places (it starts with the same instruction the original continues
    /// with, as the watcher start at a scene's entry does), it is placed at the earliest instruction boundary.
    /// </summary>
    private static List<(int At, int Length)> Alignment(byte[] before, byte[] after, out List<(int At, int Length)> nops, out string? problem)
    {
        problem = null;
        nops = [];
        var inserts = new List<(int At, int Length)>();
        // Opcodes from 0x80 up take a 16-bit operand.
        var boundary = new bool[before.Length + 1];
        for (int at = 0; at < before.Length; at += before[at] >= 0x80 ? 3 : 1) boundary[at] = true;
        boundary[before.Length] = true;
        int i = 0, j = 0, floor = 0;
        while (i < before.Length)
        {
            if (j < after.Length && before[i] == after[j])
            {
                i++;
                j++;
                continue;
            }
            int k = Math.Min(32, before.Length - i);
            int zeros = 0;
            while (j + zeros < after.Length && after[j + zeros] == 0) zeros++;
            int nopped = -1;
            for (int len = 1; len <= zeros && i + len <= before.Length; len++)
            {
                int rest = Math.Min(32, before.Length - i - len);
                if (j + len + rest <= after.Length && after.AsSpan(j + len, rest).SequenceEqual(before.AsSpan(i + len, rest))) { nopped = len; break; }
            }
            if (nopped > 0)
            {
                nops.Add((i, nopped));
                i += nopped;
                j += nopped;
                floor = i;
                continue;
            }
            int found = -1;
            for (int len = 1; j + len <= after.Length && len <= 4096; len++)
                if (j + len + k <= after.Length && after.AsSpan(j + len, k).SequenceEqual(before.AsSpan(i, k))) { found = len; break; }
            if (found < 0)
            {
                problem = $"original code at {i:X4} doesn't continue anywhere in the patched code.";
                return inserts;
            }
            // Slide the block back over original bytes it ends with, keeping the earliest boundary it can start at.
            var (bestI, bestJ) = (i, j);
            for (int si = i, sj = j; si > floor && after[sj - 1 + found] == before[si - 1]; )
            {
                si--;
                sj--;
                if (boundary[si]) (bestI, bestJ) = (si, sj);
            }
            (i, j) = (bestI, bestJ);
            inserts.Add((i, found));
            j += found;
            floor = i + 1;
        }
        if (j < after.Length) inserts.Add((before.Length, after.Length - j));
        return inserts;
    }

    /// <summary>Checks an edited string table: the original strings unchanged and <paramref name="added"/> more after them.</summary>
    internal static IEnumerable<string> CheckText(byte[] before, byte[] after, int added)
    {
        int countB = EventText.Count(before), countA = EventText.Count(after);
        if (countA < countB + added) yield return $"has {countA} strings, expected at least {countB + added}.";
        for (int s = 0; s < countB * 2; s++)
        {
            if (!StringAt(before, s).SequenceEqual(StringAt(after, s))) { yield return $"string {s / 2} changed."; yield break; }
            if ((Header(before, s) & 0xFFFF0000) != (Header(after, s) & 0xFFFF0000)) { yield return $"string {s / 2} lost its flags."; yield break; }
        }
    }

    private static uint Header(byte[] table, int slot) => BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(slot * 4));

    private static ReadOnlySpan<byte> StringAt(byte[] table, int slot)
    {
        var rest = table.AsSpan((int)(Header(table, slot) & 0xFFFF));
        int end = rest.IndexOf((byte)0);
        return rest[..(end < 0 ? rest.Length : end)];
    }

    private static int Shared(byte[] chunk) => (int)BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(WorkerHeader(chunk, 0) + 0x30));

    private static int Tables(byte[] chunk)
    {
        int workers = BinaryPrimitives.ReadUInt16LittleEndian(chunk.AsSpan(0x34));
        return Enumerable.Range(0, workers).Min(w => (int)BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(WorkerHeader(chunk, w) + 0x20)));
    }

    private static int WorkerHeader(byte[] chunk, int worker) => (int)BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(0x38 + worker * 4));

    private static IEnumerable<int> MetaPointerFields(byte[] chunk)
    {
        int meta = (int)BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(0x2C));
        if (meta == 0) yield break;
        foreach (int field in new[] { 0x0C, 0x10, 0x18, 0x1C, 0x24, 0x28, 0x2C, 0x3C }) yield return meta + field;
        int map = (int)BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(meta + 0x3C));
        if (map == 0) yield break;
        yield return map + 0x08;
        yield return map + 0x0C;
    }
}
