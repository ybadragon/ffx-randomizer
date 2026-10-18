using System.Buffers.Binary;

namespace FfxRandomizer.Core.GameData.Text;

/// <summary>
/// A localized battle kernel table (item.bin, a_ability.bin, monster1.bin, ...) read for its text.
/// Header (little-endian u16): first index at 0x08, last index at 0x0A, record size at 0x0C, data length at 0x0E,
/// data offset at 0x10. Records follow at the data offset and the string blob runs from the end of the data to
/// the end of the file. Each record starts with its name as a u16 offset into that blob (plus a u16 key).
/// </summary>
public sealed class LocalizedTable
{
    private readonly byte[] _bytes;
    private readonly int _dataOffset;
    private readonly int _stringsOffset;

    public int FirstIndex { get; }
    public int Count { get; }
    public int RecordSize { get; }

    public LocalizedTable(byte[] bytes)
    {
        if (bytes.Length < 0x14) throw new InvalidDataException("Not a kernel table.");
        _bytes = bytes;
        FirstIndex = U16(0x08);
        int lastIndex = U16(0x0A);
        RecordSize = U16(0x0C);
        int dataLength = U16(0x0E);
        _dataOffset = U16(0x10);
        _stringsOffset = _dataOffset + dataLength;
        Count = lastIndex - FirstIndex + 1;
        if (RecordSize < 2 || Count <= 0 || Count * RecordSize > dataLength || _stringsOffset > bytes.Length)
            throw new InvalidDataException("Not a kernel table.");
    }

    /// <summary>Decodes the name of the record at <paramref name="row"/> (0-based within this file).</summary>
    public string? Name(int row, FfxCharset charset, Func<int, string?>? playerName = null)
    {
        if ((uint)row >= (uint)Count) return null;
        int nameOffset = U16(_dataOffset + row * RecordSize);
        return charset.Decode(_bytes.AsSpan(_stringsOffset), nameOffset, playerName);
    }

    /// <summary>Decodes every record name, indexed by row.</summary>
    public string?[] Names(FfxCharset charset, Func<int, string?>? playerName = null)
    {
        var names = new string?[Count];
        for (int row = 0; row < Count; row++) names[row] = Name(row, charset, playerName);
        return names;
    }

    private int U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(offset));
}
