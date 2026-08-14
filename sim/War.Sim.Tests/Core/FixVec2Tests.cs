using War.Sim.Core;
using Xunit;

namespace War.Sim.Tests.Core;

public class FixVec2Tests
{
    [Fact]
    public void Magnitude_MatchesPythagoras()
    {
        var v = new FixVec2(Fix.FromInt(3), Fix.FromInt(4));
        Assert.Equal(5.0, v.Magnitude.ToDouble(), 3);
    }

    [Fact]
    public void Magnitude_SurvivesFullBattlefieldDistances()
    {
        // The critical overflow case: squaring a 1000-unit distance blows past Q16.16,
        // so Magnitude must root the 64-bit intermediate rather than a Fix.
        var far = new FixVec2(Fix.FromInt(1000), Fix.FromInt(1000));
        Assert.Equal(Math.Sqrt(2_000_000), far.Magnitude.ToDouble(), 1);

        var corner = new FixVec2(Fix.FromInt(1400), Fix.FromInt(1400));
        Assert.True(corner.Magnitude.ToDouble() > 1979.0);
        Assert.True(corner.Magnitude.ToDouble() < 1980.0);
    }

    [Fact]
    public void Normalized_ProducesUnitLength()
    {
        for (int i = 1; i <= 360; i += 7)
        {
            var v = new FixVec2(Fix.FromInt(i), Fix.FromInt(360 - i));
            double length = v.Normalized.Magnitude.ToDouble();
            Assert.True(Math.Abs(length - 1.0) < 0.001, $"length was {length} for {v}");
        }
    }

    [Fact]
    public void Normalized_OfZero_IsZero()
    {
        Assert.Equal(FixVec2.Zero, FixVec2.Zero.Normalized);
    }

    [Fact]
    public void Dot_DetectsFacing()
    {
        // This is exactly how flank detection works: positive dot means the attack
        // is coming from in front, negative means from behind.
        Assert.True(FixVec2.Dot(FixVec2.North, FixVec2.North) > Fix.Zero);
        Assert.True(FixVec2.Dot(FixVec2.North, -FixVec2.North) < Fix.Zero);
        Assert.Equal(Fix.Zero, FixVec2.Dot(FixVec2.North, FixVec2.East));
    }

    [Fact]
    public void RightAndLeft_AreConsistentWithCompass()
    {
        // Facing north, your right hand points east.
        Assert.Equal(FixVec2.East, FixVec2.North.Right);
        Assert.Equal(-FixVec2.East, FixVec2.North.Left);
    }

    [Fact]
    public void Rotate_PlacesFormationSlotsCorrectly()
    {
        // A slot two units ahead of a unit facing north must end up two units north.
        var localOffset = new FixVec2(Fix.Zero, Fix.FromInt(2));
        FixVec2 world = localOffset.Rotate(FixVec2.North);

        Assert.Equal(-2.0, world.X.ToDouble(), 3);
        Assert.Equal(0.0, world.Y.ToDouble(), 3);

        // Facing east (the identity rotation) the offset is unchanged.
        Assert.Equal(localOffset, localOffset.Rotate(FixVec2.East));
    }

    [Fact]
    public void WithinDistance_IsExactAtTheBoundary()
    {
        var a = FixVec2.Zero;
        var b = new FixVec2(Fix.FromInt(3), Fix.FromInt(4));

        Assert.True(FixVec2.WithinDistance(a, b, Fix.FromInt(5)));
        Assert.False(FixVec2.WithinDistance(a, b, Fix.Ratio(499, 100)));
    }

    [Fact]
    public void WithinDistance_DoesNotOverflowAtLongRange()
    {
        var a = FixVec2.Zero;
        var b = new FixVec2(Fix.FromInt(900), Fix.FromInt(900));

        Assert.False(FixVec2.WithinDistance(a, b, Fix.FromInt(100)));
        Assert.True(FixVec2.WithinDistance(a, b, Fix.FromInt(1300)));
    }

    [Fact]
    public void MoveTowards_ArrivesWithoutOvershooting()
    {
        var current = FixVec2.Zero;
        var target = new FixVec2(Fix.FromInt(10), Fix.FromInt(10));

        for (int i = 0; i < 200; i++)
            current = FixVec2.MoveTowards(current, target, Fix.Half);

        Assert.Equal(target, current);
    }

    [Fact]
    public void TurnTowards_ConvergesAndStaysUnitLength()
    {
        FixVec2 facing = FixVec2.North;
        FixVec2 desired = FixVec2.East;

        for (int i = 0; i < 60; i++)
        {
            facing = FixVec2.TurnTowards(facing, desired, Fix.Ratio(1, 5));
            double length = facing.Magnitude.ToDouble();
            Assert.True(Math.Abs(length - 1.0) < 0.01, $"facing drifted to length {length}");
        }

        Assert.True(FixVec2.Dot(facing, desired).ToDouble() > 0.99);
    }

