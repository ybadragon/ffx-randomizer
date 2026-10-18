using System.Text.Json;
using System.Text.Json.Nodes;
using FfxRandomizer.Core.Randomization;

namespace FfxRandomizer.Core.Storage;

public static class SpoilerLog
{
    public const string Format = "ffx-randomizer-spoiler";
    public const int FormatVersion = 1;

    /// <summary>
    /// Writes the spoiler as JSON: run details first, then one section per area of the game
    /// ("party", later "enemies", "shops", ...) holding exactly what changed.
    /// </summary>
    public static string Write(AppPaths paths, RunResult run, HistoryEntry entry)
    {
        var root = new JsonObject
        {
            ["format"] = Format,
            ["formatVersion"] = FormatVersion,
            ["app"] = "FFX Randomizer",
            ["appVersion"] = entry.AppVersion,
            ["createdAt"] = entry.CreatedAt.ToString("O"),
            ["seed"] = run.Seed.Text,
            ["preset"] = entry.PresetName,
            ["settings"] = new JsonObject
            {
                ["toggles"] = JsonSerializer.SerializeToNode(run.Settings.Toggles.Where(t => t.Value).Select(t => t.Key)),
                ["choices"] = JsonSerializer.SerializeToNode(run.Settings.Choices),
            },
            ["summary"] = JsonSerializer.SerializeToNode(Describe(run.Settings).ToList()),
        };
        foreach (var (section, value) in run.Spoiler) root[section] = value?.DeepClone();

        Directory.CreateDirectory(paths.SpoilerDirectory);
        string safeSeed = string.Concat(run.Seed.Text.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_'));
        string path = Path.Combine(paths.SpoilerDirectory, $"{entry.CreatedAt:yyyyMMdd-HHmmss}-seed-{safeSeed}.json");
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    /// <summary>One line per active feature box, in catalog order, e.g. "Enemy rewards: Item drops, Steals".</summary>
    public static IEnumerable<string> Describe(RandomizerSettings settings)
    {
        foreach (var group in OptionCatalog.AllGroups.Where(settings.IsActive))
        {
            var parts = new List<string>();
            if (group.Choices.Count > 0) parts.Add(group.Choices.First(c => c.Id == settings.ChoiceOf(group)).Label);
            parts.AddRange(group.SubOptions.Where(s => settings.IsOn(s.Id)).Select(s => s.Label));
            yield return parts.Count == 0 ? group.Title : $"{group.Title}: {string.Join(", ", parts)}";
        }
    }
}
