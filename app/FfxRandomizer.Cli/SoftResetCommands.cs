using FfxRandomizer.Core;
using FfxRandomizer.Core.GameData;
using FfxRandomizer.Core.GameData.Events;

/// <summary>
/// Soft reset tools (read-only on the game):
///   ffxr softreset plan     every field event, the worker its watcher would replace the main of, or why it's left out
/// </summary>
internal static class SoftResetCommands
{
    public static int Run(RandomizerService service, string[] args)
    {
        var original = service.Patcher.OpenOriginal();
        if (args.FirstOrDefault() == "buttonstmp")
        {
            var c = new Dictionary<string, int>();
            foreach (string n in original.Names.Where(n => n.EndsWith(".ebp", StringComparison.OrdinalIgnoreCase)))
            {
                var t = new ScriptTables(EventFile.Read(original.ReadFile(n)).Chunk(EventFile.ScriptChunk)!);
                byte[] code = t.Code.ToArray();
                for (int at = 0; at < code.Length && AtelCode.StatementAt(code, at) is { } s; at = s.End)
                    for (int i = 1; i < s.Instructions.Count; i++)
                        if (s.Instructions[i].Op is 0xB5 or 0xD8 && s.Instructions[i].Operand is 0x4C or 0x4D or 0x4E or 0x4F or 0x50 && AtelCode.Constant(s.Instructions[i - 1], t) is int btn && btn is 9 or 10)
                        { string k = $"{Path.GetFileNameWithoutExtension(n)} call {s.Instructions[i].Operand:X4} button {btn}"; c[k] = c.GetValueOrDefault(k) + 1; }
            }
            foreach (var (k, v) in c) Console.WriteLine($"{v,4} {k}");
            return 0;
        }
        if (args.FirstOrDefault() != "plan")
        {
            Console.Error.WriteLine("Use: ffxr softreset plan");
            return 2;
        }
        int covered = 0, left = 0;
        long added = 0;
        foreach (string id in SoftReset.FieldEvents(original))
        {
            byte[] ebp = original.ReadFile(Cutscenes.EventPath(id));
            var host = SoftReset.HostFor(EventFile.Read(ebp).Chunk(EventFile.ScriptChunk)!, SoftReset.CutsceneWorkers(id));
            var (bytes, reason) = SoftReset.Apply(ebp, SoftReset.CutsceneWorkers(id), SoftReset.ComboIndexes(null));
            if (bytes is null)
            {
                left++;
                Console.WriteLine($"{id}  LEFT OUT: {reason}");
                continue;
            }
            covered++;
            added += bytes.Length - ebp.Length;
            var tables = new ScriptTables(EventFile.Read(ebp).Chunk(EventFile.ScriptChunk)!);
            string where = host.Worker is int h ? $"w{h:X2} (type {tables.WorkerType(h)}, {host.Why})"
                : $"own new worker w{SoftReset.NewWorkerPlan(tables).Index:X2} ({SoftReset.NewWorkerPlan(tables).Patches.Count} references renumbered)";
            Console.WriteLine($"{id}  {where}  +{bytes.Length - ebp.Length} bytes");
        }
        Console.WriteLine($"{covered} covered, {left} left out, {added / 1024.0:F0} KB added");

        // What applying it costs: the randomizer run (edit plus checks) and what the archive grows by (each changed file
        // is appended whole).
        var settings = new FfxRandomizer.Core.Randomization.RandomizerSettings();
        settings.Toggles[FfxRandomizer.Core.Randomization.Features.SoftResetFeature.Id] = true;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = FfxRandomizer.Core.Randomization.RandomizerEngine.Run(original, settings, new FfxRandomizer.Core.Randomization.Seed("plan"));
        Console.WriteLine($"Randomizer run (edit and checks): {clock.Elapsed.TotalSeconds:F1} s, {result.Problems.Count} problems, "
            + $"appends {result.Files.Values.Sum(f => (long)f.Length) / 1024.0 / 1024.0:F1} MB to the archive");
        foreach (string problem in result.Problems.Take(10)) Console.WriteLine($"  {problem}");
        return 0;
    }
}
