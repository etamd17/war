using War.Sim.Core;
using Xunit;

namespace War.Sim.Tests.Core;

/// <summary>
/// The whole simulation rests on these types, so they get checked hard. Tests are
/// free to use System.Math as a reference oracle — the ban on floating point applies
/// to the simulation, not to the thing measuring it.
/// </summary>
public class FixTests
{
    private const double Tolerance = 1.0 / 65536.0 * 2;   // two ulps of Q16.16

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(100)]
    [InlineData(-100)]
    [InlineData(32767)]
    [InlineData(-32767)]
    public void FromInt_RoundTrips(int value)
    {
        Assert.Equal(value, Fix.FromInt(value).ToDouble(), 6);
    }

    [Fact]
    public void One_TimesOne_IsExactlyOne()
    {
        Assert.Equal(Fix.One, Fix.One * Fix.One);
    }

    [Fact]
    public void Multiplication_ByOne_IsExactForArbitraryValues()
    {
        // Rounding must not drift on the identity operation, or every scaled stat
        // in the game would slowly decay.
        for (int raw = -1_000_000; raw <= 1_000_000; raw += 7919)
        {
            Fix v = Fix.FromRaw(raw);
            Assert.Equal(v, v * Fix.One);
        }
    }

    [Fact]
    public void Ratio_IsExact()
    {
        Assert.Equal(0.75, Fix.Ratio(3, 4).ToDouble(), 6);
        Assert.Equal(0.5, Fix.Ratio(1, 2).ToDouble(), 6);
        Assert.Equal(-0.25, Fix.Ratio(-1, 4).ToDouble(), 6);
        Assert.Equal(Fix.Half, Fix.Ratio(1, 2));
    }

    [Fact]
    public void Arithmetic_MatchesRealNumbers()
    {
        Fix a = Fix.Ratio(7, 2);      // 3.5
        Fix b = Fix.Ratio(5, 4);      // 1.25

        Assert.Equal(4.75, (a + b).ToDouble(), 4);
        Assert.Equal(2.25, (a - b).ToDouble(), 4);
        Assert.Equal(4.375, (a * b).ToDouble(), 4);
        Assert.Equal(2.8, (a / b).ToDouble(), 4);
        Assert.Equal(-3.5, (-a).ToDouble(), 4);
    }

    [Fact]
    public void Constants_AreAccurate()
    {
        Assert.Equal(Math.PI, Fix.Pi.ToDouble(), 4);
        Assert.Equal(Math.PI * 2, Fix.TwoPi.ToDouble(), 4);
        Assert.Equal(Math.PI / 2, Fix.HalfPi.ToDouble(), 4);
        Assert.Equal(Math.PI / 180, Fix.Deg2Rad.ToDouble(), 4);
    }

    [Fact]
    public void Comparisons_OrderCorrectly()
    {
        Fix small = Fix.Ratio(1, 100);
        Fix large = Fix.FromInt(50);

        Assert.True(small < large);
        Assert.True(large > small);
        Assert.True(small <= Fix.Ratio(1, 100));
        Assert.True(-large < small);
        Assert.Equal(-1, (-large).CompareTo(small));
    }

    [Theory]
    [InlineData(0, "0.0000")]
    [InlineData(Fix.OneRaw, "1.0000")]
    [InlineData(-Fix.OneRaw, "-1.0000")]
    [InlineData(Fix.OneRaw / 2, "0.5000")]
    [InlineData(-Fix.OneRaw / 2, "-0.5000")]
    public void ToString_FormatsWithoutFloatingPoint(int raw, string expected)
    {
        Assert.Equal(expected, Fix.FromRaw(raw).ToString());
    }

    [Fact]
    public void FloorAndRound_HandleNegatives()
    {
        Assert.Equal(1, Fix.Ratio(3, 2).FloorToInt);
        Assert.Equal(2, Fix.Ratio(3, 2).RoundToInt);
        Assert.Equal(-2, Fix.Ratio(-3, 2).FloorToInt);
        Assert.Equal(-1, Fix.Ratio(-3, 2).RoundToInt);
    }
}

