using System.Diagnostics;
using FfxRandomizer.Core.Game;
using FfxRandomizer.Core.GameData;
using FfxRandomizer.Core.GameData.Text;
using FfxRandomizer.Core.Patching;
using FfxRandomizer.Core.Storage;
using FfxRandomizer.Core.Vbf;

namespace FfxRandomizer.Tests;

public class GameTextTests
{
    private static readonly Lazy<GameText?> English = new(() =>
    {
        var original = OpenOriginal();
        return original is null ? null : GameText.Load(original);
    });

    private static VbfArchive? OpenOriginal()
    {
        var game = GameInstall.FindSteamInstall();
        return game is null ? null : new PatchManager(game.DataArchivePath, AppPaths.Default).OpenOriginal();
    }

    [Theory]
    [InlineData(0x2000, "Potion")]
    [InlineData(0x2001, "Hi-Potion")]
    [InlineData(0x2006, "Phoenix Down")]
    [InlineData(0x2008, "Elixir")]
    [InlineData(0xA000, "Withered Bouquet")]
    [InlineData(0xA001, "Flint")]
    public void Item_names_decode_from_the_game(int itemId, string expected)
    {
        var text = English.Value;
        if (text is null) return;
        Assert.Equal(expected, text.ItemName(itemId));
    }

    [Theory]
    [InlineData(101, "Tros")]
    [InlineData(107, "Sinscale")]
    [InlineData(144, "Kimahri Weapon")]
    [InlineData(159, "Sinspawn Ammes")]
    [InlineData(189, "Ochu")]
    [InlineData(258, "Valefor")]
    public void Monster_names_decode_from_the_game(int monsterIndex, string expected)
    {
        var text = English.Value;
        if (text is null) return;
        Assert.Equal(expected, text.MonsterName(monsterIndex));
    }

    [Theory]
    [InlineData(163, "Valefor")]
    [InlineData(166, "Shiva")]
    [InlineData(169, "Yojimbo")]
    public void Monster_names_made_of_a_party_name_code_resolve_to_the_name(int monsterIndex, string expected)
    {
        var text = English.Value;
        if (text is null) return;
        Assert.Equal(expected, text.MonsterName(monsterIndex));
    }

    [Theory]
    [InlineData(0x8000, "Sensor")]
    [InlineData(0x801E, "Firestrike")]
    [InlineData(0x8030, "Deathproof")]
    [InlineData(0x8080, "Ribbon")]
    public void Auto_ability_names_decode_from_the_game(int abilityId, string expected)
    {
        var text = English.Value;
        if (text is null) return;
        Assert.Equal(expected, text.AutoAbilityName(abilityId));
    }

    [Fact]
    public void Unknown_ids_have_no_name()
    {
        var text = English.Value;
        if (text is null) return;
        Assert.Null(text.ItemName(0x2000 + text.ItemCount));
        Assert.Null(text.ItemName(0x1000));
        Assert.Null(text.AutoAbilityName(0x00FF));
        Assert.Null(text.AutoAbilityName(0));
        Assert.Null(text.MonsterName(-1));
        Assert.Null(text.MonsterName(5000));
    }

    [Fact]
    public void Every_item_decodes_to_plain_text()
    {
        var text = English.Value;
        if (text is null) return;
        Assert.True(text.ItemCount > 100);
        for (int id = 0x2000; id < 0x2000 + text.ItemCount; id++)
        {
            string? name = text.ItemName(id);
            Assert.False(string.IsNullOrWhiteSpace(name), $"Item {id:X4} has no name");
            AssertPlainText(name!);
        }
    }

    [Fact]
    public void Every_monster_key_item_and_auto_ability_name_is_plain_text()
    {
        var text = English.Value;
        if (text is null) return;
        var names = new List<string?>();
        for (int i = 0; i < 1000; i++) names.Add(text.MonsterName(i));
        for (int i = 0; i < text.KeyItemCount; i++) names.Add(text.ItemName(0xA000 + i));
        for (int i = 0; i < text.AutoAbilityCount; i++) names.Add(text.AutoAbilityName(0x8000 + i));
        Assert.Contains(names, n => n is not null);
        foreach (string? name in names)
            if (name is not null) AssertPlainText(name);
    }

    [Fact]
    public void Loading_all_names_is_fast()
    {
        var original = OpenOriginal();
        if (original is null) return;
        var watch = Stopwatch.StartNew();
        GameText.Load(original);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"Load took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Decoder_drops_control_codes_and_joins_lines()
    {
        // Table: '0'->'A', '1'->'B', '2'->'C' at codes 0x30..0x32. Code 0x30 + 0xD0 = 0x100 is written 0x2C 0x30.
        var glyphs = new char[0x110];
        Array.Fill(glyphs, '#');
        glyphs[0] = 'A';
        glyphs[1] = 'B';
        glyphs[2] = 'C';
        glyphs[0xD0] = 'D';
        var charset = new FfxCharset(new string(glyphs));

        byte[] data = [0x0A, 0x43, 0x30, 0x03, 0x31, 0x01, 0x2C, 0x30, 0x13, 0x30, 0x32, 0x00, 0x31];
        Assert.Equal("A BDTidusC", charset.Decode(data, 0, slot => slot == 0 ? "Tidus" : null));
        Assert.Equal("A BDC", charset.Decode(data, 0));
        Assert.Null(charset.Decode([0x0A, 0x43, 0x03, 0x00], 0));
        Assert.Null(charset.Decode([0x30], 5));
    }

    private static void AssertPlainText(string name)
    {
        Assert.Equal(name.Trim(), name);
        Assert.DoesNotContain(name, c => char.IsControl(c) || c == '�' || c == '{' || c == '}');
    }
}
