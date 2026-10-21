namespace FfxRandomizer.Core.GameData.Events;

/// <summary>A controller button the skip watcher can listen for.</summary>
/// <param name="Index">The game's controllerButton number, as <see cref="Atel.IsDown"/> takes it.</param>
/// <param name="Icon">The text control code argument (after 0x0B) that draws this button, when known; null shows <paramref name="Name"/>.</param>
/// <param name="AdvancesDialogue">Also skips voiced lines and closes text boxes in vanilla scripts.</param>
public sealed record SkipButton(string Id, int Index, string Label, string Name, byte? Icon, bool AdvancesDialogue = false);

/// <summary>
/// The buttons offered for skipping, PlayStation names with their Xbox equivalent. Button numbers from FFXDataParser's
/// controllerButton enum (ScriptConstants); icon codes from its text control code table (StringHelper.getControllerInput),
/// where L2 and Start aren't known, so those show their name instead. Not the stick clicks: 9 and 10 (the gaps in the
/// enum, where the PS2 pad has L3 and R3) were never seen held on PC.
/// </summary>
public static class SkipButtons
{
    public static SkipButton Default => All[0];

    public static IReadOnlyList<SkipButton> All { get; } =
    [
        new("select", 8, "Select (Back)", "SELECT", 0x39),
        new("l1", 2, "L1 (LB)", "L1", 0x34),
        new("r1", 3, "R1 (RB)", "R1", 0x35),
        new("l2", 0, "L2 (LT)", "L2", null),
        new("r2", 1, "R2 (RT)", "R2", 0x37),
        new("triangle", 4, "Triangle (Y)", "TRIANGLE", 0x30),
        new("square", 7, "Square (X)", "SQUARE", 0x33),
        new("start", 11, "Start", "START", null),
        new("x", 5, "X (A)", "X", 0x31, AdvancesDialogue: true),
        new("circle", 6, "Circle (B)", "CIRCLE", 0x32, AdvancesDialogue: true),
    ];

    public static SkipButton ById(string? id) => All.FirstOrDefault(b => b.Id == id) ?? Default;

    /// <summary>
    /// The game button for an Xbox controller button, with the standard layout: A=X, B=Circle, X=Square, Y=Triangle,
    /// LB=L1, RB=R1, LT=L2, RT=R2, Back=Select, Start=Start. Null for buttons that can't skip (sticks, d-pad, guide).
    /// </summary>
    public static SkipButton? FromXbox(string xboxButton) => xboxButton switch
    {
        "A" => ById("x"),
        "B" => ById("circle"),
        "X" => ById("square"),
        "Y" => ById("triangle"),
        "LB" => ById("l1"),
        "RB" => ById("r1"),
        "LT" => ById("l2"),
        "RT" => ById("r2"),
        "Back" or "View" => ById("select"),
        "Start" or "Menu" => ById("start"),
        _ => null,
    };
}
