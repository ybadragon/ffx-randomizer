using FfxRandomizer.Core.Vbf;

namespace FfxRandomizer.Tests;

public class VbfArchiveTests
{
    private static readonly byte[] Small = TestArchive.Bytes(3000, 1);
    private static readonly byte[] Large = TestArchive.Bytes(150_000, 2);
    private static readonly byte[] Exact = TestArchive.Bytes(VbfArchive.BlockSize, 3);

    private static string Build(TempDir dir)
    {
        string path = dir.File("FFX_Data.vbf");
        TestArchive.Write(path, [("a/small.bin", Small), ("a/large.bin", Large), ("b/exact.bin", Exact)]);
        return path;
    }

    [Fact]
    public void Reads_files_across_compressed_and_stored_blocks()
    {
        using var dir = new TempDir();
        var archive = VbfArchive.Open(Build(dir));

        Assert.True(archive.HeaderChecksumValid);
        Assert.Equal(["a/small.bin", "a/large.bin", "b/exact.bin"], archive.Names);
        Assert.Equal(Small, archive.ReadFile("a/small.bin"));
        Assert.Equal(Large, archive.ReadFile("A/LARGE.BIN"));
        Assert.Equal(Exact, archive.ReadFile("b/exact.bin"));
    }

    [Fact]
    public void Patch_replaces_only_the_given_files_and_keeps_the_header_valid()
    {
        using var dir = new TempDir();
        string path = Build(dir);
        long originalLength = new FileInfo(path).Length;
        var newLarge = TestArchive.Bytes(140_000, 9);

        VbfArchive.Open(path).Patch(new Dictionary<string, byte[]> { ["a/large.bin"] = newLarge }, originalLength);

        var patched = VbfArchive.Open(path);
        Assert.True(patched.HeaderChecksumValid);
        Assert.Equal(newLarge, patched.ReadFile("a/large.bin"));
        Assert.Equal(Small, patched.ReadFile("a/small.bin"));
        Assert.Equal(Exact, patched.ReadFile("b/exact.bin"));
    }

    [Fact]
    public void Original_header_still_reads_the_original_data_after_patching()
    {
        using var dir = new TempDir();
        string path = Build(dir);
        byte[] header = VbfArchive.ReadHeaderBytes(path);
        VbfArchive.Open(path).Patch(new Dictionary<string, byte[]> { ["a/small.bin"] = new byte[3000] }, new FileInfo(path).Length);

        Assert.Equal(Small, VbfArchive.Open(path, header).ReadFile("a/small.bin"));
    }

    [Fact]
    public void Restore_returns_the_archive_byte_for_byte()
    {
        using var dir = new TempDir();
        string path = Build(dir);
        string before = TestArchive.FileHash(path);
        byte[] header = VbfArchive.ReadHeaderBytes(path);
        long length = new FileInfo(path).Length;

        VbfArchive.Open(path).Patch(new Dictionary<string, byte[]> { ["a/large.bin"] = new byte[150_000] }, length);
        Assert.NotEqual(before, TestArchive.FileHash(path));

        VbfArchive.Restore(path, header, length);
        Assert.Equal(before, TestArchive.FileHash(path));
    }

    [Fact]
    public void Patch_refuses_a_file_whose_block_count_changes()
    {
        using var dir = new TempDir();
        string path = Build(dir);
        string before = TestArchive.FileHash(path);

        Assert.Throws<InvalidOperationException>(() =>
            VbfArchive.Open(path).Patch(new Dictionary<string, byte[]> { ["a/small.bin"] = new byte[70_000] }, new FileInfo(path).Length));
        Assert.Equal(before, TestArchive.FileHash(path));
    }

    [Fact]
    public void Patch_refuses_unknown_files()
    {
        using var dir = new TempDir();
        string path = Build(dir);
        Assert.Throws<FileNotFoundException>(() =>
            VbfArchive.Open(path).Patch(new Dictionary<string, byte[]> { ["nope.bin"] = [1] }, new FileInfo(path).Length));
    }
}
