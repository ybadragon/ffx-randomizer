using FfxRandomizer.Core.GameData;

namespace FfxRandomizer.Core.Randomization.Features;

/// <summary>
/// Sphere Grid contents for all three grids, following FFXRando's doSphereGridRandomization: grid contents
/// (shuffle / controlled random / true random), then lock removal, then empty or full grid, then node strength.
/// Only the contents files change; a new game copies them into the save.
/// </summary>
public sealed class SphereGridFeature : IRandomizerFeature
{
    public const string Section = "sphereGrid";
    public const string AbilitiesSection = "startingAbilities";

    private static readonly int AbilityCount = SphereNodes.LastAbility - SphereNodes.FirstAbility + 1;

    /// <summary>
    /// Abilities the game's tutorials make a character use. A changed grid may not offer them in time, so these characters
    /// start knowing them (FFXRando's setRequiredAbilities).
    /// </summary>
    public static readonly IReadOnlyList<(int Character, string Name, int[] Commands)> RequiredAbilities =
    [
        (3, "Kimahri", [SphereNodes.LancetCommand]),
        (4, "Wakka", [SphereNodes.DarkAttackCommand]),
        (5, "Lulu", [SphereNodes.ThunderCommand]),
        (6, "Rikku", [SphereNodes.StealCommand, SphereNodes.UseCommand]),
    ];

    public bool IsEnabled(RandomizerSettings s) =>
        Mode(s) != "orig" || Fill(s) != "none" || Strength(s) != "none" || s.IsOn("locks");

    private static string Choice(RandomizerSettings s, string id) => s.ChoiceOf(OptionCatalog.AllGroups.First(g => g.Id == id));
    private static string Mode(RandomizerSettings s) => Choice(s, "gridmode");
    private static string Fill(RandomizerSettings s) => Choice(s, "gridfill");
    private static string Strength(RandomizerSettings s) => Choice(s, "nodes");

    /// <summary>With "Keep things sane", lock nodes stay where they are, so a random lock can't wall a character in at the start.</summary>
    private static bool LocksStay(RandomizerSettings s) => s.IsOn("sane") && Mode(s) != "orig";

    public void Apply(RandomizerContext context)
    {
        var s = context.Settings;
        foreach (var grid in SphereGridFile.All)
        {
            byte[] file = context.Edit(grid.ContentsPath);
            var nodes = file.AsSpan(SphereGridFile.ContentsHeader);
            byte[] before = nodes.ToArray();
            var movable = Enumerable.Range(0, nodes.Length)
                .Where(i => SphereNodes.IsRegular(before[i]) && !(LocksStay(s) && SphereNodes.IsLock(before[i])))
                .ToList();

            var rng = context.RngFor($"grid.contents.{grid.Name}");
            switch (Mode(s))
            {
                case "shuffle":
                    var values = movable.Select(i => before[i]).ToList();
                    rng.Shuffle(values);
                    for (int k = 0; k < movable.Count; k++) nodes[movable[k]] = values[k];
                    break;
                case "controlled":
                    // Every ability exactly once, on distinct nodes; FFXRando could pick the same node twice and lose abilities.
                    var order = movable.ToList();
                    rng.Shuffle(order);
                    for (int k = 0; k < order.Count; k++)
                        nodes[order[k]] = k < AbilityCount ? (byte)(SphereNodes.FirstAbility + k)
                            : LocksStay(s) ? (byte)rng.Next(SphereNodes.Empty, SphereNodes.LastStat)
                            : (byte)rng.Next(0, s.IsOn("locks") ? SphereNodes.LastStat : SphereNodes.Lock4);
                    break;
                case "random":
                    foreach (int i in movable)
                    {
                        byte node;
                        do node = (byte)rng.Next(0, SphereNodes.LastAbility);
                        while (LocksStay(s) && SphereNodes.IsLock(node));
                        nodes[i] = node;
                    }
                    break;
            }

            if (s.IsOn("locks"))
                for (int i = 0; i < nodes.Length; i++)
                    if (SphereNodes.IsLock(nodes[i])) nodes[i] = SphereNodes.Empty;

            var fillRng = context.RngFor($"grid.fill.{grid.Name}");
            for (int i = 0; i < nodes.Length; i++)
            {
                if (Fill(s) == "empty" && (SphereNodes.IsStat(nodes[i]) || SphereNodes.IsLock(nodes[i]))) nodes[i] = SphereNodes.Empty;
                // Stat nodes only, so a full grid really has no empty nodes (FFXRando could roll Empty again).
                else if (Fill(s) == "full" && nodes[i] == SphereNodes.Empty) nodes[i] = (byte)fillRng.Next(SphereNodes.FirstStat, SphereNodes.LastStat);
            }

            for (int i = 0; i < nodes.Length; i++)
                nodes[i] = Strength(s) switch
                {
                    "up" => SphereNodes.Upgrade(nodes[i]),
                    "down" => SphereNodes.Downgrade(nodes[i]),
                    _ => nodes[i],
                };

            Spoil(context, grid, before, nodes.ToArray());
        }

        if (Mode(s) != "orig") GrantRequiredAbilities(context);
    }

    private static void Spoil(RandomizerContext context, SphereGridFile grid, byte[] before, byte[] after)
    {
        object Counts(byte[] nodes) => new
        {
            Locks = nodes.Count(SphereNodes.IsLock),
            Empty = nodes.Count(n => n == SphereNodes.Empty),
            Stats = nodes.Count(SphereNodes.IsStat),
            Abilities = nodes.Count(SphereNodes.IsAbility),
        };
        var abilities = Mode(context.Settings) == "orig" ? null : Enumerable.Range(0, after.Length)
            .Where(i => SphereNodes.IsAbility(after[i]))
            .OrderBy(i => after[i]).ThenBy(i => i)
            .Select(i => new { Ability = SphereNodes.Name(after[i]), Node = i, Replaces = SphereNodes.Name(before[i]) })
            .ToList();
        context.Spoil(Section, new
        {
            Grid = grid.Name,
            File = Path.GetFileName(grid.ContentsPath),
            Nodes = after.Length,
            Changed = Enumerable.Range(0, after.Length).Count(i => after[i] != before[i]),
            Before = Counts(before),
            After = Counts(after),
            Abilities = abilities,
        });
    }

