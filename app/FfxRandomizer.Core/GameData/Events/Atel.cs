namespace FfxRandomizer.Core.GameData.Events;

/// <summary>
/// Builds ATEL bytecode, the event script language. Opcodes and call targets as FFXDataParser names them
/// (ScriptOpcode / ScriptCallTargetLib). Every value goes through the stack; calls pop their arguments in order.
/// </summary>
public static class Atel
{
    public const byte Return = 0x3C, DirectReturn = 0x54;
    public const byte LogicalAnd = 0x02, BitwiseAnd = 0x05, LessThan = 0x0B;
    private const byte PushInt16 = 0xAE, PushVariable = 0x9F, SetVariable = 0xA0;
    private const byte CallKeep = 0xB5, CallDiscard = 0xD8, Jump = 0xB0, JumpIfTrue = 0xD6, JumpIfFalse = 0xD7;
    private const byte RunAsync = 0x36, RunAwaitEnd = 0x38, DiscardResult = 0x25;

    // Call targets.
    public const int Wait = 0x0000, DisplayFieldString = 0x0064, PositionMessage = 0x0065, ShowMessage = 0x006A,
        CloseMessage = 0x006B, HeldButtonsBitfield = 0x0046;

    /// <summary>The GameMoment story-progress variable is variable 0 in every event script.</summary>
    public const int GameMomentVariable = 0;

    public static byte[] Push(int value) => [PushInt16, (byte)value, (byte)(value >> 8)];
    public static byte[] Get(int variable) => [PushVariable, (byte)variable, (byte)(variable >> 8)];
    public static byte[] Set(int variable, int value) => [.. Push(value), SetVariable, (byte)variable, (byte)(variable >> 8)];
    public static byte[] Goto(int jump) => [Jump, (byte)jump, (byte)(jump >> 8)];
    public static byte[] GotoIf(int jump) => [JumpIfTrue, (byte)jump, (byte)(jump >> 8)];
    public static byte[] GotoUnless(int jump) => [JumpIfFalse, (byte)jump, (byte)(jump >> 8)];

    /// <summary>Calls a function and drops its result; arguments are pushed in order.</summary>
    public static byte[] Call(int target, params int[] arguments) =>
        [.. arguments.SelectMany(Push), CallDiscard, (byte)target, (byte)(target >> 8)];

    /// <summary>Calls a function with no arguments and leaves its result on the stack.</summary>
    public static byte[] Value(int target) => [CallKeep, (byte)target, (byte)(target >> 8)];

    /// <summary>Starts a worker's function at a signal level, either without waiting or waiting for it to end.</summary>
    public static byte[] Run(int level, int worker, int function, bool awaitEnd) =>
        [.. Push(level), .. Push(worker), .. Push(function), awaitEnd ? RunAwaitEnd : RunAsync, DiscardResult];

    /// <summary>Pushes whether any button in <paramref name="mask"/> is held.</summary>
    public static byte[] Held(int mask) => [.. Value(HeldButtonsBitfield), .. Push(mask), BitwiseAnd];

    /// <summary>Pushes GameMoment &lt; <paramref name="moment"/>.</summary>
    public static byte[] MomentBelow(int moment) => [.. Get(GameMomentVariable), .. Push(moment), LessThan];
}
