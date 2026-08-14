using War.Sim.Core;
using War.Sim.World;
using Xunit;

namespace War.Sim.Tests.World;

public class TerrainTests
{
    private static Terrain Flat(int resolution = 33, int size = 256)
    {
        var terrain = new Terrain(resolution, Fix.FromInt(size));
        terrain.RebuildGradients();
        return terrain;
    }

    /// <summary>A hill rising to the east: height equals x, so the gradient is exactly (1, 0).</summary>
    private static Terrain EastwardSlope(int resolution = 33, int size = 256)
    {
        var terrain = new Terrain(resolution, Fix.FromInt(size));
        for (int y = 0; y < resolution; y++)
            for (int x = 0; x < resolution; x++)
                terrain.SetHeight(x, y, Fix.FromInt(x) * terrain.CellSize);
        terrain.RebuildGradients();
        return terrain;
    }

    [Fact]
    public void Generation_IsDeterministic()
    {
        var settings = new BattlefieldSettings { Seed = 4471, River = true };
        Terrain a = TerrainGenerator.Generate(settings);
        Terrain b = TerrainGenerator.Generate(settings);

        for (int y = 0; y < settings.Resolution; y++)
            for (int x = 0; x < settings.Resolution; x++)
            {
                Assert.Equal(a.GetHeight(x, y), b.GetHeight(x, y));
                Assert.Equal(a.GetForest(x, y), b.GetForest(x, y));
            }
    }

    [Fact]
    public void DifferentSeeds_ProduceDifferentMaps()
    {
        Terrain a = TerrainGenerator.Generate(new BattlefieldSettings { Seed = 1 });
        Terrain b = TerrainGenerator.Generate(new BattlefieldSettings { Seed = 2 });

        int differences = 0;
        for (int y = 0; y < 129; y++)
            for (int x = 0; x < 129; x++)
                if (a.GetHeight(x, y) != b.GetHeight(x, y)) differences++;

        Assert.True(differences > 10000, $"only {differences} samples differed");
    }

    [Fact]
    public void CentralRidge_IsActuallyTheHighGround()
    {
        Terrain terrain = TerrainGenerator.Generate(
            new BattlefieldSettings { Seed = 9, CentralRidge = true, Hilliness = Fix.One });

        Fix half = terrain.Size / 2;
        Fix crest = terrain.HeightAt(new FixVec2(half, half));
        Fix southEdge = terrain.HeightAt(new FixVec2(half, Fix.FromInt(20)));
        Fix northEdge = terrain.HeightAt(new FixVec2(half, terrain.Size - Fix.FromInt(20)));

        Assert.True(crest > southEdge, "ridge crest should stand above the south approach");
        Assert.True(crest > northEdge, "ridge crest should stand above the north approach");
    }

    [Fact]
    public void HeightAt_InterpolatesBetweenSamples()
    {
        Terrain terrain = EastwardSlope();

        // Height equals the x coordinate, so a sample halfway between grid points
        // must land halfway between their heights.
        Fix mid = terrain.CellSize / 2;
        Assert.Equal(mid.ToDouble(), terrain.HeightAt(new FixVec2(mid, Fix.FromInt(40))).ToDouble(), 2);
        Assert.Equal(100.0, terrain.HeightAt(new FixVec2(Fix.FromInt(100), Fix.FromInt(40))).ToDouble(), 1);
    }

    [Fact]
    public void HeightAt_ClampsOutsideTheMapRatherThanWrapping()
    {
        Terrain terrain = EastwardSlope();
        Fix inside = terrain.HeightAt(new FixVec2(Fix.Zero, Fix.Zero));
        Fix outside = terrain.HeightAt(new FixVec2(Fix.FromInt(-500), Fix.FromInt(-500)));
        Assert.Equal(inside, outside);
    }

    [Fact]
    public void GradientAt_PointsUphill()
    {
        Terrain terrain = EastwardSlope();
        FixVec2 gradient = terrain.GradientAt(new FixVec2(Fix.FromInt(100), Fix.FromInt(100)));

        Assert.Equal(1.0, gradient.X.ToDouble(), 2);
        Assert.Equal(0.0, gradient.Y.ToDouble(), 2);
    }

    [Fact]
    public void MovingUphill_IsSlowerThanDownhill()
    {
        Terrain terrain = EastwardSlope();
        var here = new FixVec2(Fix.FromInt(100), Fix.FromInt(100));

        Fix uphill = terrain.SpeedMultiplierAt(here, FixVec2.East);
        Fix downhill = terrain.SpeedMultiplierAt(here, -FixVec2.East);
        Fix across = terrain.SpeedMultiplierAt(here, FixVec2.North);

        Assert.True(uphill < across, "climbing should be slower than moving along the contour");
        Assert.True(across < downhill, "descending should be quicker than moving along the contour");
    }

