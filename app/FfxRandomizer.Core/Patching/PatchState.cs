using System.Text.Json;
using System.Text.Json.Serialization;

namespace FfxRandomizer.Core.Patching;

/// <summary>Contents of state.json. Property names match what FFX-Randomizer.bat reads and writes.</summary>
public sealed class PatchState
{
    public string? OriginalFingerprint { get; set; }
    public long OriginalLength { get; set; }
    public string? InputFingerprint { get; set; }
    public bool Patched { get; set; }
    public string? PatchedFingerprint { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static PatchState? Load(string path)
    {
        if (!File.Exists(path)) return null;
        // File.ReadAllText strips the UTF-8 BOM that Windows PowerShell writes.
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<PatchState>(json, Options);
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
    }
}
