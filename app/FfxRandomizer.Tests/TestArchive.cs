using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FfxRandomizer.Core.Vbf;

namespace FfxRandomizer.Tests;

/// <summary>Builds small VBF archives in the real layout: zlib-compressed 64 KB blocks, MD5 name index, header MD5 trailer.</summary>
internal static class TestArchive
{
    public static void Write(string path, IReadOnlyList<(string Name, byte[] Data)> files)
    {
        var table = new MemoryStream();
        var nameOffsets = new List<long>();
        foreach (var (name, _) in files)
        {
            nameOffsets.Add(table.Length);
            table.Write(Encoding.UTF8.GetBytes(name));
            table.WriteByte(0);
        }

        var blockLengths = new List<ushort>();
        var blockStarts = new List<uint>();
        var payloads = new List<byte[]>();
        foreach (var (_, data) in files)
        {
            blockStarts.Add((uint)blockLengths.Count);
            var payload = new MemoryStream();
            for (int offset = 0; offset < data.Length; offset += VbfArchive.BlockSize)
            {
                var block = data.AsSpan(offset, Math.Min(VbfArchive.BlockSize, data.Length - offset));
                var compressed = new MemoryStream();
                using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(block);
                if (compressed.Length < block.Length)
                {
                    payload.Write(compressed.ToArray());
                    blockLengths.Add((ushort)compressed.Length);
                }
                else
                {
                    payload.Write(block);
                    blockLengths.Add((ushort)(block.Length == VbfArchive.BlockSize ? 0 : block.Length));
                }
            }
            payloads.Add(payload.ToArray());
        }

        int n = files.Count;
        long headerLength = 4 + 4 + 8 + n * 16 + n * 32 + 4 + table.Length + blockLengths.Count * 2;
        var header = new MemoryStream();
        var w = new BinaryWriter(header);
        w.Write(VbfArchive.Magic);
        w.Write((uint)headerLength);
        w.Write((ulong)n);
        foreach (var (name, _) in files) w.Write(MD5.HashData(Encoding.UTF8.GetBytes(name.ToLowerInvariant())));
        long dataOffset = headerLength;
        for (int i = 0; i < n; i++)
        {
            w.Write(blockStarts[i]);
            w.Write(0x00381336u);
            w.Write((ulong)files[i].Data.Length);
            w.Write((ulong)dataOffset);
            w.Write((ulong)nameOffsets[i]);
            dataOffset += payloads[i].Length;
        }
        w.Write((uint)(table.Length + 4));
        w.Write(table.ToArray());
        foreach (ushort length in blockLengths) w.Write(length);
        w.Flush();

        using var file = File.Create(path);
        file.Write(header.ToArray());
        foreach (var payload in payloads) file.Write(payload);
        file.Write(MD5.HashData(header.ToArray()));
    }

    public static byte[] Bytes(int length, int seed)
    {
        // Compressible but not trivially so: a repeating ramp with noise.
        var random = new Random(seed);
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)(i % 251 ^ (random.Next(8) == 0 ? random.Next(256) : 0));
        return bytes;
    }

    /// <summary>A ply_save.bin shaped like the game's: 20-byte header and 20 records of 148 bytes.</summary>
    public static byte[] PlySave(uint tidusHp)
    {
        var bytes = new byte[20 + 20 * 148];
        BitConverter.TryWriteBytes(bytes.AsSpan(0x0C), (ushort)148);
        BitConverter.TryWriteBytes(bytes.AsSpan(0x0E), (ushort)(20 * 148));
        BitConverter.TryWriteBytes(bytes.AsSpan(0x10), (ushort)20);
        foreach (int offset in new[] { 0x04, 0x1C, 0x24 })
            BitConverter.TryWriteBytes(bytes.AsSpan(20 + offset), tidusHp);
        return bytes;
    }

    public static string FileHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("ffxr-test-").FullName;
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() => Directory.Delete(Path, recursive: true);
}
