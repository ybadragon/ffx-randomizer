using FfxRandomizer.Core.GameData;
using FfxRandomizer.Core.GameData.Events;
using FfxRandomizer.Core.Saves;
using FfxRandomizer.Core.Vbf;

/// <summary>
/// Jump-to-scene test saves: one per skippable scene, placed in the scene's room at the entrance the story enters it by,
/// with GameMoment at the value the scene plays at, so loading it starts the scene within seconds.
/// </summary>
internal static class SceneSaves
{
    public sealed record Recipe(SkippableScene Scene, string EventId, int Room, int Spawn, int Moment, string SpawnSource);

    public static int Write(VbfArchive original, string from, string? slots, string? outFolder, string[] areas)
    {
        if ((slots is null) == (outFolder is null)) throw new ArgumentException("Give exactly one of --slots A-B or --out <folder>.");
        var template = SaveFile.Read(from);
        if (!template.CrcValid) throw new InvalidDataException($"{from} has a bad CRC; refusing to build on it.");
        var recipes = Recipes(original, areas).ToList();
        var slotList = new List<int>();
        if (slots is not null)
        {
            var parts = slots.Split('-');
            int first = int.Parse(parts[0]), last = parts.Length > 1 ? int.Parse(parts[1]) : first;
            slotList.AddRange(Enumerable.Range(first, last - first + 1));
            if (slotList.Count < recipes.Count) throw new ArgumentException($"{recipes.Count} scenes need {recipes.Count} slots; {slots} has {slotList.Count}.");
        }
        else Directory.CreateDirectory(outFolder!);

        for (int i = 0; i < recipes.Count; i++)
        {
            var r = recipes[i];
            var location = EventLocation.Read(original, r.EventId);
            var save = SaveFile.Read(from);
            ushort? area = location.AreaNameIndexes.Count > 0 ? location.AreaNameIndexes[0] : null;
            SaveJump.Apply(save, new JumpTarget(checked((ushort)r.Room), location.Entrances[r.Spawn], area, checked((ushort)r.Moment)), JumpVariant.C);
            string purpose = $"Before \"{r.Scene.Name}\" ({r.EventId}, room {r.Room}, spawn {r.Spawn}, GameMoment {r.Moment})";
            string written = slots is not null
                ? TestSaves.Default.Write(save, slotList[i], purpose)
                : SaveTo(save, Path.Combine(outFolder!, $"ffx_{(i + 10):D3}"));
            Console.WriteLine($"{written}: {purpose}; spawn from {r.SpawnSource}");
        }
        return 0;
    }

    private static string SaveTo(SaveFile save, string path)
    {
        if (File.Exists(path)) File.Delete(path); // only ever inside the --out folder this command was given
        save.Save(path);
        return path;
    }

