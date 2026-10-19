using FfxRandomizer.Core.GameData;

namespace FfxRandomizer.Core.Saves;

/// <summary>
/// Which location fields a jump writes. The save holds the room and spawn point in more than one place, and which one
/// the game loads from isn't known yet; these variants exist to find out in-game.
/// </summary>
public enum JumpVariant
{
    /// <summary>Only the current room and map (0x40/0x44) and their mirror at 0x100.</summary>
    A,
    /// <summary>Only the saved room and spawn point (0xFA/0xF8).</summary>
    B,
    /// <summary>Every room, map and spawn field, with the old room recorded as the last room.</summary>
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
