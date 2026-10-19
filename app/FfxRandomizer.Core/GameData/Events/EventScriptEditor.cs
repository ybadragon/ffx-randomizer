using System.Buffers.Binary;

namespace FfxRandomizer.Core.GameData.Events;

/// <summary>
/// Edits an event script chunk (offsets below are relative to the chunk) and keeps every structure pointing at the right
/// place. Layout, as read and written by FFXDataParser's AtelScriptObject:
/// <list type="bullet">
/// <item>Header: code length 0x00; pointers to map entrances 0x04, creator tag 0x08, script id 0x0C, end of the chunk
/// 0x10, event data 0x20, map table 0x24, area names 0x28, meta struct 0x2C, code 0x30; worker count (u16) 0x34;
/// worker header offsets from 0x38.</item>
/// <item>Worker header (0x34 bytes): function count 0x08, jump count 0x0A; pointers to variable declarations 0x14,
/// ref ints 0x18, ref floats 0x1C, function entries 0x20, jumps 0x24, private data 0x2C, shared data 0x30.</item>
/// <item>Meta struct header (at 0x2C): pointers at +0x0C, +0x10, +0x18, +0x1C, +0x24, +0x28, +0x2C, +0x3C; the
/// object at +0x3C holds two more at +0x08 and +0x0C.</item>
/// <item>After the code: shared data (16-aligned), meta struct, event data, map table, area names, then per worker its
/// function entry table, jump table and private data. Entry points and jump targets are code offsets.</item>
/// </list>
/// Code holds no addresses (jumps go through the worker's jump table), so code can move freely as long as the tables
/// follow it. Every insertion is recorded, so <see cref="Map"/> can say where an original byte ended up.
/// </summary>
public sealed class EventScriptEditor
{
    private const int CodeLengthField = 0x00, EndField = 0x10, MetaField = 0x2C, CodeStartField = 0x30;
    private const int WorkerCountField = 0x34, WorkerTable = 0x38;
    private const int MinimumCodePadding = 0x10;

    private byte[] _s;
    private readonly List<(int At, int Count)> _edits = [];
    private readonly List<(int Offset, int Length)> _insertedCode = [];

    public EventScriptEditor(byte[] scriptChunk)
    {
        int end = (int)U32(scriptChunk, EndField);
        if (end <= 0 || end > scriptChunk.Length) throw new InvalidDataException("Event script length is out of range.");
        _s = scriptChunk[..end];
    }

    public int CodeStart => (int)U32(_s, CodeStartField);
    public int CodeLength => (int)U32(_s, CodeLengthField);
    public int WorkerCount => U16(_s, WorkerCountField);
    public int FunctionCount(int worker) => U16(_s, Worker(worker) + 0x08);
    public int JumpCount(int worker) => U16(_s, Worker(worker) + 0x0A);
    public int FunctionEntry(int worker, int function) => (int)U32(_s, EntrySlot(worker, function));
    public int JumpTarget(int worker, int jump) => (int)U32(_s, JumpSlot(worker, jump));
    public ReadOnlySpan<byte> Code(int offset, int length) => _s.AsSpan(CodeStart + offset, length);

    /// <summary>
    /// The chunk offsets of the shared data (variables' storage, which comes after the code and moves as one block when
    /// code is added) and of the first worker's tables, where it ends.
    /// </summary>
    public int SharedDataStart => (int)U32(_s, Worker(0) + 0x30);
    public int WorkerTablesStart => Enumerable.Range(0, WorkerCount).Min(w => (int)U32(_s, Worker(w) + 0x20));

    /// <summary>Code inserted so far, as (code offset, length) in the current layout.</summary>
    public IReadOnlyList<(int Offset, int Length)> InsertedCode => _insertedCode;

    /// <summary>Where a byte at <paramref name="original"/> (chunk offset in the unedited script) is now.</summary>
    public int Map(int original)
    {
        int at = original;
        foreach (var (x, count) in _edits) if (at >= x) at += count;
        return at;
    }

