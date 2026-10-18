using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace FfxRandomizer.Core.Game;

/// <summary>A located copy of FINAL FANTASY X/X-2 HD Remaster.</summary>
public sealed partial record GameInstall(string Directory, string? BuildId)
{
    public const string SteamAppId = "359870";

    private static readonly string[] GameProcessNames = ["FFX", "FFX-2", "FFX&X-2_Will", "FFX&X-2_LAUNCHER"];

    public string DataArchivePath => System.IO.Path.Combine(Directory, "data", "FFX_Data.vbf");
    public string ExecutablePath => System.IO.Path.Combine(Directory, "FFX.exe");

    /// <summary>Finds the Steam copy through every Steam library listed in libraryfolders.vdf.</summary>
    public static GameInstall? FindSteamInstall()
    {
        foreach (string library in SteamLibraries())
        {
            string manifest = System.IO.Path.Combine(library, "steamapps", $"appmanifest_{SteamAppId}.acf");
            if (!File.Exists(manifest)) continue;
            string text = File.ReadAllText(manifest);
            string installDir = AcfValue(text, "installdir");
            if (installDir.Length == 0) continue;
            var install = new GameInstall(System.IO.Path.Combine(library, "steamapps", "common", installDir), AcfValue(text, "buildid"));
            if (File.Exists(install.DataArchivePath)) return install;
        }
        return null;
    }

    /// <summary>Accepts a folder the user picked, if it holds the game's data archive.</summary>
    public static GameInstall? FromFolder(string folder)
    {
        var install = new GameInstall(folder, null);
        return File.Exists(install.DataArchivePath) ? install : null;
    }

    public static bool IsGameRunning()
    {
        foreach (string name in GameProcessNames)
        {
            var processes = Process.GetProcessesByName(name);
            bool running = processes.Length > 0;
            foreach (var p in processes) p.Dispose();
            if (running) return true;
        }
        return false;
    }

    public bool CanWriteArchive()
    {
        try
        {
            using var stream = new FileStream(DataArchivePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static IEnumerable<string> SteamLibraries()
    {
        var roots = new List<string>();
        foreach (var (hive, key, value) in new[]
                 {
                     (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                     (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
                 })
        {
            if (hive.OpenSubKey(key)?.GetValue(value) is string path && path.Length > 0)
                roots.Add(System.IO.Path.GetFullPath(path.Replace('/', '\\')));
        }

        var libraries = new List<string>();
        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            libraries.Add(root);
            string vdf = System.IO.Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (Match m in VdfPath().Matches(File.ReadAllText(vdf)))
                libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        }
        return libraries.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string AcfValue(string text, string key)
    {
        var match = Regex.Match(text, $"\"{key}\"\\s+\"([^\"]*)\"");
        return match.Success ? match.Groups[1].Value : "";
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex VdfPath();
}
