using FfxRandomizer.Core.GameData.Text;
using FfxRandomizer.Core.Vbf;

namespace FfxRandomizer.Core.GameData;

/// <summary>
/// In-game names of items, key items, gear auto-abilities and monsters, decoded from one language's battle kernel
/// (ffx_ps2/ffx/master/new_XXpc/battle/kernel/):
/// <list type="bullet">
/// <item>item.bin: items 0x2000 + row</item>
/// <item>important.bin: key items 0xA000 + row</item>
/// <item>a_ability.bin: auto-abilities 0x8000 + row</item>
/// <item>monster1.bin, monster2.bin, monster3.bin: monsters, each file's header holding the first monster index it covers
/// (the same index as the jppc/battle/mon/_mNNN folder)</item>
/// <item>ply_save.bin: party names, substituted where a name embeds a character-name code (the aeon monsters)</item>
/// </list>
/// </summary>
public sealed class GameText : IGameNames
{
    public const int ItemBase = 0x2000;
    public const int KeyItemBase = 0xA000;
    public const int AutoAbilityBase = 0x8000;

    /// <summary>Default names for party slots the game stores as "????" until the player names them.</summary>
    private static readonly string[] DefaultPartyNames =
    [
        "Tidus", "Yuna", "Auron", "Kimahri", "Wakka", "Lulu", "Rikku", "Seymour",
        "Valefor", "Ifrit", "Ixion", "Shiva", "Bahamut", "Anima", "Yojimbo", "Cindy", "Sandy", "Mindy",
    ];

    private readonly string?[] _items;
    private readonly string?[] _keyItems;
    private readonly string?[] _autoAbilities;
    private readonly Dictionary<int, string> _monsters;

    private GameText(string?[] items, string?[] keyItems, string?[] autoAbilities, Dictionary<int, string> monsters)
    {
        _items = items;
        _keyItems = keyItems;
        _autoAbilities = autoAbilities;
        _monsters = monsters;
    }

    public string Language { get; private init; } = "us";

    /// <summary>command.bin: battle commands and abilities 0x3000 + row (what aeons learn, what sphere grid nodes teach).</summary>
    private string?[] Commands { get; init; } = [];

    /// <summary>
    /// Loads every name for <paramref name="language"/>, the two-letter code of a new_XXpc folder
    /// ("us", "de", "fr", "it", "sp", "jp", "ch", "kr").
    /// </summary>
    public static GameText Load(VbfArchive original, string language = "us")
    {
        ArgumentNullException.ThrowIfNull(original);
        string folder = $"new_{language}pc";
        if (!GamePaths.LanguageFolders.Contains(folder))
            throw new ArgumentException($"Unknown language '{language}'.", nameof(language));

        var charset = FfxCharset.FromTableFile(original.ReadFile(FfxCharset.TablePath(language)));
        LocalizedTable Table(string file) => new(original.ReadFile(GamePaths.LanguageKernel(folder, file)));

        string?[] party = Table("ply_save.bin").Names(charset);
        string? PartyName(int slot)
        {
            string? name = (uint)slot < (uint)party.Length ? party[slot] : null;
            bool placeholder = name is null || name.All(c => c == '?');
            if (!placeholder) return name;
            return (uint)slot < (uint)DefaultPartyNames.Length ? DefaultPartyNames[slot] : null;
        }

        var monsters = new Dictionary<int, string>();
        for (int fileNumber = 1; ; fileNumber++)
        {
            string path = GamePaths.LanguageKernel(folder, $"monster{fileNumber}.bin");
            if (original.IndexOf(path) < 0) break;
            var table = new LocalizedTable(original.ReadFile(path));
            for (int row = 0; row < table.Count; row++)
            {
                string? name = table.Name(row, charset, PartyName);
                if (name is not null) monsters[table.FirstIndex + row] = name;
            }
        }

        return new GameText(
            Table("item.bin").Names(charset),
            Table("important.bin").Names(charset),
            Table("a_ability.bin").Names(charset),
            monsters)
        {
            Language = language,
            Commands = Table("command.bin").Names(charset),
            _blitzballPlayers = MacroStrings(original, folder, charset, PartyName, 7, BlitzballStats.PlayerCount),
            _areas = MacroStrings(original, folder, charset, PartyName, 11, int.MaxValue),
        };
    }

