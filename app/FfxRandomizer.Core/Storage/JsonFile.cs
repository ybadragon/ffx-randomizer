using System.Text.Json;

namespace FfxRandomizer.Core.Storage;

internal static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static T Load<T>(string path, Func<T> fallback)
    {
        if (!File.Exists(path)) return fallback();
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? fallback();
    }

    /// <summary>Writes through a temp file so a crash mid-write never leaves a truncated file behind.</summary>
    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, path, overwrite: true);
    }
}