    /// <summary>
    /// Inserts <paramref name="code"/> before code offset <paramref name="at"/>. Function entries and jump targets past
    /// it move with their code; ones exactly at it stay, so they run the new code first. Returns <paramref name="at"/>.
    /// </summary>
    public int InsertCode(int at, ReadOnlySpan<byte> code)
    {
        if (at < 0 || at > CodeLength) throw new ArgumentOutOfRangeException(nameof(at));
        int n = code.Length;
        int codeEnd = CodeStart + CodeLength;
        int padding = PaddingAfter(codeEnd);

        InsertRaw(CodeStart + at, n, excludeFields: [CodeStartField]);
        code.CopyTo(_s.AsSpan(CodeStart + at));
        W32(_s, CodeLengthField, (uint)(CodeLength + n));
        foreach (int slot in AllEntryAndJumpSlots())
        {
            uint target = U32(_s, slot);
            if (target > at) W32(_s, slot, target + (uint)n);
        }
        for (int i = 0; i < _insertedCode.Count; i++)
            if (_insertedCode[i].Offset >= at) _insertedCode[i] = (_insertedCode[i].Offset + n, _insertedCode[i].Length);
        _insertedCode.Add((at, n));

        // Give back what the zero padding after the code can absorb, keeping at least 0x10 bytes of it and keeping
        // everything after the code 16-byte aligned. The rest moves later structures along.
        int spare = Math.Max(0, padding - MinimumCodePadding);
        int shift = n <= spare ? 0 : (n - spare + 15) / 16 * 16;
        int reclaim = n - shift;
        if (reclaim > 0) RemoveRaw(CodeStart + CodeLength, reclaim);
        else if (reclaim < 0) InsertRaw(CodeStart + CodeLength, -reclaim, excludeFields: [CodeStartField]);
        return at;
    }

    /// <summary>Replaces code in place with the same number of bytes, after checking it is <paramref name="expected"/>.</summary>
    public void OverwriteCode(int offset, ReadOnlySpan<byte> expected, ReadOnlySpan<byte> code)
    {
        if (code.Length != expected.Length) throw new ArgumentException("An overwrite must keep the code the same length.", nameof(code));
        if (offset < 0 || offset + expected.Length > CodeLength || !Code(offset, expected.Length).SequenceEqual(expected))
            throw new InvalidDataException($"The code at {offset:X4} isn't {Convert.ToHexString(expected)}.");
        code.CopyTo(_s.AsSpan(CodeStart + offset));
    }

    /// <summary>Appends code at the end of the code and returns its code offset.</summary>
    public int AppendCode(ReadOnlySpan<byte> code) => InsertCode(CodeLength, code);

    /// <summary>Adds a function to a worker, starting at <paramref name="codeOffset"/>; returns its index.</summary>
    public int AddFunction(int worker, int codeOffset)
    {
        int count = FunctionCount(worker);
        int table = (int)U32(_s, Worker(worker) + 0x20);
        GrowTable(table + count * 4, Worker(worker) + 0x20, Worker(worker) + 0x08, count, codeOffset);
        return count;
    }

    /// <summary>Adds a jump label to a worker, pointing at <paramref name="codeOffset"/>; returns its index.</summary>
    public int AddJump(int worker, int codeOffset)
    {
        int count = JumpCount(worker);
        int table = (int)U32(_s, Worker(worker) + 0x24);
        GrowTable(table + count * 4, Worker(worker) + 0x24, Worker(worker) + 0x0A, count, codeOffset);
        return count;
    }

    private const int WorkerHeaderLength = 0x34;
    private const int ScenarioCountField = 0x16, MainScriptField = 0x18, ActorCountField = 0x36;

