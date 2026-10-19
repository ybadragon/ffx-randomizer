using FfxRandomizer.Core.Game;
using FfxRandomizer.Core.GameData;
using FfxRandomizer.Core.Patching;
using FfxRandomizer.Core.Saves;
using FfxRandomizer.Core.Storage;
using FfxRandomizer.Core.Vbf;

namespace FfxRandomizer.Tests;

public class SaveFileTests
{
    private static readonly Lazy<VbfArchive?> Original = new(() =>
        GameInstall.FindSteamInstall() is { } game ? new PatchManager(game.DataArchivePath, AppPaths.Default).OpenOriginal() : null);

    // A folder of copies of real PC saves (ffx_NNN). Personal saves never go in the repo, so the tests that need them
    // pass trivially unless FFXR_SAVE_COPIES points at some. (The game's own menu/tuto_savedata templates can't stand
    // in: their stored CRCs aren't maintained and match neither table.)
    private static readonly string SaveCopies = Environment.GetEnvironmentVariable("FFXR_SAVE_COPIES") ?? "";

    private static IEnumerable<(string Name, byte[] Bytes)> RealSaves() =>
        Directory.Exists(SaveCopies)
            ? Directory.EnumerateFiles(SaveCopies, "ffx_???").Select(p => (Path.GetFileName(p), File.ReadAllBytes(p)))
            : [];

    private static SaveFile Synthetic()
    {
        var bytes = new byte[SaveFile.FileLength];
        new Random(7).NextBytes(bytes.AsSpan(SaveFile.BlockStart, SaveFile.BlockEnd - SaveFile.BlockStart));
        var save = SaveFile.FromBytes(bytes);
        save.Fix();
        return save;
    }

    [Fact]
    public void The_game_crc_matches_every_save_the_game_wrote_and_the_standard_crc_does_not()
    {
        var files = RealSaves().Select(s => s.Bytes).ToList();
        if (files.Count == 0) return;

        foreach (byte[] file in files)
        {
            ushort stored = BitConverter.ToUInt16(file, SaveFile.CrcInBlock);
            Assert.Equal(stored, SaveFile.ComputeCrc(file));
            Assert.True(SaveFile.FromBytes(file).CrcValid);
        }
        // Entry 0xFF of the game's table is 0; at least one real save must hit it, or this test guards nothing.
        Assert.Contains(files, f => SaveFile.ComputeCrc(f, SaveFile.StandardTable) != BitConverter.ToUInt16(f, SaveFile.CrcInBlock));
    }

    [Fact]
    public void Loading_and_saving_is_byte_identical()
    {
        foreach (var (_, bytes) in RealSaves()) Assert.Equal(bytes, SaveFile.FromBytes(bytes).ToBytes());

        using var dir = new TempDir();
        var save = Synthetic();
        save.Save(dir.File("ffx_000"));
        Assert.Equal(save.ToBytes(), File.ReadAllBytes(dir.File("ffx_000")));
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void Fix_syncs_the_preview_and_both_crc_copies()
    {
        var save = Synthetic();
        save.GameMoment = 1234;
        Assert.False(save.CrcValid);
        save.Fix();
        Assert.True(save.CrcValid);
        byte[] bytes = save.ToBytes();
        Assert.Equal(save.PlayTimeSeconds, BitConverter.ToUInt32(bytes, SaveFile.HeaderPlayTime));
        Assert.Equal(save.Gil, BitConverter.ToUInt32(bytes, SaveFile.HeaderGil));
        Assert.Equal(save.Area, BitConverter.ToUInt16(bytes, SaveFile.HeaderArea));
    }

    [Fact]
    public void Saving_refuses_to_overwrite_and_refuses_a_bad_crc()
    {
        using var dir = new TempDir();
        string path = dir.File("ffx_004");
        File.WriteAllBytes(path, [1, 2, 3]);
        var save = Synthetic();
        Assert.Throws<IOException>(() => save.Save(path));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(path));

        save.GameMoment = 9;
        Assert.Throws<InvalidOperationException>(() => save.Save(dir.File("ffx_005")));
        Assert.False(File.Exists(dir.File("ffx_005")));
    }

