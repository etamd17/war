using War.Sim.Core;
using War.Sim.World;
using Xunit;

namespace War.Sim.Tests.World;

public class SpatialHashTests
{
    private static (SpatialHash hash, FixVec2[] positions, int[] ids) Populate(
        int count, uint seed, int worldSize = 512, int cellSize = 4)
    {
        var rng = new DetRandom(seed, RngStream.Setup);
        var positions = new FixVec2[count];
        var ids = new int[count];

        for (int i = 0; i < count; i++)
        {
            positions[i] = new FixVec2(
                rng.NextFix() * Fix.FromInt(worldSize),
                rng.NextFix() * Fix.FromInt(worldSize));
            ids[i] = i;
        }

        var hash = new SpatialHash(Fix.FromInt(worldSize), Fix.FromInt(cellSize), count);
        hash.Build(ids, positions);
        return (hash, positions, ids);
    }

    private static List<int> BruteForce(FixVec2[] positions, FixVec2 centre, Fix radius)
    {
        var found = new List<int>();
        for (int i = 0; i < positions.Length; i++)
            if (FixVec2.WithinDistance(centre, positions[i], radius)) found.Add(i);
        return found;
    }

    [Fact]
    public void Query_MatchesBruteForceExactly()
    {
        var (hash, positions, _) = Populate(2400, 1234);
        var results = new int[2400];
        var rng = new DetRandom(99, RngStream.Melee);

        for (int trial = 0; trial < 200; trial++)
        {
            var centre = new FixVec2(rng.NextFix() * 512, rng.NextFix() * 512);
            Fix radius = rng.NextFix(Fix.One, Fix.FromInt(30));

            int count = hash.Query(centre, radius, positions, results);
            List<int> expected = BruteForce(positions, centre, radius);

            Assert.Equal(expected.Count, count);
            Assert.Equal(expected.OrderBy(x => x), results.Take(count).OrderBy(x => x));
        }
    }

    [Fact]
    public void Query_ReturnsIdsInDeterministicOrder()
    {
        // Combat pairing walks query results, so their order is part of the simulation
        // state — it has to be reproducible, not merely correct as a set.
        var (hashA, positionsA, _) = Populate(1500, 77);
        var (hashB, positionsB, _) = Populate(1500, 77);

        var bufferA = new int[1500];
        var bufferB = new int[1500];
        var centre = new FixVec2(Fix.FromInt(256), Fix.FromInt(256));

        int countA = hashA.Query(centre, Fix.FromInt(40), positionsA, bufferA);
        int countB = hashB.Query(centre, Fix.FromInt(40), positionsB, bufferB);

        Assert.Equal(countA, countB);
        Assert.Equal(bufferA.Take(countA), bufferB.Take(countB));
    }

    [Fact]
    public void Rebuild_ReusesStorageAndStaysCorrect()
    {
        var (hash, positions, ids) = Populate(800, 5);
        var results = new int[800];
        var rng = new DetRandom(6, RngStream.Setup);

        for (int tick = 0; tick < 30; tick++)
        {
            // Jiggle everyone the way a tick of movement would, then reindex.
            for (int i = 0; i < positions.Length; i++)
                positions[i] += rng.NextDirection() * Fix.Half;

            hash.Build(ids, positions);

            var centre = new FixVec2(Fix.FromInt(100), Fix.FromInt(100));
            int count = hash.Query(centre, Fix.FromInt(25), positions, results);
            Assert.Equal(BruteForce(positions, centre, Fix.FromInt(25)).Count, count);
        }
    }

    [Fact]
    public void Build_ExcludesEntriesTheCallerLeavesOut()
    {
        // This is how corpses stay out of the index instead of slowing every query
        // down as casualties pile up.
        var (_, positions, _) = Populate(500, 21);

        var living = new List<int>();
        for (int i = 0; i < positions.Length; i += 2) living.Add(i);

        var livingPositions = living.Select(i => positions[i]).ToArray();
        var hash = new SpatialHash(Fix.FromInt(512), Fix.FromInt(4), 500);
        hash.Build(living.ToArray(), livingPositions);

        Assert.Equal(living.Count, hash.Count);

        var results = new int[500];
        int found = hash.Query(new FixVec2(Fix.FromInt(256), Fix.FromInt(256)), Fix.FromInt(600), positions, results);

        Assert.Equal(living.Count, found);
        Assert.All(results.Take(found), id => Assert.True(id % 2 == 0));
    }

