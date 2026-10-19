namespace FfxRandomizer.Core.GameData;

public enum EventPatchKind
{
    /// <summary>Replace <see cref="EventPatch.Expected"/> with <see cref="EventPatch.Bytes"/> of the same length.</summary>
    Overwrite,
    /// <summary>Insert <see cref="EventPatch.Bytes"/> just before <see cref="EventPatch.Expected"/>, growing the code.</summary>
    InsertBefore,
}

/// <summary>A jump label a patch's bytes depend on, checked before patching so a game update can't make it jump elsewhere.</summary>
public sealed record EventJump(int Worker, int Jump, int CodeOffset);

/// <summary>
/// One edit to an event script's code. Before changing anything it checks that the code at <see cref="CodeOffset"/> is
/// exactly <see cref="Expected"/> and that every label in <see cref="Jumps"/> still points where it should; a mismatch
/// (a different game version, or another edit already there) throws instead of writing into the wrong place.
/// Code offsets are relative to the start of the script's code, as FFXDataParser prints them.
/// </summary>
public sealed record EventPatch(
    string EventId,
    int CodeOffset,
    byte[] Expected,
    byte[] Bytes,
    EventPatchKind Kind,
    IReadOnlyList<EventJump> Jumps)
{
    public string Path => BlitzballStats.EventPath(EventId);

    /// <summary>Returns the patched event (a new array; the input is not changed).</summary>
    public byte[] Apply(byte[] ebp)
    {
        var script = new EventScript(ebp);
        foreach (string problem in Check(script)) throw new EventPatchMismatchException(this, problem);

        if (Kind == EventPatchKind.InsertBefore) return EventScript.InsertCode(ebp, CodeOffset, Bytes);
        if (Bytes.Length != Expected.Length) throw new InvalidOperationException("An overwrite must keep the code the same length.");
        byte[] copy = (byte[])ebp.Clone();
        Bytes.CopyTo(copy, script.CodeStart + CodeOffset);
        return copy;
    }

    private IEnumerable<string> Check(EventScript script)
    {
        if (CodeOffset < 0 || CodeOffset + Expected.Length > script.CodeLength)
        {
            yield return $"code offset {CodeOffset:X4} is outside the script";
            yield break;
        }
        if (!script.Code(CodeOffset, Expected.Length).SequenceEqual(Expected))
            yield return $"the code at {CodeOffset:X4} is {Convert.ToHexString(script.Code(CodeOffset, Expected.Length))}, expected {Convert.ToHexString(Expected)}";
        foreach (var jump in Jumps)
        {
            int target = script.JumpTarget(jump.Worker, jump.Jump);
            if (target != jump.CodeOffset)
                yield return $"w{jump.Worker:X2} j{jump.Jump:X2} points at {target:X4}, expected {jump.CodeOffset:X4}";
        }
    }
}

public sealed class EventPatchMismatchException(EventPatch patch, string problem)
    : InvalidOperationException($"Event {patch.EventId} isn't the version this edit was made for: {problem}. Nothing was changed.")
{
    public EventPatch Patch { get; } = patch;
}
