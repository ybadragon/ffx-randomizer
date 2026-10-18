using System.Security.Cryptography;
using System.Text;

namespace FfxRandomizer.Core.Randomization;

/// <summary>
/// A seed as the player sees it (any text) plus the stable number it stands for.
/// string.GetHashCode is randomized per process, so the number comes from SHA-256 instead.
/// </summary>
public readonly record struct Seed(string Text)
{
    public ulong Value => Hash(Text.Trim());

    public static Seed NewRandom() => new(RandomNumberGenerator.GetInt32(100_000_000, int.MaxValue).ToString());

    public static ulong Hash(string text) =>
        BitConverter.ToUInt64(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0);

    public override string ToString() => Text;
}
