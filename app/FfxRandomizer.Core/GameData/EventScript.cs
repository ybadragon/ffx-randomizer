using System.Buffers.Binary;

namespace FfxRandomizer.Core.GameData;

/// <summary>
/// The parts of an event file (.ebp) needed to find script variables. The file starts with "EV01" and a table of chunk
/// offsets; chunk 0 is the event script. Script header: worker count (u16) at 0x34, worker header offsets (u32) from 0x38.
/// Worker header: variable count (u16) at 0x02, variable table offset at 0x14, shared data offset at 0x30.
/// Each variable is 8 bytes: data offset (24 bits) plus format and location in the top byte, then element count (u16).
/// For editing scripts, see <see cref="Events.EventScriptEditor"/>.
/// </summary>
internal sealed class EventScript
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
        if (U16(_script + 0x34) == 0) throw new InvalidDataException("Event script has no workers.");
        int worker = _script + (int)U32(_script + 0x38);
        _variableCount = U16(worker + 0x02);
        _variables = _script + (int)U32(worker + 0x14);
        _shared = _script + (int)U32(worker + 0x30);
    }

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

    private uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(offset));
    private int U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(offset));
}
