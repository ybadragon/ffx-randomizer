using System.Text.Json.Nodes;
using FfxRandomizer.Core.GameData;

namespace FfxRandomizer.Core.Randomization.Features;

/// <summary>
/// Shuffles which enemies appear in random-encounter battles, following FFXRando's RandomEncounterRandomizer: every
/// eligible enemy is paired with another, and each battle file has that enemy replaced by its partner everywhere.
/// Optionally the newcomer takes on (or is scaled toward) the stats of the enemy it replaces.
/// </summary>
public sealed class EnemyEncountersFeature : IRandomizerFeature
{
    public const string Section = "encounters";
    public const string Original = "orig", Shuffle = "random", Swap = "swap", Scale = "scale";

    // FFXRando never moves these (MON_BELIAL_01 and the sandworms).
    private static readonly HashSet<int> NeverMoved = [188, 46, 47, 330, 301];

    // FFXRando's boss_id_whitelist: MON_OCHU_E01, MON_SINSPAWN_02, MON_GOLEM_03.
    private static readonly HashSet<int> BossWhitelist = [153, 103, 229];

    // FFXRando's enemy_id_whitelist: enemies with no Monster Arena slot it still treats as ordinary fiends
    // (mechs, golems, Guado, Sahagins, piranhas, the battle treasure chest...).
    private static readonly HashSet<int> EnemyWhitelist =
    [
        91, 92, 93, 94, 96, 97, 48, 49, 195, 196, 225, 226, 227, 228, 222, 43, 241, 245, 246, 247, 42, 51, 53, 58,
        68, 69, 70, 71, 72, 73, 74, 75, 76, 211,
    ];

    /// <summary>The battle treasure chest. It can be moved, but never takes on another enemy's stats.</summary>
    public const int BattleTreasureChest = 211;

    public bool IsEnabled(RandomizerSettings s) => Mode(s) != Original;

    public static string Mode(RandomizerSettings s) => s.ChoiceOf(OptionCatalog.AllGroups.First(g => g.Id == "encounters"));

    public void Apply(RandomizerContext context)
    {
        var rng = context.RngFor("enemies.encounters");
        var battles = EncounterFiles.RandomEncounterBattles(context.Original);
        var pool = EligibleEnemies(context, battles);
        var partners = pool.ToList();
        rng.Shuffle(partners);
        var replacement = pool.Zip(partners).ToDictionary(p => p.First, p => p.Second);

        foreach (string battle in battles)
        {
            string path = EncounterFiles.PathFor(battle);
            var monsters = EncounterFiles.Monsters(context.Original.ReadFile(path));
            if (!monsters.Any(m => replacement.TryGetValue(m, out int to) && to != m)) continue;
            byte[] bytes = context.Edit(path);
            for (int slot = 0; slot < monsters.Length; slot++)
                if (replacement.TryGetValue(monsters[slot], out int to)) EncounterFiles.SetMonster(bytes, slot, to);
        }

        string mode = Mode(context.Settings);
        foreach (var (from, to) in replacement)
        {
            if (from == to) continue;
            context.Spoil(Section, new
            {
                Replaced = context.Names.Monster(from), ReplacedIndex = from,
                By = context.Names.Monster(to), ByIndex = to,
            });
            // FFXRando skips stat changes for the chest; we also keep its 1 HP from being handed to a real enemy.
            if (mode is Swap or Scale && to != BattleTreasureChest && from != BattleTreasureChest) AdjustStats(context, from, to, mode == Scale);
        }
    }

    /// <summary>Enemies met in random encounters that may be moved, in the order FFXRando collects them.</summary>
    internal static List<int> EligibleEnemies(RandomizerContext context, IReadOnlyList<string> battles)
    {
        var pool = new List<int>();
        foreach (string battle in battles)
        {
            foreach (int monster in EncounterFiles.Monsters(context.Original.ReadFile(EncounterFiles.PathFor(battle))))
            {
                if (monster < 0 || NeverMoved.Contains(monster) || pool.Contains(monster)) continue;
                var file = new MonsterFile(monster, context.Original.ReadFile(MonsterFile.PathFor(monster)));
                if (!file.HasStats) continue;
                // 1-HP placeholders (the battle treasure chest, Gemini's spawner) are the likeliest cause of the reported
                // Thunder Plains crash, so with "sane" on they stay in their own battles.
                if (context.Settings.IsOn("sane") && file.Stats.Hp <= 1) continue;
                int arena = file.Stats.ArenaId;
                bool capturable = arena != 255 && (arena != 0 || monster == 1);
                if (capturable || BossWhitelist.Contains(monster) || EnemyWhitelist.Contains(monster)) pool.Add(monster);
            }
        }
        return pool;
    }

