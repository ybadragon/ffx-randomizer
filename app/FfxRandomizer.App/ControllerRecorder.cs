using System.Runtime.InteropServices;

namespace FfxRandomizer.App;

/// <summary>
/// Waits for a button press on any XInput controller (Xbox controllers, and others Steam Input presents as one) and
/// names it the way <see cref="Core.GameData.Events.SkipButtons.FromXbox"/> expects. Buttons already held when
/// recording starts are ignored until released.
/// </summary>
public static class ControllerRecorder
{
    private const int TriggerThreshold = 64;

    private static readonly (ushort Bit, string Name)[] Buttons =
    [
        (0x1000, "A"), (0x2000, "B"), (0x4000, "X"), (0x8000, "Y"), (0x0100, "LB"), (0x0200, "RB"),
        (0x0020, "Back"), (0x0010, "Start"), (0x0001, "DPadUp"), (0x0002, "DPadDown"), (0x0004, "DPadLeft"),
        (0x0008, "DPadRight"), (0x0040, "LeftThumb"), (0x0080, "RightThumb"),
    ];

    /// <summary>Whether any controller is connected right now.</summary>
    public static bool AnyConnected() => Enumerable.Range(0, 4).Any(i => Read(i) is not null);

    /// <summary>The first newly pressed button's name, or null when <paramref name="timeout"/> passes first.</summary>
    public static async Task<string?> RecordAsync(TimeSpan timeout, CancellationToken cancel = default)
    {
        var held = new HashSet<string>[4];
        for (int i = 0; i < 4; i++) held[i] = Pressed(i).ToHashSet();
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(16, cancel);
            for (int i = 0; i < 4; i++)
            {
                var now = Pressed(i).ToHashSet();
                if (now.FirstOrDefault(b => !held[i].Contains(b)) is { } button) return button;
                held[i].IntersectWith(now);
            }
        }
        return null;
    }

    /// <summary>
    /// The buttons held together on one controller: recording starts with the first newly pressed button and ends when
    /// they are all let go again; the result is the most held at once. Null when nothing is pressed before
    /// <paramref name="timeout"/>.
    /// </summary>
    public static async Task<IReadOnlyList<string>?> RecordComboAsync(TimeSpan timeout, CancellationToken cancel = default)
    {
        var held = new HashSet<string>[4];
        for (int i = 0; i < 4; i++) held[i] = Pressed(i).ToHashSet();
        var deadline = DateTime.UtcNow + timeout;
        int user = -1;
        var most = new HashSet<string>();
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(16, cancel);
            if (user < 0)
            {
                for (int i = 0; i < 4 && user < 0; i++)
                {
                    var now = Pressed(i).ToHashSet();
                    now.ExceptWith(held[i]);
                    if (now.Count > 0) (user, most) = (i, now);
                    held[i].IntersectWith(Pressed(i));
                }
                continue;
            }
            var pressed = Pressed(user).ToHashSet();
            if (pressed.Count == 0) return [.. most];
            if (pressed.Count > most.Count) most = pressed;
        }
        return user < 0 ? null : [.. most];
    }

    private static IEnumerable<string> Pressed(int user)
    {
        if (Read(user) is not { } pad) yield break;
        foreach (var (bit, name) in Buttons)
            if ((pad.Buttons & bit) != 0) yield return name;
        if (pad.LeftTrigger >= TriggerThreshold) yield return "LT";
        if (pad.RightTrigger >= TriggerThreshold) yield return "RT";
    }

    private static Gamepad? Read(int user)
    {
        try
        {
            return XInputGetState((uint)user, out var state) == 0 ? state.Pad : null;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Gamepad
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct State
    {
        public uint PacketNumber;
        public Gamepad Pad;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint userIndex, out State state);
}