    [Fact]
    public void Query_TruncatesRatherThanOverrunningTheBuffer()
    {
        var (hash, positions, _) = Populate(2000, 8);
        var tiny = new int[10];

        int found = hash.Query(new FixVec2(Fix.FromInt(256), Fix.FromInt(256)), Fix.FromInt(500), positions, tiny);
        Assert.Equal(10, found);
    }

    [Fact]
    public void FindNearest_MatchesBruteForce()
    {
        var (hash, positions, _) = Populate(1200, 4242);
        var rng = new DetRandom(17, RngStream.Ai);

        for (int trial = 0; trial < 100; trial++)
        {
            var centre = new FixVec2(rng.NextFix() * 512, rng.NextFix() * 512);
            Fix radius = Fix.FromInt(60);

            int actual = hash.FindNearest(centre, radius, positions);

            int expected = -1;
            long bestSqr = FixMath.SqrRaw(radius);
            for (int i = 0; i < positions.Length; i++)
            {
                long sqr = FixVec2.SqrDistanceRaw(centre, positions[i]);
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                expected = i;
            }

            if (expected < 0)
            {
                Assert.Equal(-1, actual);
            }
            else
            {
                // Ties on exact distance may pick either; compare the distance itself.
                Assert.True(actual >= 0);
                Assert.Equal(
                    FixVec2.SqrDistanceRaw(centre, positions[expected]),
                    FixVec2.SqrDistanceRaw(centre, positions[actual]));
            }
        }
    }

    [Fact]
    public void FindNearest_RespectsAFilter()
    {
        var (hash, positions, _) = Populate(1000, 63);

        int result = hash.FindNearest(
            new FixVec2(Fix.FromInt(256), Fix.FromInt(256)),
            Fix.FromInt(500),
            positions,
            id => id % 7 == 0);

        Assert.True(result >= 0);
        Assert.Equal(0, result % 7);
    }

    [Fact]
    public void FindNearest_ReturnsMinusOneWhenNothingIsInRange()
    {
        var hash = new SpatialHash(Fix.FromInt(512), Fix.FromInt(4), 16);
        var positions = new[] { new FixVec2(Fix.FromInt(500), Fix.FromInt(500)) };
        hash.Build(new[] { 0 }, positions);

        Assert.Equal(-1, hash.FindNearest(new FixVec2(Fix.FromInt(10), Fix.FromInt(10)), Fix.FromInt(20), positions));
    }

    [Fact]
    public void PositionsOutsideTheWorld_AreClampedNotWrapped()
    {
        // Routers run off the edge before they are removed, and a wrapped cell index
        // would teleport them into the middle of the enemy line.
        var hash = new SpatialHash(Fix.FromInt(512), Fix.FromInt(4), 16);
        var positions = new[]
        {
            new FixVec2(Fix.FromInt(-50), Fix.FromInt(-50)),
            new FixVec2(Fix.FromInt(900), Fix.FromInt(900)),
        };
        hash.Build(new[] { 0, 1 }, positions);

        var results = new int[8];
        Assert.Equal(1, hash.Query(new FixVec2(Fix.FromInt(-50), Fix.FromInt(-50)), Fix.FromInt(5), positions, results));
        Assert.Equal(0, results[0]);

        Assert.Equal(0, hash.Query(new FixVec2(Fix.FromInt(256), Fix.FromInt(256)), Fix.FromInt(5), positions, results));
    }

    [Fact]
    public void EmptyIndex_QueriesCleanly()
    {
        var hash = new SpatialHash(Fix.FromInt(512), Fix.FromInt(4));
        hash.Build(ReadOnlySpan<int>.Empty, ReadOnlySpan<FixVec2>.Empty);

        Assert.Equal(0, hash.Count);
        Assert.Equal(0, hash.Query(FixVec2.Zero, Fix.FromInt(100), Array.Empty<FixVec2>(), new int[4]));
    }
}
