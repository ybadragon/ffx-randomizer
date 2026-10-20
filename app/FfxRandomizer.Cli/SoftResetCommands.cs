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
        if (args.FirstOrDefault() == "gatetmp")
        {
            int bad = 0;
            foreach (string id in SoftReset.FieldEvents(original))
            {
                var c = new EventScriptEditor(EventFile.Read(original.ReadFile(Cutscenes.EventPath(id))).Chunk(EventFile.ScriptChunk)!).UntrackedPointerCandidates();
                if (c.Count == 0) continue;
                bad++;
                Console.WriteLine($"{id}: {c.Count} {string.Join(" ", c.Take(6).Select(x => $"{x.Field:X}->{x.Value:X}"))}");
            }
            Console.WriteLine($"{bad} field events gated");
            return 0;
        }
        if (args.FirstOrDefault() == "metatmp")
        {
            foreach (string n in original.Names.Where(n => n.EndsWith(".ebp", StringComparison.OrdinalIgnoreCase)))
            {
                byte[] s = EventFile.Read(original.ReadFile(n)).Chunk(EventFile.ScriptChunk)!;
                int meta = BitConverter.ToInt32(s, 0x2C);
                if (meta == 0) continue;
                var odd = new[] { 0x04, 0x08, 0x14, 0x20, 0x30, 0x34, 0x38 }.Where(f => BitConverter.ToInt32(s, meta + f) != 0).Select(f => $"+{f:X}={BitConverter.ToInt32(s, meta + f):X}").ToList();
                if (odd.Count > 0) Console.WriteLine($"{Path.GetFileNameWithoutExtension(n)} meta {meta:X}: {string.Join(" ", odd)}");
            }
            return 0;
        }
        if (args.FirstOrDefault() == "pointerstmp")
        {
            foreach (string id in args.Length > 1 ? args[1..] : SoftReset.FieldEvents(original).ToArray())
            {
                byte[] chunk = EventFile.Read(original.ReadFile(Cutscenes.EventPath(id))).Chunk(EventFile.ScriptChunk)!;
                var ed = new EventScriptEditor(chunk);
                byte[] s = ed.Chunk.ToArray();
                int codeEnd = ed.CodeStart + ed.CodeLength, end = s.Length;
                var known = ed.KnownPointerFields.ToHashSet();
                var targets = known.Select(f => BitConverter.ToInt32(s, f)).Where(v => v != 0).ToHashSet();
                var suspects = new List<string>();
                for (int at = (codeEnd + 3) & ~3; at + 4 <= end; at += 4)
                {
                    int v = BitConverter.ToInt32(s, at);
                    if (known.Contains(at) || v < codeEnd || v >= end || v % 4 != 0) continue;
                    suspects.Add($"{at:X}->{v:X}{(targets.Contains(v) ? "*" : "")}");
                }
                Console.WriteLine($"{id} code end {codeEnd:X} chunk {end:X} file {original.ReadFile(Cutscenes.EventPath(id)).Length:X} suspects {suspects.Count}: {string.Join(" ", suspects.Take(12))}");
                if (args.Contains("--layout"))
                {
                    int U(int at) => BitConverter.ToInt32(s, at);
                    Console.WriteLine($"  header: entrances {U(0x04):X} creator {U(0x08):X} id {U(0x0C):X} end {U(0x10):X} eventData {U(0x20):X} mapTable {U(0x24):X} areaNames {U(0x28):X} meta {U(0x2C):X} code {U(0x30):X} shared {ed.SharedDataStart:X}");
                    int meta = U(0x2C);
                    if (meta != 0) Console.WriteLine($"  meta: " + string.Join(" ", new[] { 0x0C, 0x10, 0x18, 0x1C, 0x24, 0x28, 0x2C, 0x3C }.Select(f => $"+{f:X}={U(meta + f):X}")) + (U(meta + 0x3C) != 0 ? $" map+8={U(U(meta + 0x3C) + 8):X} map+C={U(U(meta + 0x3C) + 0xC):X}" : ""));
                    for (int w = 0; w < ed.WorkerCount; w++)
                    {
                        int h = U(0x38 + w * 4);
                        Console.WriteLine($"  w{w:X2}: entries {U(h + 0x20):X} jumps {U(h + 0x24):X} private {U(h + 0x2C):X} len {U(h + 0x10):X} vars {U(h + 0x14):X} refInts {U(h + 0x18):X} refFloats {U(h + 0x1C):X}");
                    }
                    Console.WriteLine($"  words around 56C0: {Convert.ToHexString(s, Math.Min(0x56C0, s.Length - 64), 64)}");
                }
            }
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
