using FfxRandomizer.Core.GameData;
using FfxRandomizer.Core.GameData.Events;
using FfxRandomizer.Core.Vbf;

namespace FfxRandomizer.Core.Randomization.Features;

/// <summary>
/// Adds the <see cref="SoftReset"/> watcher to every field event it fits in. Runs after the features that edit events
/// (Blitzball tables, cutscene skips) and adds to what they wrote.
/// </summary>
public sealed class SoftResetFeature : IRandomizerFeature
{
    public const string Section = "softreset", Id = "softreset";

    public static bool On(RandomizerSettings s) => s.IsOn(Id);

    public bool IsEnabled(RandomizerSettings s) => On(s);

    public void Apply(RandomizerContext context)
    {
        var leftOut = new List<object>();
        int covered = 0;
        foreach (string id in SoftReset.FieldEvents(context.Original))
        {
            string path = Cutscenes.EventPath(id);
            byte[] current = context.ChangedFiles.TryGetValue(path, out byte[]? edited) ? edited : context.Original.ReadFile(path);
            var (bytes, reason) = SoftReset.Apply(current, SoftReset.CutsceneWorkers(id));
            if (bytes is null)
            {
                leftOut.Add(new { Event = id, Why = reason });
                continue;
            }
            context.Replace(path, bytes);
            covered++;
        }
        context.Spoil(Section, new { FieldEvents = covered, Combo = "hold L1 + R1 + Start for about a second", LeftOut = leftOut });
    }

    public IEnumerable<string> Validate(RandomizerContext context)
    {
        int covered = 0;
        var blitzCopies = new BlitzballStatsFeature().IsEnabled(context.Settings)
            ? BlitzballStats.FindCopies(context.Original).ToDictionary(c => c.Path, StringComparer.OrdinalIgnoreCase)
            : [];
        var cutsceneEvents = new CutsceneSkipFeature().IsEnabled(context.Settings)
            ? Cutscenes.ByEvent.ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase)
            : [];
        foreach (string id in SoftReset.FieldEvents(context.Original))
        {
            string path = Cutscenes.EventPath(id);
            byte[] original = context.Original.ReadFile(path);
            byte[] written = context.ChangedFiles.TryGetValue(path, out byte[]? w) ? w : original;

            // What the earlier features made of this event (they check their own edits): the original, the cutscene
            // skip's build, or the original with the Blitzball tables found in the written file.
            byte[] before = original;
            if (cutsceneEvents.TryGetValue(id, out var scenes))
                before = Cutscenes.Build(context.Original, scenes, CutsceneSkipFeature.Mode(context.Settings), CutsceneSkipFeature.Button(context.Settings))[path];
            if (blitzCopies.TryGetValue(path, out var copy))
            {
                before = (byte[])original.Clone();
                BlitzballStats.FromCopy(written, copy.In(original, written)).WriteTo(before, copy);
            }

            byte[] expected = SoftReset.WithWatcher(path, before);
            if (!written.AsSpan().SequenceEqual(expected))
            {
                yield return $"Soft reset: {path} isn't the watcher added to what the other features wrote.";
                continue;
            }
            if (ReferenceEquals(expected, before)) continue; // left out (`ffxr softreset plan` says why)
            covered++;
            if (VbfArchive.BlocksFor((ulong)written.Length) != VbfArchive.BlocksFor((ulong)original.Length))
                yield return $"Soft reset: {path} changed its 64 KB block count.";
            // Checked without trusting the editor, as the cutscene skip checks its own edits: the code before survives
            // around the added code, entries and jumps moved with their code, the data after the code moved as one block.
            foreach (string problem in CutsceneSkipFeature.CheckEvent(before, written, Cutscenes.Original))
                yield return $"Soft reset: {path}: {problem}";
            if (!HasWatcher(written)) yield return $"Soft reset: {path} has no worker whose main is the watcher.";
        }
        if (covered == 0) yield return "Soft reset: no field event has the watcher.";
    }

    /// <summary>Some worker's main starts at the watcher code, looping through that worker's label at its start.</summary>
    public static bool HasWatcher(byte[] ebp)
    {
        var script = new EventScriptEditor(EventFile.Read(ebp).Chunk(EventFile.ScriptChunk)!);
        for (int w = 0; w < script.WorkerCount; w++)
        {
            if (script.FunctionCount(w) < 2 || script.JumpCount(w) == 0) continue;
            int entry = script.FunctionEntry(w, 1), loop = script.JumpCount(w) - 1;
            byte[] code = SoftReset.WatcherCode(loop);
            if (script.JumpTarget(w, loop) == entry && entry + code.Length <= script.CodeLength && script.Code(entry, code.Length).SequenceEqual(code))
                return true;
        }
        return false;
    }
}
