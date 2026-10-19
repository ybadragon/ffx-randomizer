using System.Buffers.Binary;

namespace FfxRandomizer.Core.GameData;

/// <summary>
/// The parts of an event file (.ebp) the randomizer reads or edits. The file starts with "EV01" and a table of chunk
/// offsets; chunk 0 is the event script, and every offset inside it below is relative to the script's start.
/// <list type="bullet">
/// <item>Script header: code length (u32) at 0x00, total length at 0x10, code start at 0x30, worker count (u16) at 0x34,
/// worker header offsets (u32) from 0x38.</item>
/// <item>Worker header (0x34 bytes): variable count (u16) at 0x02, function count at 0x08, jump count at 0x0A, variable
/// table offset at 0x14, function entry table at 0x20, jump table at 0x24, private data at 0x2C, shared data at 0x30.</item>
/// <item>Function entry points and jump targets are u32 code offsets (relative to the code start). Code refers to jumps by
/// index into its worker's jump table, so the bytecode itself holds no code addresses.</item>
/// <item>Each variable is 8 bytes: data offset (24 bits) plus format and location in the top byte, then element count (u16).</item>
/// </list>
/// </summary>
public sealed class EventScript
{
    private const uint Magic = 0x31305645; // "EV01"
    private const int SharedLocation = 4;

    private readonly byte[] _bytes;
    private readonly int _script;
    private readonly int _variableCount;
    private readonly int _variables;
    private readonly int _shared;

    public EventScript(byte[] ebp)
    {
        _bytes = ebp;
        if (ebp.Length < 8 || U32(0) != Magic) throw new InvalidDataException("Not an event file.");
        _script = (int)U32(4);
        WorkerCount = U16(_script + 0x34);
        if (WorkerCount == 0) throw new InvalidDataException("Event script has no workers.");
        int worker = Worker(0);
        _variableCount = U16(worker + 0x02);
        _variables = _script + (int)U32(worker + 0x14);
        _shared = _script + (int)U32(worker + 0x30);
    }

    public int WorkerCount { get; }

    /// <summary>File offset of the first byte of code; a code offset c is at file offset CodeStart + c.</summary>
    public int CodeStart => _script + (int)U32(_script + 0x30);

    public int CodeLength => (int)U32(_script);

    /// <summary>File offset of the u32 code length in the script header.</summary>
    public int CodeLengthField => _script;

    /// <summary>File offsets of every worker's function entry and jump table slots (u32 code offsets).</summary>
    public IEnumerable<int> EntryAndJumpSlots()
    {
        for (int w = 0; w < WorkerCount; w++)
        {
            for (int f = 0; f < FunctionCount(w); f++) yield return FunctionEntrySlot(w, f);
            for (int j = 0; j < JumpCount(w); j++) yield return JumpSlot(w, j);
        }
    }

    public int FunctionCount(int worker) => U16(Worker(worker) + 0x08);
    public int JumpCount(int worker) => U16(Worker(worker) + 0x0A);

    /// <summary>Code offset where a worker's function starts.</summary>
    public int FunctionEntry(int worker, int function) => (int)U32(FunctionEntrySlot(worker, function));

    /// <summary>Code offset a worker's jump label points at.</summary>
    public int JumpTarget(int worker, int jump) => (int)U32(JumpSlot(worker, jump));

    public ReadOnlySpan<byte> Code(int codeOffset, int length) => _bytes.AsSpan(CodeStart + codeOffset, length);

    /// <summary>File offset of a shared-data array variable, after checking its declared location and element count.</summary>
    public int SharedArrayOffset(int variable, int elementCount)
    {
        if (variable >= _variableCount) throw new InvalidDataException($"Event has no variable 0x{variable:X}.");
        uint declaration = U32(_variables + variable * 8);
        int location = (int)(declaration >> 25) & 7;
        int count = U16(_variables + variable * 8 + 4);
        if (location != SharedLocation || count != elementCount)
            throw new InvalidDataException($"Variable 0x{variable:X} isn't a {elementCount}-element shared array.");
        int offset = _shared + (int)(declaration & 0xFFFFFF);
        if (offset + elementCount * 4 > _bytes.Length) throw new InvalidDataException("Variable runs past the end of the file.");
        return offset;
    }

    /// <summary>
    /// Free bytes after the code: the zero padding up to whichever table the script header or a worker header places next.
    /// Code can grow into this without moving anything else in the file.
    /// </summary>
    public int CodePadding()
    {
        int codeEnd = CodeStart + CodeLength;
        int next = _script + (int)U32(_script + 0x10);
        void Consider(uint relative)
        {
            int at = _script + (int)relative;
            if (relative != 0 && at >= codeEnd && at < next) next = at;
        }
        foreach (int field in new[] { 0x20, 0x24, 0x28, 0x2C }) Consider(U32(_script + field));
        for (int w = 0; w < WorkerCount; w++)
            foreach (int field in new[] { 0x14, 0x18, 0x1C, 0x20, 0x24, 0x2C, 0x30 }) Consider(U32(Worker(w) + field));
        int free = 0;
        while (codeEnd + free < next && _bytes[codeEnd + free] == 0) free++;
        return free;
    }

    /// <summary>
    /// Returns a copy of the event with <paramref name="code"/> inserted just before code offset <paramref name="at"/>.
    /// The code from <paramref name="at"/> to the end shifts up into the padding after the code, so the file keeps its
    /// size and no other table moves; every function entry point and jump target past <paramref name="at"/> moves with it.
    /// Entry points and jumps exactly at <paramref name="at"/> stay put, so they now run the inserted code first.
    /// </summary>
    public static byte[] InsertCode(byte[] ebp, int at, ReadOnlySpan<byte> code)
    {
        var script = new EventScript(ebp);
        int n = code.Length;
        if (at < 0 || at > script.CodeLength) throw new ArgumentOutOfRangeException(nameof(at));
        if (n > script.CodePadding())
            throw new InvalidOperationException($"Not enough free space after the event code for {n} more bytes ({script.CodePadding()} free).");

        byte[] copy = (byte[])ebp.Clone();
        int from = script.CodeStart + at, end = script.CodeStart + script.CodeLength;
        Buffer.BlockCopy(ebp, from, copy, from + n, end - from);
        code.CopyTo(copy.AsSpan(from));
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(script._script), (uint)(script.CodeLength + n));

        for (int w = 0; w < script.WorkerCount; w++)
        {
            for (int f = 0; f < script.FunctionCount(w); f++) Shift(script.FunctionEntrySlot(w, f));
            for (int j = 0; j < script.JumpCount(w); j++) Shift(script.JumpSlot(w, j));
        }
        return copy;

        void Shift(int slot)
        {
            uint target = BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(slot));
            if (target > at) BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(slot), target + (uint)n);
        }
    }

    private int Worker(int index)
    {
        if (index < 0 || index >= WorkerCount) throw new InvalidDataException($"Event has no worker w{index:X2}.");
        return _script + (int)U32(_script + 0x38 + index * 4);
    }

    private int FunctionEntrySlot(int worker, int function)
    {
        if (function < 0 || function >= FunctionCount(worker)) throw new InvalidDataException($"Worker w{worker:X2} has no function f{function:X2}.");
        return _script + (int)U32(Worker(worker) + 0x20) + function * 4;
    }

    private int JumpSlot(int worker, int jump)
    {
        if (jump < 0 || jump >= JumpCount(worker)) throw new InvalidDataException($"Worker w{worker:X2} has no jump j{jump:X2}.");
        return _script + (int)U32(Worker(worker) + 0x24) + jump * 4;
    }

    private uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(offset));
    private int U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(offset));
}
