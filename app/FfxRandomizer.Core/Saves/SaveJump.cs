using FfxRandomizer.Core.GameData;

namespace FfxRandomizer.Core.Saves;

/// <summary>
/// Which location fields a jump writes. The save holds the room and spawn point in more than one place. Tested in-game
/// (Kilika Woods save moved to Kilika's residential area): the game places the player by the saved room and spawn point
/// (0xFA/0xF8) and ignores the current room and map (0x40/0x44) on load. The load menu's location name and picture come
/// from the area field (header 0x18, body 0x4E). C sets all of them consistently and is the default; the others remain
/// for diagnosing other saves.
/// </summary>
public enum JumpVariant
{
    /// <summary>Only the current room and map (0x40/0x44) and their mirror at 0x100. Loads in the template's old room.</summary>
    A,
    /// <summary>Only the saved room and spawn point (0xFA/0xF8). Loads in the new room, but the menu shows the old location.</summary>
    B,
    /// <summary>Every room, map, spawn and area field, with the old room recorded as the last room. Loads in the new room.</summary>
    C,
    /// <summary>As C, plus the leader's position set to the entrance's.</summary>
    D,
}

/// <summary>Where a jump-to-scene save should put the party.</summary>
public sealed record JumpTarget(ushort Room, MapEntrance Entrance, ushort? Area, ushort? GameMoment);

public static class SaveJump
{
    /// <summary>Moves <paramref name="save"/> to <paramref name="target"/> using <paramref name="variant"/>'s fields, then fixes the CRC.</summary>
    public static void Apply(SaveFile save, JumpTarget target, JumpVariant variant)
    {
        byte spawn = checked((byte)target.Entrance.Index);
        if (variant is JumpVariant.A or JumpVariant.C or JumpVariant.D)
        {
            if (variant != JumpVariant.A)
            {
                save.LastRoom = save.CurrentRoom;
                save.LastMap = save.CurrentMap;
            }
            save.CurrentRoom = target.Room;
            save.CurrentMap = target.Entrance.Map;
            save.MirrorLocation(currentOnly: variant == JumpVariant.A);
        }
        if (variant is JumpVariant.B or JumpVariant.C or JumpVariant.D)
        {
            save.SavedRoom = target.Room;
            save.SavedSpawn = spawn;
        }
        if (variant is JumpVariant.C or JumpVariant.D)
        {
            save.Spawn = spawn;
            if (target.Area is { } area) save.Area = area;
        }
        if (variant == JumpVariant.D) save.Position = (target.Entrance.X, target.Entrance.Y, target.Entrance.Z);
        if (target.GameMoment is { } moment) save.GameMoment = moment;
        save.Fix();
    }
}