public class FixMathTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(9)]
    [InlineData(16)]
    [InlineData(100)]
    [InlineData(10000)]
    public void Sqrt_IsExactForPerfectSquares(int square)
    {
        Fix result = FixMath.Sqrt(Fix.FromInt(square));
        Assert.Equal(Math.Sqrt(square), result.ToDouble(), 3);
    }

    [Fact]
    public void Sqrt_MatchesReferenceAcrossRange()
    {
        for (int i = 1; i <= 20000; i += 37)
        {
            Fix v = Fix.Ratio(i, 20);
            double expected = Math.Sqrt(v.ToDouble());
            double actual = FixMath.Sqrt(v).ToDouble();
            Assert.True(Math.Abs(expected - actual) < 0.001,
                $"sqrt({v}) was {actual}, expected {expected}");
        }
    }

    [Fact]
    public void Sqrt_OfNegative_ReturnsZeroRatherThanThrowing()
    {
        // A NaN inside a battle tick has nowhere useful to go.
        Assert.Equal(Fix.Zero, FixMath.Sqrt(Fix.FromInt(-5)));
    }

    [Fact]
    public void Sin_HitsTheCardinalAngles()
    {
        Assert.Equal(0.0, FixMath.Sin(Fix.Zero).ToDouble(), 3);
        Assert.Equal(1.0, FixMath.Sin(Fix.HalfPi).ToDouble(), 3);
        Assert.Equal(0.0, FixMath.Sin(Fix.Pi).ToDouble(), 3);
        Assert.Equal(-1.0, FixMath.Sin(-Fix.HalfPi).ToDouble(), 3);
    }

    [Fact]
    public void Sin_StaysWithinToleranceOverSeveralTurns()
    {
        // Also exercises the wrap: angles well outside [-pi, pi] must still be right.
        double worst = 0;
        for (int i = -2000; i <= 2000; i++)
        {
            Fix angle = Fix.Ratio(i, 100);
            double expected = Math.Sin(angle.ToDouble());
            double actual = FixMath.Sin(angle).ToDouble();
            worst = Math.Max(worst, Math.Abs(expected - actual));
        }
        Assert.True(worst < 0.002, $"worst sin error was {worst}");
    }

    [Fact]
    public void Cos_StaysWithinTolerance()
    {
        double worst = 0;
        for (int i = -2000; i <= 2000; i++)
        {
            Fix angle = Fix.Ratio(i, 100);
            double expected = Math.Cos(angle.ToDouble());
            double actual = FixMath.Cos(angle).ToDouble();
            worst = Math.Max(worst, Math.Abs(expected - actual));
        }
        Assert.True(worst < 0.002, $"worst cos error was {worst}");
    }

    [Fact]
    public void Atan2_CoversAllFourQuadrants()
    {
        double worst = 0;
        for (int y = -20; y <= 20; y++)
        {
            for (int x = -20; x <= 20; x++)
            {
                if (x == 0 && y == 0) continue;

                double expected = Math.Atan2(y, x);
                double actual = FixMath.Atan2(Fix.FromInt(y), Fix.FromInt(x)).ToDouble();

                // Normalise the wrap-around at +/-pi before comparing.
                double diff = Math.Abs(expected - actual);
                if (diff > Math.PI) diff = Math.Abs(diff - 2 * Math.PI);
                worst = Math.Max(worst, diff);
            }
        }
        Assert.True(worst < 0.02, $"worst atan2 error was {worst}");
    }

    [Fact]
    public void Clamp_And_Lerp_Behave()
    {
        Assert.Equal(Fix.FromInt(5), FixMath.Clamp(Fix.FromInt(9), Fix.Zero, Fix.FromInt(5)));
        Assert.Equal(Fix.Zero, FixMath.Clamp(Fix.FromInt(-9), Fix.Zero, Fix.FromInt(5)));
        Assert.Equal(Fix.One, FixMath.Clamp01(Fix.FromInt(4)));

        Assert.Equal(Fix.FromInt(5), FixMath.Lerp(Fix.Zero, Fix.FromInt(10), Fix.Half));
        Assert.Equal(Fix.FromInt(10), FixMath.Lerp(Fix.Zero, Fix.FromInt(10), Fix.FromInt(3)));
    }

    [Fact]
    public void MoveTowards_NeverOvershoots()
    {
        Fix current = Fix.Zero;
        for (int i = 0; i < 100; i++)
            current = FixMath.MoveTowards(current, Fix.FromInt(10), Fix.One);

        Assert.Equal(Fix.FromInt(10), current);
    }
}