    [Fact]
    public void ClimbingTiresMenFasterThanDescending()
    {
        Terrain terrain = EastwardSlope();
        var here = new FixVec2(Fix.FromInt(100), Fix.FromInt(100));

        Assert.True(terrain.FatigueMultiplierAt(here, FixVec2.East) >
                    terrain.FatigueMultiplierAt(here, -FixVec2.East));
    }

    [Fact]
    public void Mud_SlowsAndTires()
    {
        var terrain = new Terrain(9, Fix.FromInt(64));
        for (int y = 0; y < 9; y++)
            for (int x = 0; x < 9; x++)
                terrain.SetGround(x, y, x < 4 ? GroundType.Mud : GroundType.Grass);
        terrain.RebuildGradients();

        var inMud = new FixVec2(Fix.FromInt(8), Fix.FromInt(32));
        var onGrass = new FixVec2(Fix.FromInt(56), Fix.FromInt(32));

        Assert.True(terrain.SpeedMultiplierAt(inMud, FixVec2.North) <
                    terrain.SpeedMultiplierAt(onGrass, FixVec2.North));
        Assert.True(terrain.FatigueMultiplierAt(inMud, FixVec2.North) >
                    terrain.FatigueMultiplierAt(onGrass, FixVec2.North));
    }

    [Fact]
    public void LineOfSight_IsClearOverFlatOpenGround()
    {
        Terrain terrain = Flat();
        Assert.True(terrain.HasLineOfSight(
            new FixVec2(Fix.FromInt(20), Fix.FromInt(128)),
            new FixVec2(Fix.FromInt(230), Fix.FromInt(128)),
            Fix.Ratio(17, 10)));
    }

    [Fact]
    public void LineOfSight_IsBlockedByARidgeBetween()
    {
        var terrain = new Terrain(33, Fix.FromInt(256));
        for (int y = 0; y < 33; y++)
            for (int x = 0; x < 33; x++)
                terrain.SetHeight(x, y, x == 16 ? Fix.FromInt(40) : Fix.Zero);
        terrain.RebuildGradients();

        Assert.False(terrain.HasLineOfSight(
            new FixVec2(Fix.FromInt(20), Fix.FromInt(128)),
            new FixVec2(Fix.FromInt(230), Fix.FromInt(128)),
            Fix.Ratio(17, 10)));
    }

    [Fact]
    public void LineOfSight_IsBlockedByDeepWoods()
    {
        var terrain = new Terrain(33, Fix.FromInt(256));
        for (int y = 0; y < 33; y++)
            for (int x = 0; x < 33; x++)
                terrain.SetForest(x, y, 255);
        terrain.RebuildGradients();

        var near = new FixVec2(Fix.FromInt(120), Fix.FromInt(128));
        var justAhead = new FixVec2(Fix.FromInt(128), Fix.FromInt(128));
        var farOff = new FixVec2(Fix.FromInt(240), Fix.FromInt(128));

        // Close enough to see through a few metres of trees, but not across the wood.
        Assert.True(terrain.HasLineOfSight(near, justAhead, Fix.Ratio(17, 10)));
        Assert.False(terrain.HasLineOfSight(near, farOff, Fix.Ratio(17, 10)));
    }

    [Fact]
    public void HeightAdvantage_FavoursTheHigherPosition()
    {
        Terrain terrain = EastwardSlope();
        var high = new FixVec2(Fix.FromInt(200), Fix.FromInt(100));
        var low = new FixVec2(Fix.FromInt(50), Fix.FromInt(100));

        Assert.True(terrain.HeightAdvantage(high, low) > Fix.Zero);
        Assert.True(terrain.HeightAdvantage(low, high) < Fix.Zero);
    }

    [Fact]
    public void DistanceOutsideBounds_DetectsRoutersLeavingTheField()
    {
        Terrain terrain = Flat(33, 256);

        Assert.Equal(Fix.Zero, terrain.DistanceOutsideBounds(new FixVec2(Fix.FromInt(128), Fix.FromInt(128))));
        Assert.Equal(20.0, terrain.DistanceOutsideBounds(new FixVec2(Fix.FromInt(-20), Fix.FromInt(128))).ToDouble(), 2);
        Assert.Equal(30.0, terrain.DistanceOutsideBounds(new FixVec2(Fix.FromInt(286), Fix.FromInt(128))).ToDouble(), 2);
    }

    [Fact]
    public void River_CutsAFordThroughTheBanks()
    {
        Terrain terrain = TerrainGenerator.Generate(
            new BattlefieldSettings { Seed = 33, River = true, Resolution = 129 });

        int fordSamples = 0;
        for (int y = 0; y < 129; y++)
            for (int x = 0; x < 129; x++)
            {
                var at = new FixVec2(Fix.FromInt(x) * terrain.CellSize, Fix.FromInt(y) * terrain.CellSize);
                if (terrain.GroundAt(at) == GroundType.Ford) fordSamples++;
            }

        Assert.True(fordSamples > 0, "a river with no crossing is just a wall");
    }
}
