namespace FfxRandomizer.Core.GameData;

/// <summary>Display names for the spoiler log. Missing names fall back to readable IDs.</summary>
public interface IGameNames
{
    string? ItemName(int itemId);
    string? MonsterName(int monsterIndex);
    string? AutoAbilityName(int abilityId);

    /// <summary>Name of Blitzball player 0-59 (0 is Tidus).</summary>
    string? BlitzballPlayerName(int player) => null;
}

public static class GameNamesExtensions
{
    public static string Item(this IGameNames names, int itemId) => names.ItemName(itemId) ?? $"Item 0x{itemId:X4}";

    public static string Monster(this IGameNames names, int index) => names.MonsterName(index) ?? $"Monster m{index:D3}";

    public static string Ability(this IGameNames names, int abilityId) => names.AutoAbilityName(abilityId) ?? $"Ability 0x{abilityId:X4}";

    public static string BlitzballPlayer(this IGameNames names, int player) => names.BlitzballPlayerName(player) ?? $"Player {player}";
}

public sealed class NoGameNames : IGameNames
{
    public static NoGameNames Instance { get; } = new();
    public string? ItemName(int itemId) => null;
    public string? MonsterName(int monsterIndex) => null;
    public string? AutoAbilityName(int abilityId) => null;
}
