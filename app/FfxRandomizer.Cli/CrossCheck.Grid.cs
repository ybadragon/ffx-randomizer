using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FfxRandomizer.Core.GameData;

internal static partial class CrossCheck
{
    /// <summary>
    /// Reads every grid back with READ_SPHERE_GRID_LAYOUT and compares each node's type with what we wrote, and the
    /// spoiler's ability list with the nodes the parser sees. READ_SPHERE_GRID_NODE_TYPES checks our node names.
    /// </summary>
    /// <param name="written">The files the randomizer produced.</param>
    /// <param name="files">The files handed to the parser (differs from <paramref name="written"/> only when sabotaged).</param>
    private static IEnumerable<string> CompareSphereGrids(JsonArray entries, IReadOnlyDictionary<string, byte[]> written, IReadOnlyDictionary<string, byte[]> files)
    {
        var problems = new List<string>();
        var (typesOut, typeErrors) = RunParser(files, "READ_SPHERE_GRID_NODE_TYPES");
        problems.AddRange(typeErrors.Select(e => $"parser error: {e}"));
        string panel = typesOut.Replace("\r", "");
        panel = panel[Math.Max(0, panel.IndexOf("panel.bin ---", StringComparison.Ordinal))..];
        var types = PanelLine().Matches(panel).ToDictionary(m => int.Parse(m.Groups[1].Value), m => m.Groups[2].Value.Trim());
        for (int node = 0; node < SphereNodes.Names.Count; node++)
            if (types.GetValueOrDefault(node) != SphereNodes.Names[node])
                problems.Add($"node type {node:X2}h: we call it \"{SphereNodes.Names[node]}\" but panel.bin says \"{types.GetValueOrDefault(node)}\"");

        foreach (var entry in entries)
        {
            string name = (string)entry!["grid"]!;
            var grid = SphereGridFile.All.First(g => g.Name == name);
            var (stdout, errors) = RunParser(files, "READ_SPHERE_GRID_LAYOUT", grid.LayoutPath, grid.ContentsPath);
            problems.AddRange(errors.Select(e => $"parser error: {e}"));
            var labels = GridNodeLine().Matches(stdout.Replace("\r", "")).ToDictionary(m => int.Parse(m.Groups[1].Value), m => m.Groups[2].Value);
            byte[] nodes = written[grid.ContentsPath].AsSpan(SphereGridFile.ContentsHeader).ToArray();
            if (labels.Count != nodes.Length) problems.Add($"{name} grid: FFXDataParser read {labels.Count} nodes, we wrote {nodes.Length}");

            for (int i = 0; i < nodes.Length; i++)
            {
                if (!labels.TryGetValue(i, out string? label)) continue;
                // The parser shows a null node's layout-file type in brackets instead of its contents.
                bool ok = nodes[i] == 0xFF ? label.StartsWith('[')
                    : label == $"{(SphereNodes.IsRegular(nodes[i]) ? SphereNodes.Name(nodes[i]) : "null")} [{nodes[i]:X2}h]";
                if (!ok) problems.Add($"{name} grid node {i}: we wrote {SphereNodes.Name(nodes[i])} [{nodes[i]:X2}h] but FFXDataParser reads \"{label}\"");
            }

            if (entry["abilities"] is JsonArray abilities)
                foreach (var ability in abilities)
                {
                    int node = (int)ability!["node"]!;
                    string spoiled = (string)ability["ability"]!;
                    if (!labels.TryGetValue(node, out string? label) || !label.StartsWith(spoiled + " [", StringComparison.Ordinal))
                        problems.Add($"{name} grid: our spoiler puts {spoiled} on node {node} but FFXDataParser reads \"{label}\"");
                }
        }
        return problems;
    }

    [GeneratedRegex(@"^N#(\d+): (.*?) \{ Position", RegexOptions.Multiline)] private static partial Regex GridNodeLine();
    [GeneratedRegex(@"^Index (\d+) \[[0-9A-F]+h\] \(Offset [0-9A-F]+\) - (.*?)\s+\{", RegexOptions.Multiline)] private static partial Regex PanelLine();
}
