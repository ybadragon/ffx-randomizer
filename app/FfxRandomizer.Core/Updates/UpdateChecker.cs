using System.Net.Http.Headers;
using System.Text.Json;

namespace FfxRandomizer.Core.Updates;

public sealed record UpdateInfo(string Version, string Name, string PageUrl, bool IsPreRelease);

/// <summary>Asks GitHub whether a newer release exists. Pre-releases count, since every release so far is one.</summary>
public static class UpdateChecker
{
    public const string Repository = "ybadragon/ffx-randomizer";
    public static string ReleasesPage => $"https://github.com/{Repository}/releases";

    /// <summary>The newest release above <paramref name="currentVersion"/>, or null when up to date or GitHub can't be reached.</summary>
    public static async Task<UpdateInfo?> CheckAsync(string currentVersion, HttpMessageHandler? handler = null, CancellationToken cancel = default)
    {
        try
        {
            using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FFX-Randomizer", currentVersion));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            string json = await client.GetStringAsync($"https://api.github.com/repos/{Repository}/releases?per_page=20", cancel);
            return Newest(json, currentVersion);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Picks the highest published release in a GitHub releases listing that is newer than <paramref name="currentVersion"/>.</summary>
    public static UpdateInfo? Newest(string releasesJson, string currentVersion)
    {
        if (!TryParse(currentVersion, out var current)) return null;
        UpdateInfo? best = null;
        Version? bestVersion = null;
        using var doc = JsonDocument.Parse(releasesJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            string? tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            string? url = release.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            if (tag is null || url is null || !TryParse(tag, out var version)) continue;
            if (version <= current || (bestVersion is not null && version <= bestVersion)) continue;
            string name = release.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } s ? s : tag;
            bool pre = release.TryGetProperty("prerelease", out var p) && p.GetBoolean();
            best = new UpdateInfo(Normalize(version), name, url, pre);
            bestVersion = version;
        }
        return best;
    }

    /// <summary>Reads "v1.2.3", "1.2" or "1.2.3-beta" as a version; anything after '-' or '+' is ignored.</summary>
    internal static bool TryParse(string text, out Version version)
    {
        string core = text.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        if (Version.TryParse(core, out var parsed))
        {
            version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
            return true;
        }
        version = new Version();
        return false;
    }

    private static string Normalize(Version v) => $"{v.Major}.{v.Minor}.{v.Build}";
}
