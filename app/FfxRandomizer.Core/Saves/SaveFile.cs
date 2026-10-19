using System.Buffers.Binary;

namespace FfxRandomizer.Core.Saves;

/// <summary>
/// One FINAL FANTASY X HD Remaster PC save (ffx_NNN): a 0x40-byte header the load menu previews, the PS2 save-data
/// block (FFXDataParser's save-variable offsets + 0x40), then zero padding. A CRC over the block is stored twice.
/// On load the game places the player by <see cref="SavedRoom"/> and <see cref="SavedSpawn"/>; the load menu shows the
/// header's area, play time and gil, numbers slots by file name (ffx_000 is the autosave), lists up to 200, and shows
/// and sorts by the file's modified time.
/// </summary>
public sealed class SaveFile
{
    public const int FileLength = 0x6900;
    public const int BlockStart = 0x40;
    public const int BlockEnd = 0x64F8;
    public const int CrcInHeader = 0x1A;
    public const int CrcInBlock = 0x64F4;

    // Header preview fields (outside the CRC).
    public const int HeaderPlayTime = 0x10, HeaderGil = 0x14, HeaderArea = 0x18;

    // Body fields, as file offsets.
    public const int CurrentRoomOffset = 0x40, LastRoomOffset = 0x42, CurrentMapOffset = 0x44, LastMapOffset = 0x46;
    public const int SpawnOffset = 0x4C, AreaOffset = 0x4E;
    public const int PositionOffset = 0xDC; // three floats: x, y, z
    public const int SavedSpawnOffset = 0xF8, SavedRoomOffset = 0xFA, PlayTimeOffset = 0xFC;
    public const int LocationMirrorOffset = 0x100; // copy of 0x40-0x47
    public const int GameMomentOffset = 0xC2C;
    public const int GilOffset = 0x3D88;

    private readonly byte[] _bytes;

    private SaveFile(byte[] bytes) => _bytes = bytes;

    public static SaveFile Read(string path) => FromBytes(File.ReadAllBytes(path));

    public static SaveFile FromBytes(byte[] bytes)
    {
        if (bytes.Length != FileLength) throw new InvalidDataException($"A save is {FileLength} bytes; this file is {bytes.Length}.");
        return new SaveFile((byte[])bytes.Clone());
    }

    public byte[] ToBytes() => (byte[])_bytes.Clone();

    public ushort StoredCrc => U16(CrcInHeader);
    public bool CrcValid => StoredCrc == ComputeCrc(_bytes) && U16(CrcInBlock) == StoredCrc;

    public ushort GameMoment { get => U16(GameMomentOffset); set => SetU16(GameMomentOffset, value); }
    public ushort CurrentRoom { get => U16(CurrentRoomOffset); set => SetU16(CurrentRoomOffset, value); }
    public ushort LastRoom { get => U16(LastRoomOffset); set => SetU16(LastRoomOffset, value); }
    public ushort CurrentMap { get => U16(CurrentMapOffset); set => SetU16(CurrentMapOffset, value); }
    public ushort LastMap { get => U16(LastMapOffset); set => SetU16(LastMapOffset, value); }
    public byte Spawn { get => _bytes[SpawnOffset]; set => _bytes[SpawnOffset] = value; }
    public byte SavedSpawn { get => _bytes[SavedSpawnOffset]; set => _bytes[SavedSpawnOffset] = value; }
    public ushort SavedRoom { get => U16(SavedRoomOffset); set => SetU16(SavedRoomOffset, value); }
    public ushort Area { get => U16(AreaOffset); set => SetU16(AreaOffset, value); }
    public uint PlayTimeSeconds => BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(PlayTimeOffset));
    public uint Gil => BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(GilOffset));

    public (float X, float Y, float Z) Position
    {
        get => (F32(PositionOffset), F32(PositionOffset + 4), F32(PositionOffset + 8));
        set
        {
            SetF32(PositionOffset, value.X);
            SetF32(PositionOffset + 4, value.Y);
            SetF32(PositionOffset + 8, value.Z);
        }
    }

    /// <summary>Copies the location block 0x40-0x47 (or only its current room and map) to its mirror at 0x100.</summary>
    public void MirrorLocation(bool currentOnly = false)
    {
        if (!currentOnly)
        {
            _bytes.AsSpan(CurrentRoomOffset, 8).CopyTo(_bytes.AsSpan(LocationMirrorOffset));
            return;
        }
        SetU16(LocationMirrorOffset, CurrentRoom);
        SetU16(LocationMirrorOffset + CurrentMapOffset - CurrentRoomOffset, CurrentMap);
    }

    /// <summary>Brings the load-menu preview in line with the body and recomputes both CRC copies.</summary>
    public void Fix()
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_bytes.AsSpan(HeaderPlayTime), PlayTimeSeconds);
        BinaryPrimitives.WriteUInt32LittleEndian(_bytes.AsSpan(HeaderGil), Gil);
        SetU16(HeaderArea, Area);
        ushort crc = ComputeCrc(_bytes);
        SetU16(CrcInHeader, crc);
        SetU16(CrcInBlock, crc);
    }

    /// <summary>
    /// Writes through a temporary file and a rename, so a half-written save never appears under the slot's name.
    /// Refuses to replace an existing file unless <paramref name="overwrite"/> is set.
    /// </summary>
    public void Save(string path, bool overwrite = false)
    {
        if (!overwrite && File.Exists(path)) throw new IOException($"{path} already exists; not overwriting it.");
        if (!CrcValid) throw new InvalidOperationException("The save's CRC doesn't match its contents; call Fix() first.");
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllBytes(temp, _bytes);
        try
        {
            File.Move(temp, path, overwrite);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    // ---------- CRC ----------

    /// <summary>
    /// CRC-16/CCITT (poly 0x1021, MSB first, init and xorout 0xFFFF) over the save block, with both stored copies read as
    /// zero. The game's table has entry 0xFF set to 0 instead of 0x1EF0, so a standard CRC-16 gives the wrong answer.
    /// </summary>
    public static ushort ComputeCrc(ReadOnlySpan<byte> file) => ComputeCrc(file, GameTable);

    internal static ushort ComputeCrc(ReadOnlySpan<byte> file, ushort[] table)
    {
        ushort crc = 0xFFFF;
        for (int i = BlockStart; i < BlockEnd; i++)
        {
            byte x = i is CrcInBlock or CrcInBlock + 1 ? (byte)0 : file[i];
            crc = (ushort)((crc << 8) ^ table[((crc >> 8) ^ x) & 0xFF]);
        }
        return (ushort)(crc ^ 0xFFFF);
    }

    internal static readonly ushort[] StandardTable = BuildTable();
    private static readonly ushort[] GameTable = BuildGameTable();

    private static ushort[] BuildTable()
    {
        var table = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            int c = i << 8;
            for (int bit = 0; bit < 8; bit++) c = (c & 0x8000) != 0 ? (c << 1) ^ 0x1021 : c << 1;
            table[i] = (ushort)c;
        }
        return table;
    }

    private static ushort[] BuildGameTable()
    {
        var table = BuildTable();
        table[0xFF] = 0;
        return table;
    }

    private ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(offset));
    private void SetU16(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(_bytes.AsSpan(offset), value);
    private float F32(int offset) => BinaryPrimitives.ReadSingleLittleEndian(_bytes.AsSpan(offset));
    private void SetF32(int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(_bytes.AsSpan(offset), value);
}
