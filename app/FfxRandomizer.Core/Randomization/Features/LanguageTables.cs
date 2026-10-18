using FfxRandomizer.Core.GameData;

namespace FfxRandomizer.Core.Randomization.Features;

/// <summary>
/// Kernel tables the game keeps once per language (ply_save.bin, ply_rom.bin). The copies hold the same numbers and
/// differ only in their text offsets, so features change numeric fields in every copy alike and never copy one
/// language's records over another's (which is how FFXRando's party stats broke other languages' names).
/// </summary>
internal static class LanguageTables
{
    public const string Reference = "new_uspc";

    /// <summary>The working copy of every language's table, reference language first.</summary>
    public static IReadOnlyList<KernelTable> Edit(RandomizerContext context, string file) =>
        [.. new[] { Reference }.Concat(GamePaths.LanguageFolders.Where(l => l != Reference))
            .Select(language => new KernelTable(context.Edit(GamePaths.LanguageKernel(language, file))))];

    public static KernelTable Original(RandomizerContext context, string file) =>
        new(context.Original.ReadFile(GamePaths.LanguageKernel(Reference, file)));

    /// <summary>
    /// Checks that every language's copy kept its size and still matches the reference copy from
    /// <paramref name="numbersFrom"/> on in every record, i.e. no language was left behind or overwritten.
    /// </summary>
    public static IEnumerable<string> CheckInSync(RandomizerContext context, string file, int numbersFrom)
    {
        string referencePath = GamePaths.LanguageKernel(Reference, file);
        if (!context.ChangedFiles.TryGetValue(referencePath, out byte[]? referenceBytes)) yield break;
        var reference = new KernelTable(referenceBytes);
        foreach (string language in GamePaths.LanguageFolders)
        {
            string path = GamePaths.LanguageKernel(language, file);
            byte[] original = context.Original.ReadFile(path);
            if (!context.ChangedFiles.TryGetValue(path, out byte[]? bytes))
            {
                yield return $"{language}/{file}: not changed along with the other languages.";
                continue;
            }
            if (bytes.Length != original.Length)
            {
                yield return $"{language}/{file}: size changed.";
                continue;
            }
            var table = new KernelTable(bytes);
            var before = new KernelTable(original);
            for (int r = 0; r < table.RecordCount; r++)
            {
                if (!table.Record(r)[numbersFrom..].SequenceEqual(reference.Record(r)[numbersFrom..]))
                    yield return $"{language}/{file} record {r}: numbers differ from {Reference}.";
                if (!table.Record(r)[..numbersFrom].SequenceEqual(before.Record(r)[..numbersFrom]))
                    yield return $"{language}/{file} record {r}: its text offsets changed.";
            }
        }
    }

    /// <summary>FFXRando's normal&lt;T&gt;: a value spread around itself by half its size; 0 stays 0.</summary>
    public static int Spread(Rng rng, int value, int min, int max) =>
        value == 0 ? 0 : rng.Normal(value, value / 2, min, Math.Max(min, max));
}
