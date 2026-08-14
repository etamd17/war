using War.Sim.Core;
using War.Sim.Units;
using Xunit;

namespace War.Sim.Tests.Units;

public class FormationTests
{
    private static readonly Fix File = Fix.Ratio(8, 10);
    private static readonly Fix Rank = Fix.One;

    private static FixVec2 Slot(FormationType type, int slot, int width, int strength) =>
        Formations.SlotOffset(type, slot, width, strength, File, Rank);

    [Fact]
    public void Line_PutsTheFrontRankForwardAndDeeperRanksBehind()
    {
        // Local +X is forward, so rank 0 sits at X = 0 and each rank behind is negative.
        Assert.Equal(0.0, Slot(FormationType.Line, 0, 10, 100).X.ToDouble(), 3);
        Assert.True(Slot(FormationType.Line, 10, 10, 100).X < Fix.Zero);
        Assert.True(Slot(FormationType.Line, 20, 10, 100).X < Slot(FormationType.Line, 10, 10, 100).X);
    }

    [Fact]
    public void Line_IsCentredOnTheUnitAxis()
    {
        // The leftmost and rightmost men of a rank must be mirror images, or the unit
        // would drift sideways every time it re-formed.
        FixVec2 leftmost = Slot(FormationType.Line, 0, 10, 100);
        FixVec2 rightmost = Slot(FormationType.Line, 9, 10, 100);

        Assert.Equal(leftmost.Y.ToDouble(), -rightmost.Y.ToDouble(), 3);
        Assert.True(leftmost.Y > Fix.Zero, "file 0 should be on the left, which is +Y");
    }

    [Fact]
    public void Line_CentresARaggedLastRank()
    {
        // 100 men at width 12 leaves a last rank of 4. It must sit in the middle of the
        // block, not hang off the left edge like a comb with missing teeth.
        const int width = 12, strength = 100;
        int lastRankStart = strength / width * width;     // 96

        FixVec2 left = Slot(FormationType.Line, lastRankStart, width, strength);
        FixVec2 right = Slot(FormationType.Line, strength - 1, width, strength);

        Assert.Equal(left.Y.ToDouble(), -right.Y.ToDouble(), 3);
        Assert.True(left.Y < Slot(FormationType.Line, 0, width, strength).Y,
            "the short rank should be narrower than a full one");
    }

    [Fact]
    public void Line_GivesEveryManADistinctPlace()
    {
        var seen = new HashSet<(int, int)>();
        for (int i = 0; i < 120; i++)
        {
            FixVec2 offset = Slot(FormationType.Line, i, 20, 120);
            Assert.True(seen.Add((offset.X.Raw, offset.Y.Raw)), $"slot {i} collided with another");
        }
    }

    [Fact]
    public void Width_ControlsFrontageDirectly()
    {
        Fix narrow = Formations.HalfFrontage(FormationType.Line, 8, 120, File);
        Fix wide = Formations.HalfFrontage(FormationType.Line, 30, 120, File);

        Assert.True(wide > narrow);
        // Thirty files at 0.8 m spacing spans 23.2 m, so half of it is 11.6 m.
        Assert.Equal(11.6, wide.ToDouble(), 1);
    }

    [Fact]
    public void Wedge_IsATriangleWithASingleManAtTheTip()
    {
        // Rank r holds r+1 men. The tip is one rider, and everything behind funnels
        // into the hole he makes.
        Assert.Equal(0.0, Slot(FormationType.Wedge, 0, 8, 36).X.ToDouble(), 3);
        Assert.Equal(0.0, Slot(FormationType.Wedge, 0, 8, 36).Y.ToDouble(), 3);

        // Slots 1 and 2 form the second rank: same depth, mirrored across the axis.
        FixVec2 a = Slot(FormationType.Wedge, 1, 8, 36);
        FixVec2 b = Slot(FormationType.Wedge, 2, 8, 36);
        Assert.Equal(a.X.ToDouble(), b.X.ToDouble(), 3);
        Assert.Equal(a.Y.ToDouble(), -b.Y.ToDouble(), 3);
        Assert.True(a.X < Fix.Zero);

        // Slot 3 starts the third rank, deeper again.
        Assert.True(Slot(FormationType.Wedge, 3, 8, 36).X < a.X);
    }

    [Fact]
    public void Wedge_WidensAsItDeepens()
    {
        Fix widest = Fix.Zero;
        for (int i = 0; i < 36; i++)
        {
            Fix lateral = FixMath.Abs(Slot(FormationType.Wedge, i, 8, 36).Y);
            if (lateral > widest) widest = lateral;
        }
        Assert.True(widest > File * 3, "the base of a 36-man wedge should be several files wide");
    }

