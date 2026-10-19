using System.Buffers.Binary;

namespace FfxRandomizer.Core.GameData.Events;

/// <summary>
/// An event's string table, as in new_XXpc/event/obj_ps3/xx/&lt;id&gt;/&lt;id&gt;.bin and the event file's own text chunks:
/// one 8-byte header per string (regular, then simplified: offset in the low 16 bits, flags and choice count above),
/// then the zero-terminated strings. Offsets count from the start of the table.
/// </summary>
public static class EventText
{
    public static int Count(ReadOnlySpan<byte> table) => table.Length < 2 ? 0 : BinaryPrimitives.ReadUInt16LittleEndian(table) / 8;

    /// <summary>Returns the table with <paramref name="strings"/> (encoded, without terminators) added at the end.</summary>
    public static byte[] Append(ReadOnlySpan<byte> table, IReadOnlyList<byte[]> strings)
    {
        int count = Count(table);
        int added = strings.Count;
        var headers = new uint[count * 2];
        int dataEnd = count * 8;
        for (int i = 0; i < headers.Length; i++)
        {
            headers[i] = BinaryPrimitives.ReadUInt32LittleEndian(table[(i * 4)..]);
            int offset = (int)(headers[i] & 0xFFFF);
            int terminator = table[offset..].IndexOf((byte)0);
            if (terminator < 0) throw new InvalidDataException("A string in the event text runs off the end of the table.");
            dataEnd = Math.Max(dataEnd, offset + terminator + 1);
        }

        var output = new MemoryStream();
        int shift = added * 8;
        foreach (uint header in headers)
        {
            uint offset = (header & 0xFFFF) + (uint)shift;
            if (offset > 0xFFFF) throw new InvalidOperationException("The event text is too long to add strings to.");
            Write32(output, (header & 0xFFFF0000) | offset);
        }
        long newHeadersAt = output.Length;
        output.Write(new byte[shift]);
        output.Write(table[(count * 8)..dataEnd]);
        var offsets = new List<int>();
        foreach (byte[] s in strings)
        {
            offsets.Add((int)output.Length);
            output.Write(s);
            output.WriteByte(0);
        }
        if (output.Length > 0xFFFF) throw new InvalidOperationException("The event text is too long to add strings to.");
        byte[] bytes = output.ToArray();
        for (int i = 0; i < added; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((int)newHeadersAt + i * 8), (uint)offsets[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((int)newHeadersAt + i * 8 + 4), (uint)offsets[i]);
        }
        return bytes;
    }

    private static void Write32(Stream s, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        s.Write(b);
    }
}
