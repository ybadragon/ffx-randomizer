namespace FfxRandomizer.Core.GameData;

/// <summary>
/// A cutscene that can be skipped: the function that plays it and the label its skip jumps to. Both edits insert a short
/// check at the start of the function, so with the check false the scene plays exactly as before.
/// </summary>
public sealed record SkippableScene(string Name, string Area, EventPatch Always, EventPatch HoldSelect, EventPatch HoldAnyButton);

/// <summary>
/// The scenes "Skip cutscenes" covers. The idea of jumping from a scene's start straight to its exit comes from CSR2,
/// the Fahrenheit cutscene remover (MIT, The Fahrenheit contributors, https://github.com/fahrenheit-crew/fh-mods-csr).
/// </summary>
public static class Cutscenes
{
    /// <summary>
    /// Bit 8 (0x100) of HeldButtonsBitfield (script call 0x0046) is Select, controllerButton 8 in FFXDataParser's enum.
    /// The game's own scripts test X the same way: <c>B54600 AE2000 05</c> is HeldButtonsBitfield &amp; 0x20 (X is 5).
    /// </summary>
    public const ushort SelectButtonBit = 1 << 8;

    // GameMoment (save variable 0 in these scripts).
    private static readonly byte[] PushGameMoment = [0x9F, 0x00, 0x00];

    private static byte[] PushInt16(int value) => [0xAE, (byte)value, (byte)(value >> 8)];
    private const byte LessThan = 0x0B, BitwiseAnd = 0x05, LogicalAnd = 0x02;
    private static byte[] JumpIfTrue(int jump) => [0xD6, (byte)jump, (byte)(jump >> 8)];
    private static readonly byte[] HeldButtons = [0xB5, 0x46, 0x00];

    /// <summary>if (GameMoment &lt; <paramref name="moment"/>) jump to <paramref name="exit"/>.</summary>
    private static byte[] SkipWhileBefore(int moment, int exit) =>
        [.. PushGameMoment, .. PushInt16(moment), LessThan, .. JumpIfTrue(exit)];

    /// <summary>Diagnostic mask: any held button (0xFFFF, pushed as int16 -1, so every bit is set).</summary>
    public const ushort AnyButtonMask = 0xFFFF;

    /// <summary>if (GameMoment &lt; <paramref name="moment"/> and a button in <paramref name="mask"/> is held) jump to <paramref name="exit"/>.</summary>
    private static byte[] SkipWhileBeforeIfHeld(int moment, int exit, ushort mask) =>
        [.. PushGameMoment, .. PushInt16(moment), LessThan, .. HeldButtons, .. PushInt16(mask), BitwiseAnd, LogicalAnd, .. JumpIfTrue(exit)];

    private static SkippableScene Scene(string name, string area, string eventId, int codeOffset, byte[] expected, int moment, EventJump exit) =>
        new(name, area,
            new EventPatch(eventId, codeOffset, expected, SkipWhileBefore(moment, exit.Jump), EventPatchKind.InsertBefore, [exit]),
            new EventPatch(eventId, codeOffset, expected, SkipWhileBeforeIfHeld(moment, exit.Jump, SelectButtonBit), EventPatchKind.InsertBefore, [exit]),
            new EventPatch(eventId, codeOffset, expected, SkipWhileBeforeIfHeld(moment, exit.Jump, AnyButtonMask), EventPatchKind.InsertBefore, [exit]));

    public static IReadOnlyList<SkippableScene> All { get; } =
    [
        // znkd0600 w0D::f02 plays the campfire scene on the first visit (GameMoment < 2) and the late-game return (2700).
        // Its first-visit exit j13 hides the title card, sets GameMoment = 2 and warps to the Zanarkand harbor. Only the
        // first visit is skipped; the late-game scene still plays.
        Scene("Zanarkand Ruins campfire (opening)", "Zanarkand", "znkd0600", 0x3DC4,
            expected: [0xAE, 0x01, 0x00, 0xAE, 0x01, 0x00, 0xD8, 0x0D, 0x80], // call Map.setGfxVisibility(1, true)
            moment: 2, exit: new EventJump(Worker: 0x0D, Jump: 0x13, CodeOffset: 0x4426)),
    ];
}
