using System.Text;
using FfxRandomizer.Core.Patching;
using FfxRandomizer.Core.Storage;
using FfxRandomizer.Core.Vbf;

namespace FfxRandomizer.Tests;

public class PatchManagerTests
{
    private static readonly byte[] Data = TestArchive.Bytes(5000, 4);

    private static (TempDir Dir, string Archive, PatchManager Patcher, AppPaths Paths) Setup()
    {
        var dir = new TempDir();
        string archive = dir.File("FFX_Data.vbf");
        TestArchive.Write(archive, [("x/data.bin", Data), ("x/other.bin", TestArchive.Bytes(800, 5))]);
        var paths = new AppPaths(dir.File("appdata"));
        return (dir, archive, new PatchManager(archive, paths, isGameRunning: () => false), paths);
    }

    [Fact]
    public void Apply_then_restore_returns_the_original_archive()
    {
        var (dir, archive, patcher, _) = Setup();
        using var _d = dir;
        string original = TestArchive.FileHash(archive);
        Assert.Equal(GameDataStatus.Unrecognized, patcher.GetStatus());

        patcher.Apply(new Dictionary<string, byte[]> { ["x/data.bin"] = new byte[5000] });
        Assert.Equal(GameDataStatus.Randomized, patcher.GetStatus());

        Assert.True(patcher.Restore());
        Assert.Equal(GameDataStatus.Original, patcher.GetStatus());
        Assert.Equal(original, TestArchive.FileHash(archive));
        Assert.False(patcher.Restore());
    }

    [Fact]
    public void Re_rolling_starts_from_the_original_not_the_previous_run()
    {
        var (dir, archive, patcher, _) = Setup();
        using var _d = dir;
        long originalLength = new FileInfo(archive).Length;

        patcher.Apply(new Dictionary<string, byte[]> { ["x/data.bin"] = Enumerable.Repeat((byte)1, 5000).ToArray() });
        long afterFirst = new FileInfo(archive).Length;
        patcher.Apply(new Dictionary<string, byte[]> { ["x/other.bin"] = new byte[800] });

        Assert.Equal(originalLength + 800 + 16, new FileInfo(archive).Length);
        Assert.True(afterFirst > originalLength);
        var current = VbfArchive.Open(archive);
        Assert.Equal(Data, current.ReadFile("x/data.bin"));
        Assert.Equal(new byte[800], current.ReadFile("x/other.bin"));
    }

    [Fact]
    public void Original_data_is_readable_while_randomized()
    {
        var (dir, _, patcher, _) = Setup();
        using var _d = dir;
        patcher.Apply(new Dictionary<string, byte[]> { ["x/data.bin"] = new byte[5000] });

        Assert.Equal(Data, patcher.OpenOriginal().ReadFile("x/data.bin"));
    }

    [Fact]
    public void A_replaced_archive_is_treated_as_the_new_original()
    {
        var (dir, archive, patcher, _) = Setup();
        using var _d = dir;
        patcher.Apply(new Dictionary<string, byte[]> { ["x/data.bin"] = new byte[5000] });

        // Simulates a Steam update or "Verify integrity of game files".
        TestArchive.Write(archive, [("x/data.bin", TestArchive.Bytes(5000, 7)), ("x/other.bin", TestArchive.Bytes(800, 8))]);
        Assert.Equal(GameDataStatus.Unrecognized, patcher.GetStatus());
        Assert.Equal(TestArchive.Bytes(5000, 7), patcher.OpenOriginal().ReadFile("x/data.bin"));

        string updated = TestArchive.FileHash(archive);
        patcher.Apply(new Dictionary<string, byte[]> { ["x/data.bin"] = new byte[5000] });
        patcher.Restore();
        Assert.Equal(updated, TestArchive.FileHash(archive));
    }

    [Fact]
    public void Reads_state_written_by_the_batch_script()
    {
        var (dir, archive, patcher, paths) = Setup();
        using var _d = dir;
        patcher.Apply(new Dictionary<string, byte[]> { ["x/data.bin"] = new byte[5000] });
        var state = PatchState.Load(paths.StatePath)!;

        // Windows PowerShell's Set-Content -Encoding UTF8 writes a BOM and this layout.
        string batJson = $$"""
            {
                "OriginalFingerprint":  "{{state.OriginalFingerprint}}",
                "OriginalLength":  {{state.OriginalLength}},
                "InputFingerprint":  "{{state.OriginalFingerprint}}",
                "Patched":  true,
                "PatchedFingerprint":  "{{state.PatchedFingerprint}}"
            }
            """;
        File.WriteAllText(paths.StatePath, batJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.Equal(GameDataStatus.Randomized, patcher.GetStatus());
        Assert.True(patcher.Restore());
        Assert.Equal(state.OriginalFingerprint, PatchState.Load(paths.StatePath)!.InputFingerprint);
    }
}
