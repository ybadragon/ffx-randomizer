namespace FfxRandomizer.Core.GameData;

/// <summary>Paths of game files inside FFX_Data.vbf.</summary>
public static class GamePaths
{
    public const string Master = "ffx_ps2/ffx/master/";

    /// <summary>The per-language folders whose battle kernels the game reads for party data.</summary>
    public static readonly IReadOnlyList<string> LanguageFolders =
        ["new_chpc", "new_depc", "new_frpc", "new_itpc", "new_jppc", "new_krpc", "new_sppc", "new_uspc"];

    public static string LanguageKernel(string language, string file) => $"{Master}{language}/battle/kernel/{file}";
}

/// <summary>Byte offsets inside one ply_save.bin record (148 bytes per character).</summary>
public static class PlySave
{
    public const string FileName = "ply_save.bin";
    public const int Tidus = 0;

    public const int BaseHp = 0x04;
    public const int CurrentHp = 0x1C;
    public const int MaxHp = 0x24;
}