    [Fact]
    public void Square_IsAsDeepAsItIsWide()
    {
        int side = Formations.DefaultWidth(FormationType.Square, 100);
        Assert.Equal(10, side);

        Fix maxLateral = Fix.Zero, maxDepth = Fix.Zero;
        for (int i = 0; i < 100; i++)
        {
            FixVec2 offset = Slot(FormationType.Square, i, side, 100);
            maxLateral = FixMath.Max(maxLateral, FixMath.Abs(offset.Y));
            maxDepth = FixMath.Max(maxDepth, FixMath.Abs(offset.X));
        }

        // Square uses the same scale on both axes, so the block is symmetric about its
        // centre — there is no front rank to find and no flank to turn.
        Assert.True(maxDepth > Fix.Zero && maxLateral > Fix.Zero);
        Assert.Equal(maxDepth.ToDouble() / Rank.ToDouble(), maxLateral.ToDouble() / File.ToDouble(), 1);
    }

    [Fact]
    public void Square_DefendsAllRound()
    {
        Assert.True(Formations.Profile(FormationType.Square).AllRoundDefence);
        Assert.True(Formations.Profile(FormationType.Square).FlankDefenceBonus > 0);
        Assert.False(Formations.Profile(FormationType.Line).AllRoundDefence);
    }

    [Fact]
    public void Testudo_PacksTightAndSkirmishSpreadsOut()
    {
        Fix testudo = Formations.Profile(FormationType.Testudo).FileSpacingScale;
        Fix line = Formations.Profile(FormationType.Line).FileSpacingScale;
        Fix skirmish = Formations.Profile(FormationType.Skirmish).FileSpacingScale;

        Assert.True(testudo < line);
        Assert.True(skirmish > line);

        // And the point of each: testudo shrugs off arrows, skirmish order halves them.
        Assert.True(Formations.Profile(FormationType.Testudo).MissileVulnerability < Fix.Ratio(2, 10));
        Assert.True(Formations.Profile(FormationType.Skirmish).MissileVulnerability < line);
    }

    [Fact]
    public void Testudo_TradesFightingAbilityForCover()
    {
        FormationProfile testudo = Formations.Profile(FormationType.Testudo);
        Assert.True(testudo.AttackBonus < 0);
        Assert.True(testudo.SpeedScale < Fix.Half);
        Assert.Equal(Fix.Zero, testudo.ChargeScale);
    }

    [Fact]
    public void Phalanx_IsAWallInFrontAndADoorOnTheFlank()
    {
        FormationProfile phalanx = Formations.Profile(FormationType.Phalanx);

        Assert.True(phalanx.FrontDefenceBonus > 0);
        Assert.True(phalanx.FlankDefenceBonus < 0);
        Assert.True(phalanx.NegatesFrontalCharge, "the entire point of standing in a phalanx");
        Assert.True(phalanx.ExtraReach > Fix.Zero, "levelled pikes engage a rank early");
        Assert.True(phalanx.SpeedScale < Fix.One);
    }

    [Fact]
    public void Wedge_ConcentratesTheCharge()
    {
        Assert.True(Formations.Profile(FormationType.Wedge).ChargeScale > Fix.One);
        Assert.True(Formations.Profile(FormationType.Wedge).FlankDefenceBonus < 0);
    }

    [Fact]
    public void DefaultWidth_ProducesTheProfileDepth()
    {
        int width = Formations.DefaultWidth(FormationType.Line, 120);
        int depth = Formations.Profile(FormationType.Line).PreferredDepth;
        Assert.Equal(120 / depth, width);

        // A phalanx defaults deeper than a line, which is what makes it a phalanx.
        Assert.True(Formations.DefaultWidth(FormationType.Phalanx, 120) <
                    Formations.DefaultWidth(FormationType.Line, 120));
    }

    [Fact]
    public void DefaultWidth_SurvivesDegenerateStrengths()
    {
        foreach (FormationType type in Enum.GetValues<FormationType>())
        {
            Assert.True(Formations.DefaultWidth(type, 0) >= 1);
            Assert.True(Formations.DefaultWidth(type, 1) >= 1);
            Assert.True(Formations.HalfFrontage(type, 0, 0, File) >= Fix.Zero);
        }
    }

    [Fact]
    public void SlotOffsets_RotateWithTheUnitFacing()
    {
        // A formation pivots for free: rotate every local offset by the facing.
        // Front-and-centre of a north-facing line must be north of its centre.
        FixVec2 centre = new(Fix.FromInt(100), Fix.FromInt(100));
        FixVec2 front = Slot(FormationType.Line, 4, 10, 100);
        FixVec2 rear = Slot(FormationType.Line, 94, 10, 100);

        FixVec2 frontWorld = centre + front.Rotate(FixVec2.North);
        FixVec2 rearWorld = centre + rear.Rotate(FixVec2.North);

        Assert.True(frontWorld.Y > rearWorld.Y, "the front rank should be further north than the rear");
    }

    [Fact]
    public void FormationMask_MapsToItsType()
    {
        foreach (FormationType type in Enum.GetValues<FormationType>())
            Assert.Equal((FormationMask)(1 << (int)type), type.ToMask());

        Assert.True((FormationMask.Legionary & FormationType.Testudo.ToMask()) != 0);
        Assert.True((FormationMask.Hoplite & FormationType.Phalanx.ToMask()) != 0);
        Assert.Equal(FormationMask.None, FormationMask.Horse & FormationType.Phalanx.ToMask());
    }
}