    private static void GrantRequiredAbilities(RandomizerContext context)
    {
        foreach (string language in GamePaths.LanguageFolders)
        {
            var table = new KernelTable(context.Edit(GamePaths.LanguageKernel(language, PlySave.FileName)));
            foreach (var (character, _, commands) in RequiredAbilities)
                foreach (int command in commands) SphereNodes.Learn(table.Record(character), command);
        }
        foreach (var (_, name, commands) in RequiredAbilities)
            context.Spoil(AbilitiesSection, new { Character = name, Abilities = commands.Select(CommandName).ToList() });
    }

    private static string CommandName(int command) => command switch
    {
        SphereNodes.LancetCommand => "Lancet",
        SphereNodes.DarkAttackCommand => "Dark Attack",
        SphereNodes.ThunderCommand => "Thunder",
        SphereNodes.StealCommand => "Steal",
        SphereNodes.UseCommand => "Use",
        _ => $"Command 0x{command:X4}",
    };

    public IEnumerable<string> Validate(RandomizerContext context)
    {
        var s = context.Settings;
        foreach (var grid in SphereGridFile.All)
        {
            byte[] originalFile = context.Original.ReadFile(grid.ContentsPath);
            byte[] file = context.ChangedFiles[grid.ContentsPath];
            int count = SphereGridFile.NodeCount(context.Original.ReadFile(grid.LayoutPath));
            if (file.Length != originalFile.Length || file.Length != SphereGridFile.ContentsHeader + count)
            {
                yield return $"{grid.Name} grid: contents file is {file.Length} bytes for {count} nodes.";
                continue;
            }
            if (!file.AsSpan(0, SphereGridFile.ContentsHeader).SequenceEqual(originalFile.AsSpan(0, SphereGridFile.ContentsHeader)))
                yield return $"{grid.Name} grid: header changed.";

            var before = originalFile.AsSpan(SphereGridFile.ContentsHeader).ToArray();
            var after = file.AsSpan(SphereGridFile.ContentsHeader).ToArray();
            for (int i = 0; i < count; i++)
            {
                string where = $"{grid.Name} grid node {i}";
                if (!SphereNodes.IsRegular(before[i]) && after[i] != before[i]) yield return $"{where}: special node 0x{before[i]:X2} was changed.";
                else if (SphereNodes.IsRegular(before[i]) && !SphereNodes.IsRegular(after[i])) yield return $"{where}: invalid node type 0x{after[i]:X2}.";
                if (s.IsOn("locks") && SphereNodes.IsLock(after[i])) yield return $"{where}: still has a lock.";
                if (Fill(s) == "empty" && (SphereNodes.IsStat(after[i]) || SphereNodes.IsLock(after[i]))) yield return $"{where}: not emptied.";
                if (Fill(s) == "full" && after[i] == SphereNodes.Empty) yield return $"{where}: still empty.";
                if (Strength(s) == "up" && SphereNodes.Upgrade(after[i]) != after[i]) yield return $"{where}: {SphereNodes.Name(after[i])} wasn't upgraded.";
                if (Strength(s) == "down" && SphereNodes.Downgrade(after[i]) != after[i]) yield return $"{where}: {SphereNodes.Name(after[i])} wasn't downgraded.";
                if (LocksStay(s) && SphereNodes.IsLock(before[i]) != SphereNodes.IsLock(after[i]) && !s.IsOn("locks") && Fill(s) != "empty")
                    yield return $"{where}: a lock moved although Keep things sane is on.";
                if (Mode(s) == "orig" && SphereNodes.IsAbility(before[i]) && after[i] != before[i]) yield return $"{where}: ability changed.";
            }

            if (Mode(s) is "shuffle" or "orig")
            {
                // Later steps never touch abilities, so the shuffled grid keeps exactly the original ability nodes.
                var mine = after.Where(SphereNodes.IsAbility).Order();
                var theirs = before.Where(SphereNodes.IsAbility).Order();
                if (!mine.SequenceEqual(theirs)) yield return $"{grid.Name} grid: ability nodes differ from the original set.";
                if (Mode(s) == "shuffle" && !s.IsOn("locks") && Fill(s) == "none" && Strength(s) == "none"
                    && !after.Order().SequenceEqual(before.Order()))
                    yield return $"{grid.Name} grid: shuffled nodes aren't the original nodes.";
            }
            if (Mode(s) == "controlled")
                for (int ability = SphereNodes.FirstAbility; ability <= SphereNodes.LastAbility; ability++)
                {
                    int n = after.Count(b => b == ability);
                    if (n != 1) yield return $"{grid.Name} grid: {SphereNodes.Name((byte)ability)} appears {n} times.";
                }
        }

        if (Mode(s) != "orig")
            foreach (string language in GamePaths.LanguageFolders)
            {
                var table = new KernelTable(context.ChangedFiles[GamePaths.LanguageKernel(language, PlySave.FileName)]);
                foreach (var (character, name, commands) in RequiredAbilities)
                    foreach (int command in commands)
                        if (!SphereNodes.Knows(table.Record(character), command))
                            yield return $"{name} doesn't start with {CommandName(command)} in {language}.";
            }
    }
}
