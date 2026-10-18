using System.Text.Json.Nodes;
using FfxRandomizer.Core.GameData;

namespace FfxRandomizer.Core.Randomization.Features;

/// <summary>Spoiler bookkeeping and checks for features that change enemy stats (encounter stat swaps, enemy stats).</summary>
internal static class StatChanges
{
    private static readonly (string Name, Func<MonsterStats, long> Read)[] Stats =
    [
        ("hp", s => s.Hp), ("mp", s => s.Mp), ("overkill", s => s.OverkillThreshold),
        ("strength", s => s.Strength), ("defense", s => s.Defense), ("magic", s => s.Magic), ("magicDefense", s => s.MagicDefense),
        ("agility", s => s.Agility), ("luck", s => s.Luck), ("evasion", s => s.Evasion), ("accuracy", s => s.Accuracy),
        ("armored", s => s.Armored ? 1 : 0),
    ];

    private static readonly (string Name, Func<MonsterLoot, long> Read)[] Rewards =
        [("gil", l => l.Gil), ("ap", l => l.Ap), ("apOverkill", l => l.ApOverkill)];

    /// <summary>
    /// Writes the enemy's spoiler "stats" (every stat that now differs from the original, from → to) and brings any
    /// gil/AP entries up to date. Call after each change; the last call leaves the final values.
    /// </summary>
    public static void Record(RandomizerContext context, int index)
    {
        string path = MonsterFile.PathFor(index);
        if (!context.ChangedFiles.TryGetValue(path, out byte[]? bytes)) return;
        var original = new MonsterFile(index, context.Original.ReadFile(path));
        var now = new MonsterFile(index, bytes);
        var entry = EnemyRewardsFeature.EnemyEntry(context, index);

        if (original.HasStats)
        {
            var stats = new JsonObject();
            foreach (var (name, read) in Stats)
            {
                long from = read(original.Stats), to = read(now.Stats);
                if (from == to) continue;
                stats[name] = name == "armored" ? RandomizerContext.ToNode(new { From = from == 1, To = to == 1 }) : RandomizerContext.ToNode(new { From = from, To = to });
            }
            if (stats.Count > 0) entry["stats"] = stats;
            else entry.Remove("stats");
        }
        if (original.HasLoot)
        {
            foreach (var (name, read) in Rewards)
            {
                long from = read(original.Loot), to = read(now.Loot);
                if (from != to || entry[name] is not null) entry[name] = RandomizerContext.ToNode(new { From = from, To = to });
            }
        }
    }

    /// <summary>Stat changes stay inside the stat bytes, Armored is the only flag touched, and the spoiler matches the files.</summary>
    public static IEnumerable<string> Validate(RandomizerContext context)
    {
        foreach (int index in MonsterFile.AllIndices(context.Original))
        {
            string path = MonsterFile.PathFor(index);
            if (!context.ChangedFiles.TryGetValue(path, out byte[]? bytes)) continue;
            var original = new MonsterFile(index, context.Original.ReadFile(path));
            var now = new MonsterFile(index, bytes);
            if (!original.HasStats) continue;
            int flags = original.StatsOffset + MonsterStats.FlagsOffset;
            if ((bytes[flags] & ~MonsterStats.ArmoredFlag) != (original.Bytes[flags] & ~MonsterStats.ArmoredFlag))
                yield return $"m{index:D3}: a monster flag other than Armored changed.";
            if (original.Stats.Hp > 1 && now.Stats.Hp == 0)
                yield return $"m{index:D3}: HP is 0.";
            if (original.Stats.Hp <= 1 && now.Stats.Hp != original.Stats.Hp)
                yield return $"m{index:D3}: placeholder enemy (HP {original.Stats.Hp}) had its HP changed.";

            if (context.Spoiler[EnemyRewardsFeature.Section] is not JsonArray entries) continue;
            var entry = entries.OfType<JsonObject>().FirstOrDefault(e => (int)e["index"]! == index);
            var shown = entry?["stats"] as JsonObject;
            foreach (var (name, read) in Stats)
            {
                long from = read(original.Stats), to = read(now.Stats);
                if (from == to)
                {
                    if (shown?[name] is not null) yield return $"m{index:D3}: spoiler lists a {name} change the file doesn't have.";
                }
                else if (shown?[name] is not JsonObject change)
                    yield return $"m{index:D3}: {name} changed but the spoiler doesn't say so.";
                else if ((name == "armored" ? ((bool)change["to"]! ? 1 : 0) : (long)change["to"]!) != to)
                    yield return $"m{index:D3}: spoiler {name} doesn't match the file.";
            }
            if (original.HasLoot && entry is not null)
                foreach (var (name, read) in Rewards)
                    if (entry[name] is JsonObject change && (long)change["to"]! != read(now.Loot))
                        yield return $"m{index:D3}: spoiler {name} doesn't match the file.";
        }
    }

    /// <summary>The working copy if a feature has changed the file, otherwise the original.</summary>
    public static MonsterFile Current(RandomizerContext context, int index)
    {
        string path = MonsterFile.PathFor(index);
        return new MonsterFile(index, context.ChangedFiles.TryGetValue(path, out byte[]? bytes) ? bytes : context.Original.ReadFile(path));
    }
}
