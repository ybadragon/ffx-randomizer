using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace FfxRandomizer.Core.Vbf;

/// <summary>
/// Reads and patches the Virtuos Big File (.vbf) archives used by the FFX/X-2 HD Remaster.
/// Format and block decoding follow topher-au/VBFTool.
/// </summary>
public sealed class VbfArchive
{
    public const uint Magic = 1264144979;
    public const int BlockSize = 65536;
    private const int EntrySize = 32;

    private readonly Dictionary<string, int> _indexByMd5 = new(StringComparer.Ordinal);

    public string Path { get; }
    public uint HeaderLength { get; private set; }
    public int FileCount { get; private set; }
    public string[] Names { get; private set; } = [];
    public bool HeaderChecksumValid { get; private set; }

    private uint[] _blockListStarts = [];
    private uint[] _unknowns = [];
    private ulong[] _sizes = [];
    private ulong[] _offsets = [];
    private ulong[] _nameOffsets = [];
    private ushort[] _blockList = [];
    private long _entriesOffset;
    private long _blockListOffset;

    private VbfArchive(string path) => Path = path;

    /// <summary>
    /// Opens an archive. When <paramref name="headerOverride"/> is given it is parsed instead of the file's own
    /// header; patched archives keep the original data intact, so the original header still describes it.
    /// </summary>
    public static VbfArchive Open(string path, byte[]? headerOverride = null)
    {
        var archive = new VbfArchive(path);
        byte[] header = headerOverride ?? ReadHeaderBytes(path);
        archive.Parse(header);
        archive.HeaderChecksumValid = headerOverride is not null || ReadTrailer(path).AsSpan().SequenceEqual(MD5.HashData(header));
        return archive;
    }

    private void Parse(byte[] header)
    {
        using var reader = new BinaryReader(new MemoryStream(header));
        if (reader.ReadUInt32() != Magic) throw new InvalidDataException($"Not a VBF archive: {Path}");
        HeaderLength = reader.ReadUInt32();
        FileCount = checked((int)reader.ReadUInt64());

        for (int i = 0; i < FileCount; i++)
            _indexByMd5[Convert.ToHexString(reader.ReadBytes(16))] = i;

        _entriesOffset = reader.BaseStream.Position;
        _blockListStarts = new uint[FileCount];
        _unknowns = new uint[FileCount];
        _sizes = new ulong[FileCount];
        _offsets = new ulong[FileCount];
        _nameOffsets = new ulong[FileCount];
        for (int i = 0; i < FileCount; i++)
        {
            _blockListStarts[i] = reader.ReadUInt32();
            _unknowns[i] = reader.ReadUInt32();
            _sizes[i] = reader.ReadUInt64();
            _offsets[i] = reader.ReadUInt64();
            _nameOffsets[i] = reader.ReadUInt64();
        }

        uint stringTableSize = reader.ReadUInt32();
        byte[] table = reader.ReadBytes((int)stringTableSize - 4);
        Names = new string[FileCount];
        for (int i = 0; i < FileCount; i++)
        {
            int start = (int)_nameOffsets[i];
            int end = Array.IndexOf(table, (byte)0, start);
            Names[i] = Encoding.UTF8.GetString(table, start, (end < 0 ? table.Length : end) - start);
        }

        long blockCount = 0;
        for (int i = 0; i < FileCount; i++) blockCount += BlocksFor(_sizes[i]);
        _blockListOffset = reader.BaseStream.Position;
        _blockList = new ushort[blockCount];
        for (long i = 0; i < blockCount; i++) _blockList[i] = reader.ReadUInt16();
    }

    public static int BlocksFor(ulong size) => (int)((size + BlockSize - 1) / BlockSize);

