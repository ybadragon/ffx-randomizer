using FfxRandomizer.Core.GameData;
using FfxRandomizer.Core.Vbf;

namespace FfxRandomizer.Core.Randomization.Features;

/// <summary>Lets the scenes in <see cref="Cutscenes.All"/> be skipped, always or by holding Select as they start.</summary>
public sealed class CutsceneSkipFeature : IRandomizerFeature
{
    public const string Section = "cutscenes";
    public const string Original = "orig", HoldSelect = "hold", Always = "always", HoldAnyButton = "anybutton", PressSelect = "press";

    public bool IsEnabled(RandomizerSettings s) => Mode(s) != Original;

    public static string Mode(RandomizerSettings s) => s.ChoiceOf(OptionCatalog.AllGroups.First(g => g.Id == "cutscenes"));

    public static EventPatch PatchFor(SkippableScene scene, string mode) => mode switch
    {
        Always => scene.Always,
        HoldAnyButton => scene.HoldAnyButton,
        PressSelect => scene.PressSelect ?? scene.HoldSelect,
        _ => scene.HoldSelect,
    };

    public void Apply(RandomizerContext context)
    {
        string mode = Mode(context.Settings);
        foreach (var scene in Cutscenes.All)
        {
            var patch = PatchFor(scene, mode);
            context.Replace(patch.Path, patch.Apply(context.Edit(patch.Path)));
            context.Spoil(Section, new
            {
                Scene = scene.Name,
                scene.Area,
                Event = patch.EventId,
                Skip = mode switch
                {
                    Always => "always",
                    HoldAnyButton => "hold any button as it starts",
                    PressSelect => "press Select during the scene",
                    _ => "hold Select as it starts",
                },
            });
        }
    }

    public IEnumerable<string> Validate(RandomizerContext context)
    {
        string mode = Mode(context.Settings);
        foreach (var scene in Cutscenes.All)
        {
            var patch = PatchFor(scene, mode);
            byte[] original = context.Original.ReadFile(patch.Path);
            if (!context.ChangedFiles.TryGetValue(patch.Path, out byte[]? patched))
            {
                yield return $"{scene.Name}: {patch.EventId} wasn't changed.";
                continue;
            }
            foreach (string problem in CheckInsertion(original, patched, patch)) yield return $"{scene.Name}: {problem}";
        }
    }

    /// <summary>
    /// Checks that <paramref name="patched"/> is <paramref name="original"/> with exactly <paramref name="patch"/>'s code
    /// inserted: the same block count, every byte outside the code and the entry/jump tables unchanged, the code before
    /// the insertion unchanged and the code after it shifted intact, and every entry point and jump moved with its code.
    /// </summary>
    internal static IEnumerable<string> CheckInsertion(byte[] original, byte[] patched, EventPatch patch)
    {
        if (VbfArchive.BlocksFor((ulong)patched.Length) != VbfArchive.BlocksFor((ulong)original.Length))
        {
            yield return "the file's 64 KB block count changed.";
            yield break;
        }
        var before = new EventScript(original);
        var after = new EventScript(patched);
        int at = patch.CodeOffset, n = patch.Bytes.Length;
        if (patched.Length != original.Length) yield return $"the file size changed from {original.Length} to {patched.Length}.";
        if (after.CodeLength != before.CodeLength + n) yield return $"the code length is {after.CodeLength:X}, expected {before.CodeLength + n:X}.";
        if (!after.Code(0, at).SequenceEqual(before.Code(0, at))) yield return "code before the insertion changed.";
        if (!after.Code(at, n).SequenceEqual(patch.Bytes)) yield return "the inserted check isn't in place.";
        if (!after.Code(at + n, before.CodeLength - at).SequenceEqual(before.Code(at, before.CodeLength - at)))
            yield return "code after the insertion didn't move intact.";

        for (int w = 0; w < before.WorkerCount; w++)
        {
            for (int f = 0; f < before.FunctionCount(w); f++)
            {
                int was = before.FunctionEntry(w, f), expected = was > at ? was + n : was;
                if (after.FunctionEntry(w, f) != expected) yield return $"w{w:X2}::f{f:X2} starts at {after.FunctionEntry(w, f):X4}, expected {expected:X4}.";
            }
            for (int j = 0; j < before.JumpCount(w); j++)
            {
                int was = before.JumpTarget(w, j), expected = was > at ? was + n : was;
                if (after.JumpTarget(w, j) != expected) yield return $"w{w:X2} j{j:X2} points at {after.JumpTarget(w, j):X4}, expected {expected:X4}.";
            }
        }

        // Everything else in the file: the bytes outside the code region, apart from the code length and the tables above.
        var allowed = new HashSet<int>();
        for (int i = 0; i < 4; i++) allowed.Add(before.CodeLengthField + i);
        foreach (int slot in before.EntryAndJumpSlots())
            for (int i = 0; i < 4; i++) allowed.Add(slot + i);
        int codeStart = before.CodeStart, codeEnd = codeStart + before.CodeLength + n;
        for (int i = 0; i < original.Length; i++)
        {
            if (i >= codeStart && i < codeEnd) continue;
            if (patched[i] != original[i] && !allowed.Contains(i))
            {
                yield return $"byte 0x{i:X} outside the code changed.";
                yield break;
            }
        }
    }
}