    public const int CommandBase = 0x3000;

    /// <summary>Name of a battle command or ability (0x3000 + row of command.bin).</summary>
    public string? CommandName(int commandId) => (commandId & 0xF000) == CommandBase ? At(Commands, commandId & 0x0FFF) : null;

    private string?[] _blitzballPlayers = [];
    private string?[] _areas = [];

    /// <summary>
    /// Macro strings (new_XXpc/menu/macrodic.dcp): a table of u32 chunk offsets at the start of the file, and in each chunk
    /// a list of (u16 name offset, u16 simplified-name offset) pairs, whose length the first offset gives, followed by the
    /// strings. Chunk 7 holds the Blitzball player names (0x700 + player), chunk 11 the area names (0xB00 + index).
    /// </summary>
    private static string?[] MacroStrings(VbfArchive original, string folder, FfxCharset charset, Func<int, string?> partyName, int chunkIndex, int max)
    {
        string path = $"{GamePaths.Master}{folder}/menu/macrodic.dcp";
        if (original.IndexOf(path) < 0) return [];
        byte[] bytes = original.ReadFile(path);
        uint U32(int offset) => BitConverter.ToUInt32(bytes, offset);
        int start = (int)U32(chunkIndex * 4);
        if (start == 0 || start >= bytes.Length) return [];
        int end = bytes.Length;
        for (int next = chunkIndex + 1; next * 4 + 4 <= start; next++)
        {
            uint offset = U32(next * 4);
            if (offset == uint.MaxValue) break;
            if (offset >= start) { end = (int)offset; break; }
        }
        var chunk = bytes.AsSpan(start, end - start);
        int count = Math.Min(BitConverter.ToUInt16(chunk) / 4, max);
        var names = new string?[count];
        for (int i = 0; i < count; i++) names[i] = charset.Decode(chunk, BitConverter.ToUInt16(chunk[(i * 4)..]), partyName);
        return names;
    }

    public string? BlitzballPlayerName(int player) => At(_blitzballPlayers, player);

    /// <summary>An area's name, by the index an event lists (see <see cref="EventLocation.AreaNameIndexes"/>).</summary>
    public string? AreaName(int index) => At(_areas, index);

    /// <summary>Number of rows in item.bin (items 0x2000 to 0x2000 + ItemCount - 1).</summary>
    public int ItemCount => _items.Length;

    /// <summary>Number of rows in important.bin (key items 0xA000 to 0xA000 + KeyItemCount - 1).</summary>
    public int KeyItemCount => _keyItems.Length;

    /// <summary>Number of rows in a_ability.bin (auto-abilities 0x8000 to 0x8000 + AutoAbilityCount - 1).</summary>
    public int AutoAbilityCount => _autoAbilities.Length;

    /// <summary>Name of an item as stored in monster loot and treasure: 0x2000 + n for items, 0xA000 + n for key items.</summary>
    public string? ItemName(int itemId) => (itemId & 0xF000) switch
    {
        ItemBase => At(_items, itemId & 0x0FFF),
        KeyItemBase => At(_keyItems, itemId & 0x0FFF),
        _ => null,
    };

    /// <summary>Name of the monster in folder _mNNN (NNN = <paramref name="monsterIndex"/>).</summary>
    public string? MonsterName(int monsterIndex) => _monsters.GetValueOrDefault(monsterIndex);

    /// <summary>
    /// Name of a gear auto-ability as stored in gear-drop slots (0x8000 + index). Anything else, including 0x00FF
    /// (the empty-slot marker), has no name.
    /// </summary>
    public string? AutoAbilityName(int abilityId) => (abilityId & 0xF000) == AutoAbilityBase
        ? At(_autoAbilities, abilityId & 0x0FFF)
        : null;

    private static string? At(string?[] names, int index) => (uint)index < (uint)names.Length ? names[index] : null;
}
