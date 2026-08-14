namespace War.Sim.Core;

/// <summary>
/// A deterministic 2D vector. The battlefield is simulated in plan view: X is east,
/// Y is north, and elevation is a separate lookup against the terrain heightmap.
/// Soldiers move, fight, and rout in two dimensions; height only modifies the
/// numbers, it does not add a degree of freedom.
///
/// Squared magnitudes are deliberately exposed as raw <see cref="long"/> values in
/// Q32.32 rather than as <see cref="Fix"/>. Squaring a distance of more than about
/// 181 units overflows Q16.16, and the battlefield is far larger than that, so every
/// radius test compares raw longs instead. <see cref="Magnitude"/> is safe at any
/// battlefield distance because it roots the 64-bit intermediate directly.
/// </summary>
public readonly struct FixVec2 : IEquatable<FixVec2>
{
    public readonly Fix X;
    public readonly Fix Y;

    public FixVec2(Fix x, Fix y)
    {
        X = x;
        Y = y;
    }

    public static FixVec2 FromRaw(int rawX, int rawY) =>
        new(Fix.FromRaw(rawX), Fix.FromRaw(rawY));

    public static FixVec2 FromInt(int x, int y) => new(Fix.FromInt(x), Fix.FromInt(y));

    public static readonly FixVec2 Zero = new(Fix.Zero, Fix.Zero);
    public static readonly FixVec2 One = new(Fix.One, Fix.One);

    /// <summary>East. The canonical "no rotation" facing.</summary>
    public static readonly FixVec2 East = new(Fix.One, Fix.Zero);

    /// <summary>North.</summary>
    public static readonly FixVec2 North = new(Fix.Zero, Fix.One);

    public bool IsZero => X.Raw == 0 && Y.Raw == 0;

    // ------------------------------------------------------------- arithmetic

    public static FixVec2 operator +(FixVec2 a, FixVec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static FixVec2 operator -(FixVec2 a, FixVec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static FixVec2 operator -(FixVec2 v) => new(-v.X, -v.Y);
    public static FixVec2 operator *(FixVec2 v, Fix s) => new(v.X * s, v.Y * s);
    public static FixVec2 operator *(Fix s, FixVec2 v) => new(v.X * s, v.Y * s);
    public static FixVec2 operator *(FixVec2 v, int s) => new(v.X * s, v.Y * s);
    public static FixVec2 operator /(FixVec2 v, Fix s) => new(v.X / s, v.Y / s);
    public static FixVec2 operator /(FixVec2 v, int s) => new(v.X / s, v.Y / s);

    public static bool operator ==(FixVec2 a, FixVec2 b) => a.X.Raw == b.X.Raw && a.Y.Raw == b.Y.Raw;
    public static bool operator !=(FixVec2 a, FixVec2 b) => !(a == b);

    // ----------------------------------------------------------------- length

    /// <summary>Squared magnitude as a raw Q32.32 long. Never overflows at battlefield scale.</summary>
    public long SqrMagnitudeRaw => (long)X.Raw * X.Raw + (long)Y.Raw * Y.Raw;

    public Fix Magnitude
    {
        get
        {
            long sqr = SqrMagnitudeRaw;
            if (sqr == 0) return Fix.Zero;
            // sqrt of a Q32.32 value lands directly in Q16.16 — no shifting needed.
            return Fix.FromRaw((int)FixMath.ISqrt64((ulong)sqr));
        }
    }

    /// <summary>Unit vector in the same direction, or zero if this vector is zero.</summary>
    public FixVec2 Normalized
    {
        get
        {
            long sqr = SqrMagnitudeRaw;
            if (sqr == 0) return Zero;
            long len = (long)FixMath.ISqrt64((ulong)sqr);
            if (len == 0) return Zero;
            // Divide in the wider type to keep precision on very short vectors.
            return new FixVec2(
                Fix.FromRaw((int)(((long)X.Raw << Fix.FractionalBits) / len)),
                Fix.FromRaw((int)(((long)Y.Raw << Fix.FractionalBits) / len)));
        }
    }

    // --------------------------------------------------------------- geometry

    public static Fix Dot(FixVec2 a, FixVec2 b) => a.X * b.X + a.Y * b.Y;

    /// <summary>2D cross product (the z component of the 3D cross). Positive means b is counter-clockwise of a.</summary>
    public static Fix Cross(FixVec2 a, FixVec2 b) => a.X * b.Y - a.Y * b.X;

    /// <summary>Rotated 90 degrees clockwise. For a facing vector, this is the unit's right flank.</summary>
    public FixVec2 Right => new(Y, -X);

    /// <summary>Rotated 90 degrees counter-clockwise. For a facing vector, this is the unit's left flank.</summary>
    public FixVec2 Left => new(-Y, X);

    public static Fix Distance(FixVec2 a, FixVec2 b) => (b - a).Magnitude;

    public static long SqrDistanceRaw(FixVec2 a, FixVec2 b) => (b - a).SqrMagnitudeRaw;

    /// <summary>
    /// Radius test that avoids both a square root and Q16.16 overflow. This is the
    /// single most-called geometric predicate in the simulation.
    /// </summary>
    public static bool WithinDistance(FixVec2 a, FixVec2 b, Fix radius) =>
        SqrDistanceRaw(a, b) <= FixMath.SqrRaw(radius);

    /// <summary>
    /// Rotates this vector by the rotation that <paramref name="facing"/> represents,
    /// treating facing as (cos, sin). Used to place formation slots: a slot's local
    /// offset rotated by the unit's facing gives its world position, with no trig.
    /// </summary>
    public FixVec2 Rotate(FixVec2 facing) =>
        new(X * facing.X - Y * facing.Y, X * facing.Y + Y * facing.X);

    public static FixVec2 Lerp(FixVec2 a, FixVec2 b, Fix t)
    {
        t = FixMath.Clamp01(t);
        return new FixVec2(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
    }

    /// <summary>Steps toward a target by at most <paramref name="maxDelta"/>, never overshooting.</summary>
    public static FixVec2 MoveTowards(FixVec2 current, FixVec2 target, Fix maxDelta)
    {
        FixVec2 delta = target - current;
        long sqr = delta.SqrMagnitudeRaw;
        if (sqr == 0 || sqr <= FixMath.SqrRaw(maxDelta)) return target;
        return current + delta.Normalized * maxDelta;
    }

    /// <summary>Shortens the vector to <paramref name="maxLength"/> if it is longer, otherwise returns it unchanged.</summary>
    public FixVec2 ClampMagnitude(Fix maxLength)
    {
        if (SqrMagnitudeRaw <= FixMath.SqrRaw(maxLength)) return this;
        return Normalized * maxLength;
    }

    /// <summary>
    /// Rotates <paramref name="from"/> toward <paramref name="to"/> by at most the angle
    /// encoded in <paramref name="step"/>, which is <c>(cos θ, sin θ)</c> for the maximum
    /// turn permitted — exactly what <see cref="FromAngle"/> produces. Unit types
    /// precompute their step once at load, so a soldier pivoting costs four multiplies
    /// and no trigonometry at all.
    ///
    /// This is a true constant-rate rotation rather than a lerp toward the target.
    /// Lerping looks equivalent and is not: blending a vector toward its exact opposite
    /// produces no perpendicular component, so it shrinks along its own axis and
    /// normalises straight back to where it started. A unit ordered to about-face would
    /// stand there forever.
    /// </summary>
    public static FixVec2 TurnTowards(FixVec2 from, FixVec2 to, FixVec2 step)
    {
        if (to.IsZero) return from;
        FixVec2 target = to.Normalized;
        if (from.IsZero) return target;

        // Already within one step of the target — snap and stop, so facing settles
        // exactly instead of oscillating around the goal.
        Fix dot = Dot(from, target);
        if (dot >= step.X) return target;

        // Cross product picks the shorter way round. At an exact reversal it is zero
        // and neither direction is shorter, so we take one deterministically.
        Fix cross = Cross(from, target);
        FixVec2 rotation = cross.Raw >= 0 ? step : new FixVec2(step.X, -step.Y);

        // Renormalise so thousands of ticks of rounding can't slowly grow or shrink
        // the facing vector.
        return from.Rotate(rotation).Normalized;
    }

    /// <summary>
    /// Convenience overload taking the maximum turn directly in radians. Builds the
    /// rotation step on the spot, so keep it out of per-soldier loops.
    /// </summary>
    public static FixVec2 TurnTowards(FixVec2 from, FixVec2 to, Fix maxRadians) =>
        TurnTowards(from, to, FromAngle(maxRadians));

    /// <summary>Direction as an angle in radians, east being zero. Debug and UI only.</summary>
    public Fix ToAngle() => FixMath.Atan2(Y, X);

    public static FixVec2 FromAngle(Fix radians) =>
        new(FixMath.Cos(radians), FixMath.Sin(radians));

    // ---------------------------------------------------------------- plumbing

    public bool Equals(FixVec2 other) => this == other;
    public override bool Equals(object? obj) => obj is FixVec2 other && this == other;
    public override int GetHashCode() => HashCode.Combine(X.Raw, Y.Raw);
    public override string ToString() => $"({X}, {Y})";
}
