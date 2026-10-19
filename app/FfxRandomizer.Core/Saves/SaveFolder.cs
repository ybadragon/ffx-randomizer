using System.Text.RegularExpressions;

namespace FfxRandomizer.Core.Saves;

/// <summary>The folder the game reads ffx_NNN saves from, and its slots.</summary>
public sealed partial class SaveFolder(string path)
{
    public string Path { get; } = path;

    /// <summary>
    /// Documents\Square Enix\FINAL FANTASY X&amp;X-2 HD Remaster\FINAL FANTASY X. MyDocuments resolves through
    /// SHGetKnownFolderPath, so a Documents folder redirected to OneDrive is found too.
    /// </summary>
    public static SaveFolder Default => new(System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Square Enix", "FINAL FANTASY X&X-2 HD Remaster", "FINAL FANTASY X"));

    public bool Exists => Directory.Exists(Path);

    public string SlotPath(int slot) => System.IO.Path.Combine(Path, $"ffx_{slot:D3}");

    /// <summary>Slot numbers that already hold a save, in order.</summary>
    public IReadOnlyList<int> UsedSlots() =>
        !Exists ? [] : Directory.EnumerateFiles(Path)
            .Select(f => SlotName().Match(System.IO.Path.GetFileName(f)))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value))
            .Order()
            .ToList();

    /// <summary>The first slot number after the last used one, so new saves sit together at the end of the list.</summary>
    public int NextFreeSlot() => UsedSlots() is [.., var last] ? last + 1 : 0;

    [GeneratedRegex(@"^ffx_(\d{3})$")] private static partial Regex SlotName();
}
