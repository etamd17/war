namespace War.Sim.Core;

/// <summary>
/// Deterministic replacements for everything the simulation would otherwise reach
/// into <c>System.Math</c> for. Every function here is pure integer arithmetic:
/// no floats, no tables built from doubles, no runtime-version-dependent intrinsics.
///
/// The trigonometric approximations trade a little accuracy for total reproducibility.
/// <see cref="Sin"/> is accurate to about 0.001 (roughly 0.06 degrees), which is far
/// below anything a soldier's facing needs to care about. In practice the hot paths
/// avoid trig entirely: facing is stored as a unit vector, so rotation is a multiply
/// and flank detection is a dot product.
/// </summary>
public static class FixMath
{
    // --------------------------------------------------------------- basics

    public static Fix Abs(Fix v) => v.Raw < 0 ? Fix.FromRaw(-v.Raw) : v;

    public static Fix Min(Fix a, Fix b) => a.Raw < b.Raw ? a : b;

    public static Fix Max(Fix a, Fix b) => a.Raw > b.Raw ? a : b;

    public static Fix Clamp(Fix v, Fix min, Fix max) =>
        v.Raw < min.Raw ? min : v.Raw > max.Raw ? max : v;

    public static Fix Clamp01(Fix v) =>
        v.Raw < 0 ? Fix.Zero : v.Raw > Fix.OneRaw ? Fix.One : v;

    public static int Sign(Fix v) => v.Raw < 0 ? -1 : v.Raw > 0 ? 1 : 0;

    public static Fix Floor(Fix v) => Fix.FromRaw(v.Raw & ~(Fix.OneRaw - 1));

    public static Fix Ceil(Fix v) => Floor(Fix.FromRaw(v.Raw + Fix.OneRaw - 1));

    public static Fix Round(Fix v) => Fix.FromInt(v.RoundToInt);

    /// <summary>Linear interpolation. <paramref name="t"/> is clamped to [0, 1].</summary>
    public static Fix Lerp(Fix a, Fix b, Fix t)
    {
        t = Clamp01(t);
        return a + (b - a) * t;
    }

    /// <summary>Linear interpolation without clamping — for extrapolation and render blending.</summary>
    public static Fix LerpUnclamped(Fix a, Fix b, Fix t) => a + (b - a) * t;

    /// <summary>Steps <paramref name="current"/> toward <paramref name="target"/> by at most <paramref name="maxDelta"/>.</summary>
    public static Fix MoveTowards(Fix current, Fix target, Fix maxDelta)
    {
        Fix delta = target - current;
        if (Abs(delta) <= maxDelta) return target;
        return current + (delta.Raw < 0 ? -maxDelta : maxDelta);
    }

    /// <summary>The Q32.32 square of a value, as a long. Used for radius tests without overflow.</summary>
    public static long SqrRaw(Fix v) => (long)v.Raw * v.Raw;

    // ----------------------------------------------------------------- roots

    /// <summary>
    /// Square root. Exact to the last representable bit for perfect squares and
    /// correctly truncated otherwise. Negative input returns zero rather than
    /// throwing, because a NaN has nowhere useful to go inside a battle tick.
    /// </summary>
    public static Fix Sqrt(Fix value)
    {
        if (value.Raw <= 0) return Fix.Zero;
        // sqrt(raw / 2^16) * 2^16 == sqrt(raw * 2^16), so shift left before rooting.
        return Fix.FromRaw((int)ISqrt64((ulong)value.Raw << Fix.FractionalBits));
    }

    /// <summary>
    /// Integer square root by the classic bit-by-bit (digit recurrence) method.
    /// No floating point anywhere, so the result is identical on every machine.
    /// </summary>
    public static ulong ISqrt64(ulong n)
    {
        if (n == 0) return 0;

        ulong result = 0;
        ulong bit = 1UL << 62;

        while (bit > n) bit >>= 2;

        while (bit != 0)
        {
            if (n >= result + bit)
            {
                n -= result + bit;
                result = (result >> 1) + bit;
            }
            else
            {
                result >>= 1;
            }
            bit >>= 2;
        }

        return result;
    }

    // ------------------------------------------------------------------ trig

    // sin(x) via the parabola-plus-refinement approximation:
    //     y = B*x + C*x*|x|          (max error ~0.056)
    //     y = P*(y*|y| - y) + y      (max error ~0.001)
    // with B = 4/pi, C = -4/pi^2, P = 0.225. Multiplies only, no division, no table.
    private static readonly Fix SinB = Fix.FromRaw(83443);    //  4/pi
    private static readonly Fix SinC = Fix.FromRaw(-26561);   // -4/pi^2
    private static readonly Fix SinP = Fix.FromRaw(14746);    //  0.225

    public static Fix Sin(Fix angleRadians)
    {
        // Wrap into [-pi, pi] with integer remainder — exact, no accumulation drift.
        int a = angleRadians.Raw % Fix.TwoPi.Raw;
        if (a > Fix.Pi.Raw) a -= Fix.TwoPi.Raw;
        else if (a < -Fix.Pi.Raw) a += Fix.TwoPi.Raw;

        Fix x = Fix.FromRaw(a);
        Fix y = SinB * x + SinC * (x * Abs(x));
        return SinP * (y * Abs(y) - y) + y;
    }

    public static Fix Cos(Fix angleRadians) => Sin(angleRadians + Fix.HalfPi);

    public static Fix Tan(Fix angleRadians)
    {
        Fix c = Cos(angleRadians);
        if (c.IsZero) return Fix.MaxValue;
        return Sin(angleRadians) / c;
    }

    // atan2 via the standard rational approximation, max error ~0.005 rad.
    private static readonly Fix AtanA = Fix.FromRaw(12866);    // 0.1963
    private static readonly Fix AtanB = Fix.FromRaw(64337);    // 0.9817
    private static readonly Fix QuarterPi = Fix.FromRaw(51472);
    private static readonly Fix ThreeQuarterPi = Fix.FromRaw(154415);

    public static Fix Atan2(Fix y, Fix x)
    {
        if (x.IsZero && y.IsZero) return Fix.Zero;

        Fix absY = Abs(y) + Fix.Epsilon;   // nudge off zero so the divisions are safe
        Fix r, angle;

        if (x.Raw >= 0)
        {
            r = (x - absY) / (x + absY);
            angle = QuarterPi;
        }
        else
        {
            r = (x + absY) / (absY - x);
            angle = ThreeQuarterPi;
        }

        angle += AtanA * (r * r * r) - AtanB * r;
        return y.Raw < 0 ? -angle : angle;
    }
}