    public int IndexOf(string name)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(name.ToLowerInvariant()));
        return _indexByMd5.TryGetValue(Convert.ToHexString(hash), out int index) ? index : -1;
    }

    public long SizeOf(int index) => (long)_sizes[index];

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte[]> _cache = new();
    private const int CacheLimit = 1 << 20;

    /// <summary>Returns a fresh copy of a file's contents; small files are cached after the first read.</summary>
    public byte[] ReadFile(string name)
    {
        int index = IndexOf(name);
        if (index < 0) throw new FileNotFoundException($"{name} is not in {System.IO.Path.GetFileName(Path)}");
        if (_cache.TryGetValue(index, out byte[]? cached)) return (byte[])cached.Clone();
        using var stream = OpenRead(Path);
        byte[] bytes = ReadFile(stream, index);
        if (bytes.Length <= CacheLimit) _cache[index] = (byte[])bytes.Clone();
        return bytes;
    }

    public byte[] ReadFile(Stream stream, int index)
    {
        ulong size = _sizes[index];
        int blocks = BlocksFor(size);
        int remainder = (int)(size % BlockSize);
        if (remainder == 0) remainder = BlockSize;
        long blockStart = _blockListStarts[index];

        var result = new byte[size];
        var compressed = new byte[BlockSize];
        stream.Seek((long)_offsets[index], SeekOrigin.Begin);
        for (int b = 0; b < blocks; b++)
        {
            int length = _blockList[blockStart + b];
            if (length == 0) length = BlockSize;
            stream.ReadExactly(compressed, 0, length);
            int decodedSize = b == blocks - 1 ? remainder : BlockSize;
            var target = result.AsSpan(b * BlockSize, decodedSize);
            bool stored = length == BlockSize || (b == blocks - 1 && length == remainder);
            if (stored)
            {
                compressed.AsSpan(0, decodedSize).CopyTo(target);
            }
            else
            {
                // Each block is a zlib stream; skip the 2-byte header and inflate the raw deflate data.
                using var inflate = new DeflateStream(new MemoryStream(compressed, 2, length - 2), CompressionMode.Decompress);
                inflate.ReadExactly(target);
            }
        }
        return result;
    }

    /// <summary>
    /// Replaces files in place. New data is appended at <paramref name="appendAt"/> (the original end of the archive),
    /// then the affected entries, block list slots and the trailing header MD5 are rewritten. Each replacement must
    /// keep the original 64 KB block count so the block list keeps its size.
    /// </summary>
    public void Patch(IReadOnlyDictionary<string, byte[]> replacements, long appendAt)
    {
        var resolved = new List<(int Index, byte[] Data)>();
        foreach (var (name, data) in replacements)
        {
            int index = IndexOf(name);
            if (index < 0) throw new FileNotFoundException($"{name} is not in {System.IO.Path.GetFileName(Path)}");
            if (BlocksFor((ulong)data.Length) != BlocksFor(_sizes[index]))
                throw new InvalidOperationException($"{name} changed its 64 KB block count; it can't be patched in place.");
            resolved.Add((index, data));
        }

        using var stream = new FileStream(Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var writer = new BinaryWriter(stream);
        stream.SetLength(appendAt);
        stream.Seek(appendAt, SeekOrigin.Begin);
        foreach (var (index, data) in resolved)
        {
            _offsets[index] = (ulong)stream.Position;
            _sizes[index] = (ulong)data.Length;
            stream.Write(data);
            int blocks = BlocksFor((ulong)data.Length);
            int remainder = data.Length % BlockSize;
            for (int b = 0; b < blocks; b++)
            {
                bool last = b == blocks - 1;
                // 0 marks a full stored block; a last block whose length equals the remainder is stored too.
                _blockList[_blockListStarts[index] + b] = (ushort)(last && remainder != 0 ? remainder : 0);
            }
        }

        foreach (var (index, _) in resolved)
        {
            stream.Seek(_entriesOffset + (long)index * EntrySize, SeekOrigin.Begin);
            writer.Write(_blockListStarts[index]);
            writer.Write(_unknowns[index]);
            writer.Write(_sizes[index]);
            writer.Write(_offsets[index]);
            writer.Write(_nameOffsets[index]);
            for (int b = 0; b < BlocksFor(_sizes[index]); b++)
            {
                stream.Seek(_blockListOffset + (_blockListStarts[index] + b) * 2L, SeekOrigin.Begin);
                writer.Write(_blockList[_blockListStarts[index] + b]);
            }
        }
        writer.Flush();

        var header = new byte[HeaderLength];
        stream.Seek(0, SeekOrigin.Begin);
        stream.ReadExactly(header);
        stream.Seek(0, SeekOrigin.End);
        stream.Write(MD5.HashData(header));
    }

    /// <summary>Length and trailing header MD5 together identify one exact version of an archive.</summary>
    public static string Fingerprint(string path)
    {
        long length = new FileInfo(path).Length;
        return $"{length}:{Convert.ToHexString(ReadTrailer(path))}";
    }

    public static byte[] ReadHeaderBytes(string path)
    {
        using var stream = OpenRead(path);
        Span<byte> prefix = stackalloc byte[8];
        stream.ReadExactly(prefix);
        int length = BitConverter.ToInt32(prefix[4..]);
        var header = new byte[length];
        stream.Seek(0, SeekOrigin.Begin);
        stream.ReadExactly(header);
        return header;
    }

    /// <summary>Writes the original header back and cuts off anything <see cref="Patch"/> appended.</summary>
    public static void Restore(string path, byte[] originalHeader, long originalLength)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Seek(0, SeekOrigin.Begin);
        stream.Write(originalHeader);
        stream.SetLength(originalLength);
    }

    public static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);

    private static byte[] ReadTrailer(string path)
    {
        using var stream = OpenRead(path);
        var trailer = new byte[16];
        stream.Seek(-16, SeekOrigin.End);
        stream.ReadExactly(trailer);
        return trailer;
    }
}
