namespace FfxRandomizer.Core.GameData;

/// <summary>Display names for the spoiler log. Missing names fall back to readable IDs.</summary>
public interface IGameNames
{
    string? ItemName(int itemId);
    string? MonsterName(int monsterIndex);
}

public static class GameNamesExtensions
{
    public static string Item(this IGameNames names, int itemId) => names.ItemName(itemId) ?? $"Item 0x{itemId:X4}";

    public static string Monster(this IGameNames names, int index) => names.MonsterName(index) ?? $"Monster m{index:D3}";
}

public sealed class NoGameNames : IGameNames
{
    public static NoGameNames Instance { get; } = new();
    public string? ItemName(int itemId) => null;
    public string? MonsterName(int monsterIndex) => null;
}