    /// <summary>A recipe per scene in Cutscenes.All (optionally only some areas), in story order.</summary>
    public static IEnumerable<Recipe> Recipes(VbfArchive original, string[] areas)
    {
        // Only the story's own rooms count: the test rooms warp anywhere with any GameMoment.
        var transitions = Transitions(original).Where(t => SoftReset.IsFieldEvent(t.EventId)).ToList();
        var writers = MomentWriters(original);
        foreach (var scene in Cutscenes.All.Where(s => areas.Length == 0 || areas.Any(p => s.EventId.StartsWith(p, StringComparison.OrdinalIgnoreCase))).OrderBy(s => s.Trigger))
        {
            if (SaveCommands.RoomTable.IdOf(scene.EventId) is not int room)
            {
                Console.WriteLine($"No save for \"{scene.Name}\": no room id known for {scene.EventId} (FFXDataParser's room table doesn't list it).");
                continue;
            }
            // The GameMoment the story arrives with: the highest any room sets within the scene's range. (Just below
            // the range's end may be a value the room never handles: bsil0300 sets itself up for 119, not 123.)
            int moment = writers.Keys.Where(m => m >= scene.MomentFrom && m < scene.MomentBelow).DefaultIfEmpty(scene.Trigger).Max();
            var into = transitions.Where(t => t.Room == room).ToList();
            // The entrance the room that sets that moment uses (best where it sets it just before), else the most used one.
            var fromWriter = into.Where(t => writers.GetValueOrDefault(moment)?.Contains(t.EventId) == true)
                .OrderByDescending(t => t.MomentsWritten.Contains(moment)).FirstOrDefault();
            int entrances = EventLocation.Read(original, scene.EventId).Entrances.Count;
            int spawn;
            string source;
            if (fromWriter is not null && fromWriter.Spawn < entrances) (spawn, source) = (fromWriter.Spawn, $"{fromWriter.EventId} {fromWriter.Offset:X4} (its room sets GameMoment {moment})");
            else if (into.Where(t => t.Spawn < entrances).GroupBy(t => t.Spawn).OrderByDescending(g => g.Count()).FirstOrDefault() is { } common)
                (spawn, source) = (common.Key, $"most used entrance ({common.Count()} ways in)");
            else (spawn, source) = (0, "no way in found; entrance 0");
            yield return new Recipe(scene, scene.EventId, room, spawn, moment, source);
        }
    }

    /// <summary>Every GameMoment value the story's rooms set, with the rooms that set it.</summary>
    private static Dictionary<int, HashSet<string>> MomentWriters(VbfArchive original)
    {
        var writers = new Dictionary<int, HashSet<string>>();
        foreach (string id in SoftReset.FieldEvents(original))
        {
            var tables = new ScriptTables(EventFile.Read(original.ReadFile(Cutscenes.EventPath(id))).Chunk(EventFile.ScriptChunk)!);
            byte[] code = tables.Code.ToArray();
            for (int at = 0; at < code.Length && AtelCode.StatementAt(code, at) is { } s; at = s.End)
                if (AtelCode.MomentWrite(s, tables) is int m)
                    (writers.TryGetValue(m, out var set) ? set : writers[m] = []).Add(id);
        }
        return writers;
    }

    private sealed record Transition(string EventId, int Offset, int Room, int Spawn, IReadOnlySet<int> MomentsWritten);

    /// <summary>Every room change with constant arguments in every event, with the GameMoment values its function writes.</summary>
    private static IEnumerable<Transition> Transitions(VbfArchive original)
    {
        foreach (string name in original.Names.Where(n => n.StartsWith(GamePaths.Master + "jppc/event/obj/", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".ebp", StringComparison.OrdinalIgnoreCase)))
        {
            string id = Path.GetFileNameWithoutExtension(name);
            ScriptTables tables;
            try { tables = new ScriptTables(EventFile.Read(original.ReadFile(name)).Chunk(EventFile.ScriptChunk)!); }
            catch (InvalidDataException) { continue; }
            byte[] code = tables.Code.ToArray();
            var statements = new List<AtelStatement>();
            for (int at = 0; at < code.Length && AtelCode.StatementAt(code, at) is { } s; at = s.End) statements.Add(s);
            for (int i = 0; i < statements.Count; i++)
            {
                var s = statements[i];
                var ins = s.Instructions;
                if (ins.Count < 3 || ins[^1].Op != AtelCode.CallPopA || !SceneAudit.WarpCalls.Contains(ins[^1].Operand)) continue;
                if (AtelCode.Constant(ins[^3], tables) is not int room || AtelCode.Constant(ins[^2], tables) is not int spawn) continue;
                // GameMoment written shortly before, in the same stretch of code.
                var written = statements.Skip(Math.Max(0, i - 12)).Take(Math.Min(12, i)).Select(t => AtelCode.MomentWrite(t, tables)).OfType<int>().ToHashSet();
                yield return new Transition(id, s.Offset, room, spawn, written);
            }
        }
    }
}
