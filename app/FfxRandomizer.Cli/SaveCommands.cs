using System.Text.RegularExpressions;
using FfxRandomizer.Core;
using FfxRandomizer.Core.GameData;
using FfxRandomizer.Core.Saves;

/// <summary>
/// Save-file tools for testing without playing up to a scene.
///   ffxr save info &lt;file&gt;...
///   ffxr save jump --from &lt;file&gt; --event &lt;id&gt; [--room N] [--spawn N] [--moment N] [--variant a|b|c|d] (--slot NNN | --out &lt;file&gt;)
/// --slot writes into the game's save folder, and only into a free slot; --out writes anywhere (and never overwrites).
/// </summary>
internal static partial class SaveCommands
{
    public static int Run(RandomizerService service, string[] args)
    {
        switch (args.FirstOrDefault())
        {
            case "info":
                foreach (string path in args[1..]) Info(path);
                return 0;
            case "jump":
                return Jump(service, args[1..]);
            default:
                Console.Error.WriteLine("Use: ffxr save info <file>... | ffxr save jump --from <file> --event <id> [--room N] [--spawn N] [--moment N] [--variant a|b|c|d] (--slot NNN | --out <file>)");
                return 2;
        }
    }

    private static void Info(string path)
    {
        var save = SaveFile.Read(path);
        string Room(int id) => RoomTable.NameOf(id) is { } name ? $"{id} ({name})" : id.ToString();
        var (x, y, z) = save.Position;
        Console.WriteLine(path);
        Console.WriteLine($"  CRC:        {(save.CrcValid ? "OK" : "MISMATCH")} ({save.StoredCrc:X4})");
        Console.WriteLine($"  GameMoment: {save.GameMoment}");
        Console.WriteLine($"  Room:       {Room(save.CurrentRoom)}, map {save.CurrentMap}; last room {Room(save.LastRoom)}, map {save.LastMap}");
        Console.WriteLine($"  Saved room: {Room(save.SavedRoom)}, spawn {save.SavedSpawn} (0xF8); spawn {save.Spawn} (0x4C)");
        Console.WriteLine($"  Position:   {x:0.##}, {y:0.##}, {z:0.##}");
        Console.WriteLine($"  Area:       {save.Area}");
        Console.WriteLine($"  Gil:        {save.Gil}");
        Console.WriteLine($"  Play time:  {TimeSpan.FromSeconds(save.PlayTimeSeconds)}");
    }

    private static int Jump(RandomizerService service, string[] args)
    {
        string? from = null, eventId = null, outPath = null;
        int? slot = null, room = null, spawn = null, moment = null;
        var variant = JumpVariant.C;
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--from": from = Next(); break;
                case "--event": eventId = Next(); break;
                case "--room": room = int.Parse(Next()); break;
                case "--spawn": spawn = int.Parse(Next()); break;
                case "--moment": moment = int.Parse(Next()); break;
                case "--variant": variant = Enum.Parse<JumpVariant>(Next(), ignoreCase: true); break;
                case "--slot": slot = int.Parse(Next()); break;
                case "--out": outPath = Next(); break;
                default: throw new ArgumentException($"Unknown argument {args[i]}.");
            }
        }
        if (from is null || eventId is null || (slot is null) == (outPath is null))
            throw new ArgumentException("Give --from, --event and exactly one of --slot or --out.");

        var location = EventLocation.Read(service.Patcher.OpenOriginal(), eventId);
        int roomId = room ?? RoomTable.IdOf(eventId)
            ?? throw new ArgumentException($"No room id known for {eventId}; pass --room N (or set FFXDATAPARSER_DIR).");
        int entranceIndex = spawn ?? 0;
        if (entranceIndex < 0 || entranceIndex >= location.Entrances.Count)
            throw new ArgumentException($"{eventId} has {location.Entrances.Count} entrances; --spawn {entranceIndex} is out of range.");
        var target = new JumpTarget(checked((ushort)roomId), location.Entrances[entranceIndex],
            location.AreaNameIndexes.Count > 0 ? location.AreaNameIndexes[0] : null, moment is { } m ? checked((ushort)m) : null);

        var save = SaveFile.Read(from);
        if (!save.CrcValid) throw new InvalidDataException($"{from} has a bad CRC; refusing to build on it.");
        SaveJump.Apply(save, target, variant);

        if (slot is { } n)
        {
            var folder = SaveFolder.Default;
            if (!folder.Exists) throw new DirectoryNotFoundException($"The game's save folder wasn't found at {folder.Path}.");
            outPath = folder.SlotPath(n);
        }
        Console.WriteLine($"Creating {outPath}: {eventId} (room {roomId}, map {target.Entrance.Map}, spawn {entranceIndex}), variant {variant}"
            + (moment is null ? "" : $", GameMoment {moment}"));
        save.Save(outPath!);
        Info(outPath!);
        return 0;
    }

    /// <summary>
    /// Room id ⇄ event name. The game keeps this mapping in code, not in its data files, so this dev tool reads it at run
    /// time from a local FFXDataParser checkout (FFXDATAPARSER_DIR) rather than carrying a copy.
    /// </summary>
    private static partial class RoomTable
    {
        private static readonly Lazy<Dictionary<int, string>> Rooms = new(Load);

        public static string? NameOf(int id) => Rooms.Value.GetValueOrDefault(id);
        public static int? IdOf(string eventId) => Rooms.Value.FirstOrDefault(r => r.Value == eventId) is { Value: not null } r ? r.Key : null;

        private static Dictionary<int, string> Load()
        {
            string dir = Environment.GetEnvironmentVariable("FFXDATAPARSER_DIR") ?? @"C:\Github\Personal\FFXDataParser";
            string source = Path.Combine(dir, "src", "main", "java", "atel", "EventFile.java");
            if (!File.Exists(source)) return [];
            return RoomLine().Matches(File.ReadAllText(source))
                .GroupBy(m => int.Parse(m.Groups[1].Value))
                .ToDictionary(g => g.Key, g => g.First().Groups[2].Value);
        }

        [GeneratedRegex(@"EVENT_BY_ROOM_ID\.put\((\d+),\s*""(\w+)""\)")] private static partial Regex RoomLine();
    }
}