    [Fact]
    public void TurnTowards_HandlesTheExactReversal()
    {
        // Blending a vector toward its exact opposite can land on zero; the soldier
        // must keep pivoting rather than freeze with no facing at all.
        FixVec2 facing = FixVec2.North;
        for (int i = 0; i < 40; i++)
        {
            facing = FixVec2.TurnTowards(facing, -FixVec2.North, Fix.Ratio(1, 4));
            Assert.False(facing.IsZero);
        }
        Assert.True(FixVec2.Dot(facing, -FixVec2.North).ToDouble() > 0.9);
    }

    [Fact]
    public void ClampMagnitude_LimitsSpeed()
    {
        var v = new FixVec2(Fix.FromInt(30), Fix.FromInt(40));   // length 50
        FixVec2 clamped = v.ClampMagnitude(Fix.FromInt(10));

        Assert.Equal(10.0, clamped.Magnitude.ToDouble(), 2);
        Assert.Equal(v.Normalized.X.ToDouble(), clamped.Normalized.X.ToDouble(), 2);

        // Already-short vectors pass through untouched.
        var slow = new FixVec2(Fix.One, Fix.Zero);
        Assert.Equal(slow, slow.ClampMagnitude(Fix.FromInt(10)));
    }
}

public class DetRandomTests
{
    [Fact]
    public void SameSeed_ProducesIdenticalSequences()
    {
        var a = new DetRandom(12345, RngStream.Melee);
        var b = new DetRandom(12345, RngStream.Melee);

        for (int i = 0; i < 10000; i++)
            Assert.Equal(a.NextUInt(), b.NextUInt());
    }

    [Fact]
    public void DifferentStreams_Diverge()
    {
        // The point of separate streams: adding a roll to the missile code must not
        // shift what the melee code rolls.
        var melee = new DetRandom(999, RngStream.Melee);
        var missile = new DetRandom(999, RngStream.Missile);

        int identical = 0;
        for (int i = 0; i < 1000; i++)
            if (melee.NextUInt() == missile.NextUInt()) identical++;

        Assert.True(identical < 5, $"{identical} collisions suggests the streams are correlated");
    }

    [Fact]
    public void NextFix_StaysInUnitInterval()
    {
        var rng = new DetRandom(7, RngStream.Morale);
        for (int i = 0; i < 20000; i++)
        {
            Fix v = rng.NextFix();
            Assert.True(v >= Fix.Zero && v < Fix.One, $"drew {v}");
        }
    }

    [Fact]
    public void NextFix_IsRoughlyUniform()
    {
        var rng = new DetRandom(31337, RngStream.Melee);
        int[] buckets = new int[10];
        const int samples = 100_000;

        for (int i = 0; i < samples; i++)
            buckets[(rng.NextFix() * 10).FloorToInt]++;

        foreach (int count in buckets)
            Assert.InRange(count, samples / 10 - samples / 50, samples / 10 + samples / 50);
    }

    [Fact]
    public void NextInt_RespectsBounds()
    {
        var rng = new DetRandom(42, RngStream.Ai);
        for (int i = 0; i < 20000; i++)
            Assert.InRange(rng.NextInt(5, 12), 5, 11);
    }

    [Fact]
    public void Chance_ApproximatesTheStatedProbability()
    {
        var rng = new DetRandom(2024, RngStream.Melee);
        int hits = 0;
        const int trials = 100_000;

        for (int i = 0; i < trials; i++)
            if (rng.Chance(Fix.Ratio(1, 4))) hits++;

        Assert.InRange(hits / (double)trials, 0.24, 0.26);
    }

    [Fact]
    public void NextDirection_IsAlwaysUnitLength()
    {
        var rng = new DetRandom(555, RngStream.Missile);
        for (int i = 0; i < 5000; i++)
        {
            double length = rng.NextDirection().Magnitude.ToDouble();
            Assert.True(Math.Abs(length - 1.0) < 0.01, $"direction length was {length}");
        }
    }

    [Fact]
    public void NextSpread_ClustersAroundZero()
    {
        var rng = new DetRandom(808, RngStream.Missile);
        int near = 0;
        const int trials = 20000;

        for (int i = 0; i < trials; i++)
            if (FixMath.Abs(rng.NextSpread()) < Fix.Ratio(1, 3)) near++;

        // A flat distribution would put a third inside the middle third; a bell shape
        // should be well above that.
        Assert.True(near / (double)trials > 0.45, $"only {near / (double)trials:P0} landed near centre");
    }

    [Fact]
    public void SaveAndRestore_ReproducesTheStream()
    {
        var rng = new DetRandom(1, RngStream.Rout);
        for (int i = 0; i < 50; i++) rng.NextUInt();

        DetRandom.State checkpoint = rng.Save();
        uint[] expected = new uint[100];
        for (int i = 0; i < expected.Length; i++) expected[i] = rng.NextUInt();

        rng.Restore(checkpoint);
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], rng.NextUInt());
    }

    [Fact]
    public void Shuffle_IsDeterministicAndPreservesContents()
    {
        int[] Make() => Enumerable.Range(0, 100).ToArray();

        var first = Make();
        var second = Make();

        new DetRandom(77, RngStream.Setup).Shuffle(first);
        new DetRandom(77, RngStream.Setup).Shuffle(second);

        Assert.Equal(first, second);
        Assert.Equal(Make().OrderBy(x => x), first.OrderBy(x => x));
        Assert.NotEqual(Make(), first);
    }
}
