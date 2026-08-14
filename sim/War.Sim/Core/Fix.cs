namespace War.Sim.Core;

/// <summary>
/// Q16.16 signed fixed-point number: 16 bits of integer, 16 bits of fraction,
/// stored in a single <see cref="int"/>.
///
/// The entire simulation runs on this type rather than float or double. Floating
/// point is permitted by IEEE 754 to differ across compilers, architectures, and
/// optimisation levels, which would make replays, reproducible bug reports, and
/// lockstep multiplayer impossible. Integer arithmetic has no such freedom.
///
/// Range is roughly ±32768 with a precision of 1/65536 (~0.0000153). The
/// battlefield is about 1000 units across and a unit is a metre, so positions,
/// speeds, and combat values all sit comfortably inside that.
///
/// Rounding: multiplication rounds half-up, division truncates toward zero. Both
/// are exact, documented, and identical on every machine — which is the only
/// property that actually matters here.
/// </summary>
public readonly struct Fix : IEquatable<Fix>, IComparable<Fix>
{
    public const int FractionalBits = 16;
    public const int OneRaw = 1 << FractionalBits;      // 65536
    private const int HalfRaw = OneRaw >> 1;            // 32768
    private const int FractionMask = OneRaw - 1;

    /// <summary>The underlying scaled integer. Value = Raw / 65536.</summary>
    public readonly int Raw;

    private Fix(int raw) => Raw = raw;

    // ---------------------------------------------------------------- factories

    /// <summary>Wraps an already-scaled integer. Use when you know the raw encoding.</summary>
    public static Fix FromRaw(int raw) => new(raw);

    /// <summary>Exact conversion from a whole number. Overflows silently past ±32767.</summary>
    public static Fix FromInt(int value) => new(value << FractionalBits);

    /// <summary>
    /// Exact rational construction: <c>Ratio(3, 4)</c> is 0.75. This is the preferred
    /// way to author stat tables and tuning constants, because it never depends on
    /// how a decimal literal happens to be parsed.
    /// </summary>
    public static Fix Ratio(int numerator, int denominator) =>
        new((int)(((long)numerator << FractionalBits) / denominator));

    /// <summary>
    /// Authoring-time conversion from a double. Deterministic (IEEE 754 defines the
    /// multiply and the truncating cast exactly), but only ever call it while setting
    /// up a battle — never inside the tick loop.
    /// </summary>
    public static Fix FromDouble(double value) => new((int)(value * OneRaw));

    // --------------------------------------------------------------- constants

    public static readonly Fix Zero = new(0);
    public static readonly Fix One = new(OneRaw);
    public static readonly Fix Two = new(OneRaw * 2);
    public static readonly Fix Half = new(HalfRaw);
    public static readonly Fix Epsilon = new(1);
    public static readonly Fix MaxValue = new(int.MaxValue);
    public static readonly Fix MinValue = new(int.MinValue);

    // 3.14159265358979 * 65536 = 205887.416
    public static readonly Fix Pi = new(205887);
    public static readonly Fix TwoPi = new(411775);
    public static readonly Fix HalfPi = new(102944);
    public static readonly Fix Deg2Rad = new(1144);      // pi/180
    public static readonly Fix Rad2Deg = new(3754936);   // 180/pi

    // -------------------------------------------------------------- properties

    public bool IsZero => Raw == 0;
    public bool IsNegative => Raw < 0;

    /// <summary>Integer part, rounded toward negative infinity.</summary>
    public int FloorToInt => Raw >> FractionalBits;

    /// <summary>Integer part, rounded to nearest (half away from zero for positives).</summary>
    public int RoundToInt => (Raw + HalfRaw) >> FractionalBits;

    /// <summary>Fractional part in [0, 1).</summary>
    public Fix Fraction => new(Raw & FractionMask);

    // -------------------------------------------------------------- arithmetic

    public static Fix operator +(Fix a, Fix b) => new(a.Raw + b.Raw);
    public static Fix operator -(Fix a, Fix b) => new(a.Raw - b.Raw);
    public static Fix operator -(Fix a) => new(-a.Raw);

    /// <summary>
    /// Multiply with a 64-bit intermediate so the product never loses high bits,
    /// then round half-up back into Q16.16.
    /// </summary>
    public static Fix operator *(Fix a, Fix b) =>
        new((int)(((long)a.Raw * b.Raw + HalfRaw) >> FractionalBits));

    /// <summary>Divide with a 64-bit intermediate. Truncates toward zero.</summary>
    public static Fix operator /(Fix a, Fix b) =>
        new((int)(((long)a.Raw << FractionalBits) / b.Raw));

    public static Fix operator %(Fix a, Fix b) => new(a.Raw % b.Raw);

    // Integer overloads: shifting beats a full multiply, and these are extremely common.
    public static Fix operator *(Fix a, int b) => new(a.Raw * b);
    public static Fix operator *(int a, Fix b) => new(a * b.Raw);
    public static Fix operator /(Fix a, int b) => new(a.Raw / b);

    // ------------------------------------------------------------- comparisons

    public static bool operator ==(Fix a, Fix b) => a.Raw == b.Raw;
    public static bool operator !=(Fix a, Fix b) => a.Raw != b.Raw;
    public static bool operator <(Fix a, Fix b) => a.Raw < b.Raw;
    public static bool operator >(Fix a, Fix b) => a.Raw > b.Raw;
    public static bool operator <=(Fix a, Fix b) => a.Raw <= b.Raw;
    public static bool operator >=(Fix a, Fix b) => a.Raw >= b.Raw;

    /// <summary>Implicit widening from int. Convenient and exact within ±32767.</summary>
    public static implicit operator Fix(int value) => FromInt(value);

    // ------------------------------------------------------------ presentation

    /// <summary>Presentation only — never call this from simulation code.</summary>
    public float ToFloat() => Raw / (float)OneRaw;

    /// <summary>Presentation only — never call this from simulation code.</summary>
    public double ToDouble() => Raw / (double)OneRaw;

    // ---------------------------------------------------------------- plumbing

    public bool Equals(Fix other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is Fix other && Raw == other.Raw;
    public override int GetHashCode() => Raw;
    public int CompareTo(Fix other) => Raw.CompareTo(other.Raw);

    public override string ToString()
    {
        // Format by hand rather than via double, so debug output is exact.
        bool negative = Raw < 0;
        long magnitude = negative ? -(long)Raw : Raw;
        long whole = magnitude >> FractionalBits;
        long frac = ((magnitude & FractionMask) * 10000) >> FractionalBits;
        return $"{(negative ? "-" : "")}{whole}.{frac:D4}";
    }
}