    /// <summary>
    /// Inserts a new worker at <paramref name="index"/> with the given functions (code offsets) and jump labels. Its header
    /// shares the script's variable declarations, ref ints, ref floats and shared data (as every worker's does) and has
    /// no private data; its tables go at the end of the chunk. Workers from <paramref name="index"/> on move up one, as
    /// do the header's main script index and type counts; the caller renumbers references to them in the code. Only a
    /// Scenario (type 4) worker is supported, since the header counts those.
    /// </summary>
    public void AddWorker(int index, int type, IReadOnlyList<int> functions, IReadOnlyList<int> jumps)
    {
        if (type != 4) throw new ArgumentException("Only Scenario workers can be added.", nameof(type));
        int count = WorkerCount;
        if (index < 0 || index > count) throw new ArgumentOutOfRangeException(nameof(index));
        // Headers sit in order straight after the worker table; the new one goes in its place among them.
        for (int w = 0; w < count; w++)
            if (Worker(w) != WorkerTable + count * 4 + w * WorkerHeaderLength)
                throw new InvalidDataException("The worker headers aren't laid out in order after the worker table.");
        // The header first (while the table still lists only the old workers, whose pointers move past it), then the
        // table slot, which moves every header 4 bytes on.
        InsertRaw(WorkerTable + count * 4 + index * WorkerHeaderLength, WorkerHeaderLength, excludeFields: []);
        InsertRaw(WorkerTable + index * 4, 4, excludeFields: []);
        int headerAt = WorkerTable + (count + 1) * 4 + index * WorkerHeaderLength;
        W16(_s, WorkerCountField, count + 1);
        W32(_s, WorkerTable + index * 4, (uint)headerAt);

        // Tables at the end of the chunk: function entries, then jumps.
        int end = (int)U32(_s, EndField);
        int tables = functions.Count * 4 + jumps.Count * 4;
        var header = new byte[WorkerHeaderLength];
        // Shared: variable count, ref int and ref float counts, and their tables, and the shared data. Read from the
        // template after the inserts above moved them.
        var w0 = _s.AsSpan(Worker(index == 0 ? 1 : 0), WorkerHeaderLength);
        w0[0x02..0x08].CopyTo(header.AsSpan(0x02));
        foreach (int field in new[] { 0x14, 0x18, 0x1C, 0x30 }) w0.Slice(field, 4).CopyTo(header.AsSpan(field));
        BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)type);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x08), (ushort)functions.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x0A), (ushort)jumps.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x20), (uint)end);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x24), (uint)(end + functions.Count * 4));
        header.CopyTo(_s.AsSpan(headerAt));
        // The new tables go before the end; the end pointer moves past them, nothing else points there.
        InsertRaw(end, tables, excludeFields: [headerAt + 0x20, headerAt + 0x24]);
        for (int f = 0; f < functions.Count; f++) W32(_s, end + f * 4, (uint)functions[f]);
        for (int j = 0; j < jumps.Count; j++) W32(_s, end + functions.Count * 4 + j * 4, (uint)jumps[j]);

        W16(_s, ScenarioCountField, U16(_s, ScenarioCountField) + 1);
        W16(_s, ActorCountField, U16(_s, ActorCountField) + 1);
        // 0xFFFF: no main script.
        if (U16(_s, MainScriptField) is int mainScript && mainScript >= index && mainScript != 0xFFFF) W16(_s, MainScriptField, mainScript + 1);
    }

    /// <summary>Sets a 16-bit code operand (the 2 bytes after the opcode at <paramref name="offset"/>).</summary>
    public void SetOperand(int offset, byte opcode, int operand)
    {
        _s[CodeStart + offset] = opcode;
        W16(_s, CodeStart + offset + 1, operand);
    }

    /// <summary>Points an existing function at different code.</summary>
    public void SetFunctionEntry(int worker, int function, int codeOffset) => W32(_s, EntrySlot(worker, function), (uint)codeOffset);

    /// <summary>The edited script chunk, its end rounded up to 16 bytes.</summary>
    public byte[] ToBytes()
    {
        int end = (int)U32(_s, EndField);
        int aligned = (end + 15) / 16 * 16;
        if (aligned != end)
        {
            Array.Resize(ref _s, Math.Max(_s.Length, aligned));
            W32(_s, EndField, (uint)aligned);
        }
        return _s[..aligned];
    }

    /// <summary>Grows a worker's entry or jump table by one slot. Worker headers come before every table, so their fields don't move.</summary>
    private void GrowTable(int at, int pointerField, int countField, int count, int value)
    {
        // A table with no entries yet may share its address with the next table; keep this one where it is.
        InsertRaw(at, 4, excludeFields: count == 0 ? [pointerField] : []);
        W32(_s, at, (uint)value);
        W16(_s, countField, count + 1);
    }

    /// <summary>Zero bytes from <paramref name="offset"/> up to the next structure any pointer names.</summary>
    private int PaddingAfter(int offset)
    {
        int next = PointerFields().Select(f => (int)U32(_s, f)).Where(v => v >= offset).DefaultIfEmpty(_s.Length).Min();
        int free = 0;
        while (offset + free < next && _s[offset + free] == 0) free++;
        return free;
    }

    /// <summary>Inserts <paramref name="count"/> zero bytes at <paramref name="at"/> and moves every pointer at or past it.</summary>
    private void InsertRaw(int at, int count, IReadOnlyCollection<int> excludeFields)
    {
        if (count == 0) return;
        var fields = PointerFields().ToList();
        var updates = new List<(int Field, uint Value)>();
        foreach (int field in fields)
        {
            uint value = U32(_s, field);
            int moved = field >= at ? field + count : field;
            if (value != 0 && value >= at && !excludeFields.Contains(field)) updates.Add((moved, value + (uint)count));
            else updates.Add((moved, value));
        }
        var grown = new byte[_s.Length + count];
        _s.AsSpan(0, at).CopyTo(grown);
        _s.AsSpan(at).CopyTo(grown.AsSpan(at + count));
        _s = grown;
        foreach (var (field, value) in updates) W32(_s, field, value);
        _edits.Add((at, count));
    }

    /// <summary>Removes <paramref name="count"/> zero bytes at <paramref name="at"/> and moves every pointer past them back.</summary>
    private void RemoveRaw(int at, int count)
    {
        if (_s.AsSpan(at, count).ContainsAnyExcept((byte)0)) throw new InvalidOperationException("Only zero padding can be removed.");
        var updates = new List<(int Field, uint Value)>();
        foreach (int field in PointerFields())
        {
            uint value = U32(_s, field);
            int moved = field >= at + count ? field - count : field;
            updates.Add((moved, value != 0 && value >= at + count ? value - (uint)count : value));
        }
        var shrunk = new byte[_s.Length - count];
        _s.AsSpan(0, at).CopyTo(shrunk);
        _s.AsSpan(at + count).CopyTo(shrunk.AsSpan(at));
        _s = shrunk;
        foreach (var (field, value) in updates) W32(_s, field, value);
        _edits.Add((at + count, -count));
    }

    /// <summary>Chunk offsets of every u32 that holds a chunk-relative pointer.</summary>
    private IEnumerable<int> PointerFields()
    {
        foreach (int field in new[] { 0x04, 0x08, 0x0C, EndField, 0x20, 0x24, 0x28, MetaField, CodeStartField }) yield return field;
        for (int w = 0; w < WorkerCount; w++)
        {
            yield return WorkerTable + w * 4;
            int header = Worker(w);
            foreach (int field in new[] { 0x14, 0x18, 0x1C, 0x20, 0x24, 0x2C, 0x30 }) yield return header + field;
        }
        int meta = (int)U32(_s, MetaField);
        if (meta == 0) yield break;
        foreach (int field in new[] { 0x0C, 0x10, 0x18, 0x1C, 0x24, 0x28, 0x2C, 0x3C }) yield return meta + field;
        int mapPointers = (int)U32(_s, meta + 0x3C);
        if (mapPointers == 0) yield break;
        yield return mapPointers + 0x08;
        yield return mapPointers + 0x0C;
    }

    private IEnumerable<int> AllEntryAndJumpSlots()
    {
        for (int w = 0; w < WorkerCount; w++)
        {
            for (int f = 0; f < FunctionCount(w); f++) yield return EntrySlot(w, f);
            for (int j = 0; j < JumpCount(w); j++) yield return JumpSlot(w, j);
        }
    }

    private int Worker(int index)
    {
        if (index < 0 || index >= WorkerCount) throw new InvalidDataException($"Event has no worker w{index:X2}.");
        return (int)U32(_s, WorkerTable + index * 4);
    }

    private int EntrySlot(int worker, int function)
    {
        if (function < 0 || function >= FunctionCount(worker)) throw new InvalidDataException($"Worker w{worker:X2} has no function f{function:X2}.");
        return (int)U32(_s, Worker(worker) + 0x20) + function * 4;
    }

    private int JumpSlot(int worker, int jump)
    {
        if (jump < 0 || jump >= JumpCount(worker)) throw new InvalidDataException($"Worker w{worker:X2} has no jump j{jump:X2}.");
        return (int)U32(_s, Worker(worker) + 0x24) + jump * 4;
    }

    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
    private static int U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
    private static void W32(byte[] b, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), value);
    private static void W16(byte[] b, int at, int value) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), (ushort)value);
}
