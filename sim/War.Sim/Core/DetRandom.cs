namespace War.Sim.Core;

/// <summary>
/// Which subsystem a random stream belongs to.
///
/// Each subsystem draws from its own independently-seeded generator. This matters more
/// than it looks: with a single shared stream, adding one new dice roll to the missile
/// code would shift every subsequent melee roll, and a balance change in one system
/// would silently invalidate every saved replay. Separate streams keep systems
/// insulated from each other's churn.
/// </summary>
public enum RngStream : uint
{
    Setup = 1,
    Melee = 2,
    Missile = 3,
    Morale = 4,
    Fatigue = 5,
    Rout = 6,
    Ai = 7,
    Terrain = 8,
    Cosmetic = 9,

    /// <summary>Campaign-layer streams. Separate, so a battle never perturbs the map.</summary>
    Campaign = 10,
    CampaignBattle = 11,
}

/// <summary>
/// Deterministic pseudo-random generator: xorshift128, seeded explicitly, with no
/// dependency on time, thread, or platform. Given the same seed it produces the same
/// sequence forever, on any machine, which is what makes battles replayable.
///
/// This is not cryptographically secure and is not trying to be.
/// </summary>
public sealed class DetRandom
{
    private uint _x, _y, _z, _w;

    public DetRandom(uint seed, RngStream stream = RngStream.Setup)
        : this(seed, (uint)stream) { }

    public DetRandom(uint seed, uint stream)
    {
        // Expand the (seed, stream) pair into four well-mixed words via splitmix32,
        // so nearby seeds don't produce correlated sequences.
        uint s = seed ^ (stream * 0x9E3779B9u);
        _x = SplitMix32(ref s);
        _y = SplitMix32(ref s);
        _z = SplitMix32(ref s);
        _w = SplitMix32(ref s);

        // xorshift128 is dead if every word is zero.
        if ((_x | _y | _z | _w) == 0) _x = 0x1D872B41u;

        // Discard the first few outputs so the very first draw isn't seed-shaped.
        for (int i = 0; i < 8; i++) NextUInt();
    }

    private static uint SplitMix32(ref uint state)
    {
        uint z = unchecked(state += 0x9E3779B9u);
        z = unchecked((z ^ (z >> 16)) * 0x21F0AAADu);
        z = unchecked((z ^ (z >> 15)) * 0x735A2D97u);
        return z ^ (z >> 15);
    }

    // ------------------------------------------------------------------- core

    public uint NextUInt()
    {
        uint t = _x ^ (_x << 11);
        _x = _y;
        _y = _z;
        _z = _w;
        _w = _w ^ (_w >> 19) ^ t ^ (t >> 8);
        return _w;
    }

    // ---------------------------------------------------------------- integers

    /// <summary>
    /// Uniform integer in [0, maxExclusive). Uses Lemire's multiply-shift rather than
    /// a modulo: no division, and no modulo bias worth caring about at these ranges.
    /// </summary>
    public int NextInt(int maxExclusive)
    {
        if (maxExclusive <= 1) return 0;
        return (int)(((ulong)NextUInt() * (ulong)(uint)maxExclusive) >> 32);
    }

    /// <summary>Uniform integer in [minInclusive, maxExclusive).</summary>
    public int NextInt(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive) return minInclusive;
        return minInclusive + NextInt(maxExclusive - minInclusive);
    }

    // ------------------------------------------------------------------- fixed

    /// <summary>Uniform value in [0, 1).</summary>
    public Fix NextFix() => Fix.FromRaw((int)(NextUInt() >> 16));

    /// <summary>Uniform value in [min, max).</summary>
    public Fix NextFix(Fix min, Fix max) => min + (max - min) * NextFix();

    /// <summary>Uniform value in [-1, 1).</summary>
    public Fix NextSigned() => Fix.FromRaw((int)(NextUInt() >> 15) - Fix.OneRaw);

    /// <summary>
    /// Value in [-1, 1) clustered around zero — the average of three uniform draws.
    /// Used for missile scatter and attack-timing jitter, where a bell shape reads as
    /// far more natural than a flat one.
    /// </summary>
    public Fix NextSpread()
    {
        int sum = NextSigned().Raw + NextSigned().Raw + NextSigned().Raw;
        return Fix.FromRaw(sum / 3);
    }

    /// <summary>True with the given probability, where <paramref name="probability"/> is in [0, 1].</summary>
    public bool Chance(Fix probability)
    {
        if (probability.Raw <= 0) return false;
        if (probability.Raw >= Fix.OneRaw) return true;
        return NextFix().Raw < probability.Raw;
    }

    /// <summary>True with probability <paramref name="numerator"/>/<paramref name="denominator"/>.</summary>
    public bool Chance(int numerator, int denominator) =>
        NextInt(denominator) < numerator;

    // ------------------------------------------------------------------ vectors

    /// <summary>A uniformly distributed unit vector.</summary>
    public FixVec2 NextDirection()
    {
        FixVec2 d = FixVec2.FromAngle(NextFix() * Fix.TwoPi).Normalized;
        return d.IsZero ? FixVec2.East : d;
    }

    /// <summary>A uniformly distributed point inside a circle of the given radius.</summary>
    public FixVec2 NextPointInCircle(Fix radius)
    {
        // sqrt on the radial coordinate keeps the distribution area-uniform rather
        // than clumping everything toward the centre.
        Fix r = FixMath.Sqrt(NextFix()) * radius;
        return NextDirection() * r;
    }

    // ------------------------------------------------------------------ shuffle

    /// <summary>In-place Fisher-Yates. Deterministic given the generator's state.</summary>
    public void Shuffle<T>(IList<T> items)
    {
        for (int i = items.Count - 1; i > 0; i--)
        {
            int j = NextInt(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    // -------------------------------------------------------------- save/restore

    /// <summary>Full generator state, for save games and replay checkpoints.</summary>
    public readonly record struct State(uint X, uint Y, uint Z, uint W);

    public State Save() => new(_x, _y, _z, _w);

    public void Restore(State state)
    {
        _x = state.X;
        _y = state.Y;
        _z = state.Z;
        _w = state.W;
    }
}
