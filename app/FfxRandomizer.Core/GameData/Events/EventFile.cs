using System.Buffers.Binary;

namespace FfxRandomizer.Core.GameData.Events;

/// <summary>
/// An event file (.ebp) split into its chunks. The file is "EV01", then a table of u32 chunk offsets from 0x04 ending with
/// the file length and 0xFFFFFFFF; a 0 entry is an absent chunk. Chunk 0 is the event script, 1 the Japanese text,
/// 4 the English text (both in the PS2 string-table format). Chunks start on 0x40 boundaries, padded with zeros.
/// </summary>
public sealed class EventFile
{
    public const int ScriptChunk = 0, JapaneseTextChunk = 1, EnglishTextChunk = 4;
    private const uint Magic = 0x31305645; // "EV01"
    private const int ChunkAlignment = 0x40;

    private readonly byte[] _header;
    private readonly List<byte[]?> _chunks;

    private EventFile(byte[] header, List<byte[]?> chunks)
    {
        _header = header;
        _chunks = chunks;
    }

    public int ChunkCount => _chunks.Count;

    public static EventFile Read(byte[] ebp)
    {
        if (ebp.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(ebp) != Magic) throw new InvalidDataException("Not an event file.");
        var offsets = new List<uint>();
        for (int at = 4; ; at += 4)
        {
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(ebp.AsSpan(at));
            if (offset == uint.MaxValue) break;
            offsets.Add(offset);
        }
        // The last entry is the end of the file, not a chunk.
        var starts = offsets.Take(offsets.Count - 1).ToList();
        int first = (int)starts.Where(s => s != 0).Min();
        var chunks = new List<byte[]?>();
        for (int i = 0; i < starts.Count; i++)
        {
            uint start = starts[i];
            if (start == 0)
            {
                chunks.Add(null);
                continue;
            }
            // A chunk ends where the next present one starts, which may be where it starts too (an empty chunk, as
            // slik1100's chunk 1 is); the table's last entry is the file's end.
            uint end = offsets.Skip(i + 1).First(o => o != 0);
            if (end < start || end > ebp.Length) throw new InvalidDataException("Event chunks are out of order.");
            chunks.Add(ebp[(int)start..(int)end]);
        }
        return new EventFile(ebp[..first], chunks);
    }

    /// <summary>Where chunk 0, the script, starts in an event file.</summary>
    public static int ScriptChunkStart(byte[] ebp) => (int)BinaryPrimitives.ReadUInt32LittleEndian(ebp.AsSpan(4));

    /// <summary>A chunk's bytes, including the zero padding up to the next chunk.</summary>
    public byte[]? Chunk(int index) => _chunks[index];

    /// <summary>Replaces a chunk; it is padded to the chunk alignment when the file is written.</summary>
    public void SetChunk(int index, byte[] bytes) => _chunks[index] = bytes;

    public byte[] ToBytes()
    {
        var output = new MemoryStream();
        output.Write(_header);
        var offsets = new List<uint>();
        foreach (byte[]? chunk in _chunks)
        {
            if (chunk is null)
            {
                offsets.Add(0);
                continue;
            }
            offsets.Add((uint)output.Length);
            output.Write(chunk);
            int padded = (chunk.Length + ChunkAlignment - 1) / ChunkAlignment * ChunkAlignment;
            output.Write(new byte[padded - chunk.Length]);
        }
        offsets.Add((uint)output.Length);
        byte[] bytes = output.ToArray();
        for (int i = 0; i < offsets.Count; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4 + i * 4), offsets[i]);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4 + offsets.Count * 4), uint.MaxValue);
        return bytes;
    }
}
