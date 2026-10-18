namespace FfxRandomizer.Core.Randomization;

/// <summary>
/// xoshiro256** seeded through SplitMix64. Implemented here because System.Random's sequence for a given seed
/// isn't guaranteed across runtime versions, and replaying a seed must always produce the same game.
/// </summary>
public sealed class Rng
{
    private ulong _s0, _s1, _s2, _s3;

    public Rng(ulong seed)
    {
        ulong x = seed;
        _s0 = SplitMix64(ref x);
        _s1 = SplitMix64(ref x);
        _s2 = SplitMix64(ref x);
        _s3 = SplitMix64(ref x);
    }

    internal Rng(ulong s0, ulong s1, ulong s2, ulong s3) => (_s0, _s1, _s2, _s3) = (s0, s1, s2, s3);

    /// <summary>
    /// An independent stream for one part of the randomizer, so turning one option on or off
    /// never changes what another option produces for the same seed.
    /// </summary>
    public static Rng For(Seed seed, string section) => new(seed.Value ^ Seed.Hash(section));

    public ulong NextUInt64()
    {
        ulong result = RotateLeft(_s1 * 5, 7) * 9;
        ulong t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = RotateLeft(_s3, 45);
        return result;
    }

    /// <summary>Uniform integer in [min, max], inclusive, without modulo bias.</summary>
    public int Next(int min, int max)
    {
        if (max < min) throw new ArgumentOutOfRangeException(nameof(max));
        ulong range = (ulong)((long)max - min) + 1;
        ulong limit = ulong.MaxValue - ulong.MaxValue % range;
        ulong value;
        do value = NextUInt64(); while (value >= limit);
        return (int)((long)min + (long)(value % range));
    }

    /// <summary>Uniform double in [0, 1).</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    public T Pick<T>(IReadOnlyList<T> items) => items[Next(0, items.Count - 1)];

    public void Shuffle<T>(IList<T> items)
    {
        for (int i = items.Count - 1; i > 0; i--)
        {
            int j = Next(0, i);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    private static ulong SplitMix64(ref ulong x)
    {
        ulong z = x += 0x9E3779B97F4A7C15;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
        return z ^ (z >> 31);
    }

    private static ulong RotateLeft(ulong x, int k) => (x << k) | (x >> (64 - k));
}