    [Theory]
    [InlineData(JumpVariant.A, new[] { 0x40, 0x41, 0x44, 0x45, 0x100, 0x101, 0x104, 0x105 })]
    [InlineData(JumpVariant.B, new[] { 0xF8, 0xFA, 0xFB })]
    [InlineData(JumpVariant.C, new[] { 0x40, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x4C, 0x4E, 0x4F, 0xF8, 0xFA, 0xFB,
        0x100, 0x101, 0x102, 0x103, 0x104, 0x105, 0x106, 0x107 })]
    public void A_jump_changes_only_its_fields(JumpVariant variant, int[] allowed)
    {
        var save = Synthetic();
        byte[] before = save.ToBytes();
        var target = new JumpTarget(0x2E, new MapEntrance(3, 0x7E, 1, 2, 3, 0), Area: 0x55, GameMoment: 316);
        SaveJump.Apply(save, target, variant);
        byte[] after = save.ToBytes();

        var expected = allowed.Concat([SaveFile.GameMomentOffset, SaveFile.GameMomentOffset + 1]).ToHashSet();
        var changed = Enumerable.Range(0, after.Length).Where(i => before[i] != after[i]).ToList();
        var crcAndPreview = Enumerable.Range(0, SaveFile.BlockStart).Concat([SaveFile.CrcInBlock, SaveFile.CrcInBlock + 1]).ToHashSet();
        Assert.All(changed, i => Assert.True(expected.Contains(i) || crcAndPreview.Contains(i), $"byte 0x{i:X} changed"));
        Assert.True(save.CrcValid);
        Assert.Equal(316, save.GameMoment);
        if (variant != JumpVariant.B) Assert.Equal((0x2E, 0x7E), (save.CurrentRoom, save.CurrentMap));
        if (variant != JumpVariant.A) Assert.Equal((0x2E, 3), (save.SavedRoom, save.SavedSpawn));
    }

    [Fact]
    public void Variant_d_also_moves_the_leader_to_the_entrance()
    {
        var save = Synthetic();
        SaveJump.Apply(save, new JumpTarget(1, new MapEntrance(0, 2, 16.5f, -17.4f, 104.5f, 1.57f), null, null), JumpVariant.D);
        Assert.Equal((16.5f, -17.4f, 104.5f), save.Position);
    }

    [Fact]
    public void Free_slots_follow_the_last_used_one()
    {
        using var dir = new TempDir();
        var folder = new SaveFolder(dir.Path);
        Assert.Equal(0, folder.NextFreeSlot());
        foreach (string name in new[] { "ffx_000", "ffx_001", "ffx_003", "ffx_003.tmp", "notes.txt" }) File.WriteAllText(dir.File(name), "");
        Assert.Equal([0, 1, 3], folder.UsedSlots());
        Assert.Equal(4, folder.NextFreeSlot());
        Assert.EndsWith("ffx_004", folder.SlotPath(4));
    }

    [Fact]
    public void Test_saves_are_recorded_ordered_by_time_and_deleted_only_when_ours_and_unchanged()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.File("saves"));
        var folder = new SaveFolder(dir.File("saves"));
        var saves = new TestSaves(folder, dir.File("test-saves.json"), isGameRunning: () => false);
        File.WriteAllText(folder.SlotPath(3), "the player's own save");
        File.SetLastWriteTimeUtc(folder.SlotPath(3), DateTime.UtcNow.AddMinutes(5));

        saves.Write(Synthetic(), 4, "first");
        saves.Write(Synthetic(), 5, "second");
        Assert.Equal([4, 5], saves.Entries.Select(e => e.Slot));
        // Each later slot is newer than everything before it, even the player's save stamped in the future.
        Assert.True(File.GetLastWriteTimeUtc(folder.SlotPath(4)) > File.GetLastWriteTimeUtc(folder.SlotPath(3)));
        Assert.True(File.GetLastWriteTimeUtc(folder.SlotPath(5)) > File.GetLastWriteTimeUtc(folder.SlotPath(4)));
        Assert.Throws<IOException>(() => saves.Write(Synthetic(), 3, "over the player's save"));

        Assert.Throws<InvalidOperationException>(() => saves.Delete(3));
        Assert.True(File.Exists(folder.SlotPath(3)));

        File.AppendAllText(folder.SlotPath(5), "the game saved over it");
        Assert.Throws<InvalidOperationException>(() => saves.Delete(5));
        Assert.True(File.Exists(folder.SlotPath(5)));

        saves.Delete(4);
        Assert.False(File.Exists(folder.SlotPath(4)));
        Assert.Equal([5], saves.Entries.Select(e => e.Slot));
    }

    [Fact]
    public void Test_saves_refuse_while_the_game_runs()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.File("saves"));
        var folder = new SaveFolder(dir.File("saves"));
        var running = false;
        var saves = new TestSaves(folder, dir.File("test-saves.json"), () => running);
        saves.Write(Synthetic(), 4, "test");

        running = true;
        Assert.Throws<InvalidOperationException>(() => saves.Write(Synthetic(), 5, "test"));
        Assert.Throws<InvalidOperationException>(() => saves.Delete(4));
        Assert.False(File.Exists(folder.SlotPath(5)));
        Assert.True(File.Exists(folder.SlotPath(4)));
    }

    [Fact]
    public void Event_locations_name_the_maps_and_areas_the_saves_use()
    {
        if (Original.Value is not { } original) return;

        // From the user's saves: Kilika Woods (klyt0000) is map 131 / area 70, and the residential area (ptkl1300) is map 126.
        var woods = EventLocation.Read(original, "klyt0000");
        Assert.Equal((ushort)131, woods.Map);
        Assert.Equal((ushort)70, woods.AreaNameIndexes[0]);
        var village = EventLocation.Read(original, "ptkl1300");
        Assert.Equal((ushort)126, village.Map);
        Assert.Equal(5, village.Entrances.Count);
        Assert.All(village.Entrances, e => Assert.Equal((ushort)126, e.Map));
    }
}
