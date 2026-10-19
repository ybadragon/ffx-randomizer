using System.Buffers.Binary;

namespace FfxRandomizer.Core.GameData.Events;

/// <summary>One ATEL instruction: opcodes from 0x80 up carry a 16-bit operand.</summary>
public readonly record struct AtelInstruction(int Offset, byte Op, int Operand)
{
    public int Length => Op >= 0x80 ? 3 : 1;
    public int End => Offset + Length;
}

/// <summary>
/// A statement: the instructions from one stack-emptying instruction to the next (a call whose result is dropped, a
/// variable write, a jump, a return...). The compiler emits every source line this way, so a statement can be copied
/// and run elsewhere in the same script.
/// </summary>
public sealed record AtelStatement(int Offset, IReadOnlyList<AtelInstruction> Instructions)
{
    public int End => Instructions[^1].End;
    public int Length => End - Offset;
    public AtelInstruction Last => Instructions[^1];
}

/// <summary>Decodes ATEL bytecode: opcode meanings as FFXDataParser names them (ScriptOpcode).</summary>
public static class AtelCode
{
    public const byte Nop = 0x00, OpLAnd = 0x02, OpEq = 0x06, OpNe = 0x07, OpGt = 0x0A, OpLs = 0x0B, OpGte = 0x0E, OpLse = 0x0F, OpNot = 0x19;
    public const byte PushY = 0x29, PopY = 0x2C;
    public const byte PopA = 0x25, PushV = 0x9F, PopV = 0xA0, PopVL = 0xA1, PushAr = 0xA2, PopAr = 0xA3, PopArL = 0xA4;
    public const byte PushI = 0xAD, PushII = 0xAE, PushF = 0xAF, Jmp = 0xB0, CJmp = 0xB1, NCJmp = 0xB2, Jsr = 0xB3, Call = 0xB5;
    public const byte PopXJmp = 0xD5, PopXCJmp = 0xD6, PopXNCJmp = 0xD7, CallPopA = 0xD8;
    public const byte Ret = 0x3C, RetN = 0x3D, RetT = 0x3E, RetTN = 0x3F, Halt = 0x40, DRet = 0x54, ReqWait = 0x77;

    /// <summary>Instructions that leave the stack empty and so end a statement.</summary>
    public static bool EndsStatement(byte op) => op switch
    {
        Nop or PopA or 0x2A or 0x2C or 0x34 or Ret or RetN or RetT or RetTN or Halt or DRet or ReqWait or 0x78 => true,
        >= 0x59 and <= 0x66 => true, // POPI0..POPF9
        PopV or PopVL or PopAr or PopArL or Jmp or CJmp or NCJmp or Jsr or PopXJmp or PopXCJmp or PopXNCJmp or CallPopA => true,
        _ => false,
    };

    /// <summary>Run another worker's function: REQ, REQSW, REQEW and their forcing/conditional forms. Each pops level, worker, function.</summary>
    public static bool IsRun(byte op) => op is (>= 0x36 and <= 0x38) or (>= 0x45 and <= 0x53);

    /// <summary>Runs that wait for the function to finish.</summary>
    public static bool AwaitsEnd(byte op) => op is 0x38 or 0x4F or 0x50 or 0x51 or 0x52 or 0x53;

    public static bool IsReturn(byte op) => op is Ret or RetN or RetT or RetTN or Halt or DRet;

    public static AtelInstruction At(ReadOnlySpan<byte> code, int offset)
    {
        byte op = code[offset];
        int operand = op >= 0x80 && offset + 2 < code.Length ? BinaryPrimitives.ReadUInt16LittleEndian(code[(offset + 1)..]) : 0;
        return new AtelInstruction(offset, op, operand);
    }

    /// <summary>The statement starting at <paramref name="offset"/>, or null if the code runs out before one ends.</summary>
    public static AtelStatement? StatementAt(ReadOnlySpan<byte> code, int offset)
    {
        var list = new List<AtelInstruction>();
        for (int at = offset; at < code.Length;)
        {
            var instruction = At(code, at);
            list.Add(instruction);
            at = instruction.End;
            if (EndsStatement(instruction.Op)) return new AtelStatement(offset, list);
            if (list.Count > 256) return null;
        }
        return null;
    }

    /// <summary>
    /// The constant a simple expression statement pushes: PUSHII n, or PUSHI of a reference int. Null for anything else.
    /// </summary>
    public static int? Constant(AtelInstruction i, ScriptTables tables) => i.Op switch
    {
        PushII => (short)i.Operand,
        PushI => tables.RefInt(i.Operand),
        _ => null,
    };

    /// <summary>
    /// For "run wXX::fYY at level N" statements whose operands are plain constants: (level, worker, function).
    /// </summary>
    public static (int Level, int Worker, int Function)? RunTarget(AtelStatement s, ScriptTables tables)
    {
        var ins = s.Instructions;
        int run = -1;
        for (int i = 0; i < ins.Count; i++) if (IsRun(ins[i].Op)) run = i;
        if (run < 3) return null;
        if (Constant(ins[run - 3], tables) is not int level || Constant(ins[run - 2], tables) is not int worker || Constant(ins[run - 1], tables) is not int function)
            return null;
        return (level, worker, function);
    }

