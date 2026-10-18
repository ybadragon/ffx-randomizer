using System.Text.Json;
using FfxRandomizer.Core.Randomization;
using FfxRandomizer.Core.Storage;

namespace FfxRandomizer.App;

/// <summary>Window state kept between launches: the game folder if picked by hand, and the last settings.</summary>
public sealed class AppSettings
{
    public string? GameFolder { get; set; }
    public RandomizerSettings? LastSettings { get; set; }
    public Guid? LastPresetId { get; set; }
    public RandomizerSettings? LastPresetBaseline { get; set; }

    private static string PathFor(AppPaths paths) => Path.Combine(paths.Root, "app-settings.json");

    public static AppSettings Load(AppPaths paths)
    {
        try
        {
            string path = PathFor(paths);
            return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public void Save(AppPaths paths)
    {
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(PathFor(paths), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
