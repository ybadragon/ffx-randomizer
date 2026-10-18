using System.Buffers.Binary;

namespace FfxRandomizer.Core.GameData;

/// <summary>
/// A battle kernel table such as ply_save.bin: a 20-byte header followed by fixed-size records.
/// The header stores the record size at 0x0C, the data length at 0x0E and the data offset at 0x10.
/// </summary>
public sealed class KernelTable
{
    public byte[] Bytes { get; }
    public int RecordSize { get; }
    public int DataOffset { get; }
    public int RecordCount { get; }

    public KernelTable(byte[] bytes)
    {
        Bytes = bytes;
        RecordSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0x0C));
        int dataLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0x0E));
        DataOffset = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0x10));
        if (RecordSize == 0 || DataOffset + dataLength > bytes.Length)
            throw new InvalidDataException("Not a kernel table.");
        RecordCount = dataLength / RecordSize;
    }

    public Span<byte> Record(int index)
    {
        if ((uint)index >= (uint)RecordCount) throw new ArgumentOutOfRangeException(nameof(index));
        return Bytes.AsSpan(DataOffset + index * RecordSize, RecordSize);
    }

    public uint ReadUInt32(int record, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Record(record)[offset..]);

    public void WriteUInt32(int record, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Record(record)[offset..], value);
}