    /// <summary>
    /// Gives <paramref name="to"/> the stats of the enemy it replaces (swap), or stats between its own and those (scale),
    /// so an early area stays early-game tough. Gil and AP follow too, unless enemy drops are randomized separately.
    /// </summary>
    private static void AdjustStats(RandomizerContext context, int from, int to, bool scale)
    {
        var old = new MonsterFile(from, context.Original.ReadFile(MonsterFile.PathFor(from)));
        var mine = new MonsterFile(to, context.Original.ReadFile(MonsterFile.PathFor(to)));
        // Placeholder and controller enemies (1 HP, like Gemini's spawner) keep their stats, and never hand them on.
        if (!old.HasStats || !mine.HasStats || old.Stats.Hp <= 1 || mine.Stats.Hp <= 1) return;
        var target = new MonsterFile(to, context.Edit(MonsterFile.PathFor(to)));
        MonsterStats a = old.Stats, b = mine.Stats, s = target.Stats;

        if (scale)
        {
            s.Hp = Between(a.Hp, b.Hp, Math.Ceiling(a.Hp * Math.Pow(Math.Pow(b.Hp, 1 / 3.2) / Math.Pow(a.Hp, 1 / 3.2), 1 / 3.2) / 25) * 25);
            s.Mp = Between(a.Mp, b.Mp, Math.Ceiling(a.Mp * Math.Sqrt(Math.Sqrt(b.Mp) / Math.Sqrt(a.Mp)) / 5) * 5);
            s.Strength = Between(a.Strength, b.Strength);
            s.Magic = Between(a.Magic, b.Magic);
            s.Agility = Between(a.Agility, b.Agility);
            s.Accuracy = Between(a.Accuracy, b.Accuracy);
            s.Luck = Between(a.Luck, b.Luck);
        }
        else
        {
            s.Hp = a.Hp; s.Mp = a.Mp;
            s.Strength = a.Strength; s.Defense = a.Defense; s.Magic = a.Magic; s.MagicDefense = a.MagicDefense;
            s.Agility = a.Agility; s.Accuracy = a.Accuracy; s.Evasion = a.Evasion; s.Luck = a.Luck;
        }

        // With enemy drops on, gil and AP are re-rolled around each enemy's own values instead.
        if (!context.Settings.IsOn("rewards") || !context.Settings.IsOn("drops"))
        {
            if (old.HasLoot && mine.HasLoot && target.HasLoot)
            {
                MonsterLoot la = old.Loot, lb = mine.Loot, lt = target.Loot;
                lt.Ap = scale ? (ushort)Between(la.Ap, lb.Ap, Rewards(la.Ap, lb.Ap)) : la.Ap;
                lt.ApOverkill = scale ? (ushort)Between(la.ApOverkill, lb.ApOverkill, Rewards(la.ApOverkill, lb.ApOverkill)) : la.ApOverkill;
                lt.Gil = scale ? (ushort)Between(la.Gil, lb.Gil, Rewards(la.Gil, lb.Gil)) : la.Gil;
            }
        }
        StatChanges.Record(context, to);
    }

    private static double Rewards(double old, double now) => Math.Ceiling(old * Math.Pow(Math.Pow(now, 1 / 3.2) / Math.Pow(old, 1 / 3.2), 1 / 3.2));

    /// <summary>FFXRando's scaling for the 8-bit stats: the geometric mean of the two, rounded up.</summary>
    private static byte Between(byte old, byte now) => (byte)Between(old, (uint)now, Math.Ceiling(old * Math.Sqrt(now) / Math.Sqrt(old)));

    /// <summary>
    /// Clamps a scaled value between the two enemies' values. Where either is 0 the formula has no answer
    /// (FFXRando divides by zero), so the newcomer keeps its own value.
    /// </summary>
    private static uint Between(uint old, uint now, double scaled) =>
        old == 0 || now == 0 || double.IsNaN(scaled) ? now : (uint)Math.Clamp(scaled, Math.Min(old, now), Math.Max(old, now));

    public IEnumerable<string> Validate(RandomizerContext context)
    {
        var replacement = new Dictionary<int, int>();
        if (context.Spoiler[Section] is JsonArray pairs)
        {
            foreach (var pair in pairs)
            {
                int from = (int)pair!["replacedIndex"]!, to = (int)pair["byIndex"]!;
                if (!replacement.TryAdd(from, to)) yield return $"m{from:D3} is replaced twice in the encounter spoiler.";
            }
        }
        if (replacement.Values.Distinct().Count() != replacement.Count)
            yield return "Two enemies were replaced by the same enemy; encounters must be a one-to-one shuffle.";

        foreach (string battle in EncounterFiles.RandomEncounterBattles(context.Original))
        {
            string path = EncounterFiles.PathFor(battle);
            if (!context.ChangedFiles.TryGetValue(path, out byte[]? bytes)) bytes = context.Original.ReadFile(path);
            byte[] original = context.Original.ReadFile(path);
            if (bytes.Length != original.Length)
            {
                yield return $"{battle}: file size changed.";
                continue;
            }
            int monstersAt = EncounterFiles.FormationOffset(original) + EncounterFiles.FirstMonsterOffset;
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] != original[i] && (i < monstersAt || i >= monstersAt + EncounterFiles.MonsterSlots * 2))
                {
                    yield return $"{battle}: byte 0x{i:X} changed outside the formation's monster list.";
                    break;
                }
            }
            int[] before = EncounterFiles.Monsters(original), after = EncounterFiles.Monsters(bytes);
            for (int slot = 0; slot < before.Length; slot++)
            {
                int expected = before[slot] < 0 ? -1 : replacement.GetValueOrDefault(before[slot], before[slot]);
                if (after[slot] != expected)
                    yield return $"{battle}: slot {slot} holds m{after[slot]:D3}, expected m{expected:D3}.";
                else if (after[slot] >= 0 && context.Original.IndexOf(MonsterFile.PathFor(after[slot])) < 0)
                    yield return $"{battle}: slot {slot} holds m{after[slot]:D3}, which has no monster file.";
            }
        }
        foreach (string problem in StatChanges.Validate(context)) yield return problem;
        foreach (string problem in EnemyChecks.Integrity(context)) yield return problem;
        foreach (string problem in EnemyChecks.SpoilerMatchesFiles(context)) yield return problem;
    }
}
