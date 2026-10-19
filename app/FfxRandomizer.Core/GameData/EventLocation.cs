using System.Buffers.Binary;
using FfxRandomizer.Core.Vbf;

namespace FfxRandomizer.Core.GameData;

/// <summary>One place the party can enter a field: its map and where the leader stands.</summary>
public sealed record MapEntrance(int Index, ushort Map, float X, float Y, float Z, float Yaw);

/// <summary>
/// Where an event (field) puts the party, read from its script header: the map entrances (each names the field's map)
/// and the area-name indexes the menus show.
/// </summary>
public sealed class EventLocation
{
    private const uint Magic = 0x31305645; // "EV01"
    private const int EntranceLength = 0x20;

    public required string EventId { get; init; }
    public required IReadOnlyList<MapEntrance> Entrances { get; init; }
    public required IReadOnlyList<ushort> AreaNameIndexes { get; init; }

    /// <summary>The field's map, as every entrance records it; null for an event with no entrances.</summary>
    public ushort? Map => Entrances.Count > 0 ? Entrances[0].Map : null;

    public static string PathFor(string eventId) => $"{GamePaths.Master}jppc/event/obj/{eventId[..2]}/{eventId}/{eventId}.ebp";

    public static EventLocation Read(VbfArchive archive, string eventId) => FromEbp(eventId, archive.ReadFile(PathFor(eventId)));

    public static EventLocation FromEbp(string eventId, byte[] ebp)
    {
        if (ebp.Length < 8 || U32(ebp, 0) != Magic) throw new InvalidDataException($"{eventId} isn't an event file.");
        int script = (int)U32(ebp, 4);
        int entrancesAt = (int)U32(ebp, script + 0x04), creatorAt = (int)U32(ebp, script + 0x08);
        var entrances = new List<MapEntrance>();
        if (entrancesAt > 0 && entrancesAt < creatorAt)
        {
            for (int i = 0; i < (creatorAt - entrancesAt) / EntranceLength; i++)
            {
                int e = script + entrancesAt + i * EntranceLength;
                entrances.Add(new MapEntrance(i, U16(ebp, e), F32(ebp, e + 0x0C), F32(ebp, e + 0x10), F32(ebp, e + 0x14), F32(ebp, e + 0x08)));
            }
        }

        var areas = new List<ushort>();
        int areaCount = U16(ebp, script + 0x1E), areasAt = (int)U32(ebp, script + 0x28);
        if ((areaCount & 0x8000) != 0 && areasAt > 0)
            for (int i = 0; i < (areaCount & 0x7FFF); i++) areas.Add(U16(ebp, script + areasAt + 2 * i));

        return new EventLocation { EventId = eventId, Entrances = entrances, AreaNameIndexes = areas };
    }

    private static uint U32(byte[] b, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset));
    private static ushort U16(byte[] b, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(offset));
    private static float F32(byte[] b, int offset) => BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(offset));
}