    /// <summary>
    /// Evaluates a condition statement's expression (everything before its jump) if it only reads GameMoment and
    /// constants: comparisons, and/or/not. Null when it reads anything else.
    /// </summary>
    public static bool? EvaluateMomentCondition(AtelStatement s, int gameMoment, ScriptTables tables, bool switchOnMoment = false)
    {
        var stack = new Stack<int>();
        for (int i = 0; i < s.Instructions.Count - 1; i++)
        {
            var ins = s.Instructions[i];
            switch (ins.Op)
            {
                case PushV when ins.Operand == Atel.GameMomentVariable: stack.Push(gameMoment); break;
                case PushY when switchOnMoment: stack.Push(gameMoment); break;
                case PushII or PushI when Constant(ins, tables) is int c: stack.Push(c); break;
                case OpNot when stack.Count >= 1: stack.Push(stack.Pop() == 0 ? 1 : 0); break;
                case >= 0x01 and <= 0x0F when stack.Count >= 2:
                {
                    int b = stack.Pop(), a = stack.Pop();
                    stack.Push(ins.Op switch
                    {
                        0x01 => (a != 0 || b != 0) ? 1 : 0,
                        0x02 => (a != 0 && b != 0) ? 1 : 0,
                        0x03 => a | b,
                        0x04 => a ^ b,
                        0x05 => a & b,
                        0x06 => a == b ? 1 : 0,
                        0x07 => a != b ? 1 : 0,
                        0x08 or 0x0A => a > b ? 1 : 0,
                        0x09 or 0x0B => a < b ? 1 : 0,
                        0x0C or 0x0E => a >= b ? 1 : 0,
                        _ => a <= b ? 1 : 0,
                    });
                    break;
                }
                default: return null;
            }
        }
        return stack.Count == 1 ? stack.Pop() != 0 : null;
    }

    /// <summary>The GameMoment value a statement writes ("Set GameMoment = n"), if it is one.</summary>
    public static int? MomentWrite(AtelStatement s, ScriptTables tables) =>
        s.Instructions is [var push, { Op: PopV or PopVL, Operand: Atel.GameMomentVariable }] && Constant(push, tables) is int value ? value : null;

    /// <summary>Every GameMoment constant a statement compares against.</summary>
    public static IEnumerable<int> MomentConstants(AtelStatement s, ScriptTables tables)
    {
        var ins = s.Instructions;
        for (int i = 0; i + 1 < ins.Count; i++)
        {
            if (ins[i].Op == PushV && ins[i].Operand == Atel.GameMomentVariable && Constant(ins[i + 1], tables) is int c) yield return c;
            if (i + 1 < ins.Count && Constant(ins[i], tables) is int d && ins[i + 1].Op == PushV && ins[i + 1].Operand == Atel.GameMomentVariable) yield return d;
            // switch cases: "(n == case) -> jXX" (the switch subject may be GameMoment)
            if (Constant(ins[i], tables) is int e && ins[i + 1].Op == PushY) yield return e;
        }
    }

    /// <summary>Whether a statement is "switch GameMoment".</summary>
    public static bool SwitchesOnMoment(AtelStatement s) =>
        s.Instructions is [{ Op: PushV, Operand: Atel.GameMomentVariable }, { Op: PopY }];
}

/// <summary>
/// A script's shared tables: every worker shares one variable declaration table and one set of reference ints and floats
/// (FFXDataParser writes them that way), plus each worker's type, functions and jumps.
/// </summary>
public sealed class ScriptTables
{
    private readonly byte[] _s;

    public ScriptTables(byte[] scriptChunk)
    {
        _s = scriptChunk;
        Editor = new EventScriptEditor(scriptChunk);
    }

    public EventScriptEditor Editor { get; }
    public int WorkerCount => Editor.WorkerCount;
    public ReadOnlySpan<byte> Code => _s.AsSpan(Editor.CodeStart, Editor.CodeLength);

    private int Header(int worker) => (int)BinaryPrimitives.ReadUInt32LittleEndian(_s.AsSpan(0x38 + worker * 4));

    /// <summary>The worker's type: 0 subroutine, 1 field object, 2 player edge, 4 scenario, 5 edge...</summary>
    public int WorkerType(int worker) => BinaryPrimitives.ReadUInt16LittleEndian(_s.AsSpan(Header(worker)));

    public int VariableCount => BinaryPrimitives.ReadUInt16LittleEndian(_s.AsSpan(Header(0) + 0x02));

    /// <summary>Where a variable lives: 0 save data, 3 private, 4 shared, 6 event data.</summary>
    public int VariableLocation(int variable)
    {
        if (variable < 0 || variable >= VariableCount) return -1;
        int table = (int)BinaryPrimitives.ReadUInt32LittleEndian(_s.AsSpan(Header(0) + 0x14));
        uint declaration = BinaryPrimitives.ReadUInt32LittleEndian(_s.AsSpan(table + variable * 8));
        return (int)(declaration & 0x0F000000) >> 25;
    }

    public int? RefInt(int index)
    {
        int count = BinaryPrimitives.ReadUInt16LittleEndian(_s.AsSpan(Header(0) + 0x04));
        if (index < 0 || index >= count) return null;
        int table = (int)BinaryPrimitives.ReadUInt32LittleEndian(_s.AsSpan(Header(0) + 0x18));
        return BinaryPrimitives.ReadInt32LittleEndian(_s.AsSpan(table + index * 4));
    }

    /// <summary>The function a code offset belongs to: the function with the greatest entry at or before it.</summary>
    public (int Worker, int Function)? FunctionAt(int offset)
    {
        (int, int)? best = null;
        int bestEntry = -1;
        for (int w = 0; w < WorkerCount; w++)
            for (int f = 0; f < Editor.FunctionCount(w); f++)
            {
                int entry = Editor.FunctionEntry(w, f);
                if (entry <= offset && entry > bestEntry) (best, bestEntry) = ((w, f), entry);
            }
        return best;
    }

    /// <summary>The worker whose jump table holds a label at <paramref name="offset"/>, and the label, or null.</summary>
    public int? JumpAt(int worker, int offset)
    {
        for (int j = 0; j < Editor.JumpCount(worker); j++) if (Editor.JumpTarget(worker, j) == offset) return j;
        return null;
    }
}
