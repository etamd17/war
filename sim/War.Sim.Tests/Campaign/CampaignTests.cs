using War.Sim.Campaign;
using War.Sim.Units;
using Xunit;

namespace War.Sim.Tests.Campaign;

public class CampaignMapTests
{
    [Fact]
    public void EveryProvinceIsReachableFromEveryOther()
    {
        // A province nobody can march to is invisible: it simply never appears in a war,
        // and the only symptom is that the map feels slightly smaller than it is.
        List<Province> provinces = CampaignMap.Create();

        var seen = new HashSet<int> { 0 };
        var queue = new Queue<int>([0]);

        while (queue.Count > 0)
            foreach (int neighbour in provinces[queue.Dequeue()].Neighbours)
                if (seen.Add(neighbour)) queue.Enqueue(neighbour);

        Assert.Equal(provinces.Count, seen.Count);
    }

    [Fact]
    public void AdjacencyRunsBothWays()
    {
        // The bug this guards against is a province you can march into and not out of,
        // which reads from the outside as an AI that has stopped making decisions.
        List<Province> provinces = CampaignMap.Create();

        foreach (Province province in provinces)
            foreach (int neighbour in province.Neighbours)
                Assert.Contains(province.Id, provinces[neighbour].Neighbours);
    }

    [Fact]
    public void EveryPowerStartsWithGroundAndAnArmy()
    {
        CampaignState state = CampaignBuilder.Build(new CampaignSetup());

        foreach (Faction faction in Enum.GetValues<Faction>())
        {
            Assert.True(state.ProvinceCount(faction) > 0, $"{faction} holds nothing");
            Assert.Contains(state.Armies, a => a.Owner == faction && a.Men > 0);
        }
    }
}

public class CampaignTurnTests
{
    private static CampaignState Run(uint seed, int turns)
    {
        CampaignState state = CampaignBuilder.Build(new CampaignSetup { Seed = seed });
        for (int i = 0; i < turns; i++) CampaignSim.EndTurn(state);
        return state;
    }

    /// <summary>
    /// The regression that matters most in this layer.
    ///
    /// Provinces could not change hands at all. A besieged province went on raising its
    /// levy at the end of every turn, the levy had to reach zero before the province could
    /// fall, so it converged to a comfortable number and sat there. A hundred and fifty
    /// turns produced not one capture — and nothing looked wrong. Every power ended holding
    /// exactly what it started with, the borders looked stable and deliberate, and the
    /// chronicle was a full column of sieges being laid by armies that could never take
    /// anything. A campaign that cannot be won is indistinguishable from a cautious one.
    /// </summary>
    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    public void GroundActuallyChangesHands(uint seed)
    {
        CampaignState before = CampaignBuilder.Build(new CampaignSetup { Seed = seed });
        var opening = before.Provinces.Select(p => p.Owner).ToList();

        CampaignState after = Run(seed, 60);
        int changed = after.Provinces.Count(p => p.Owner != opening[p.Id]);

        Assert.True(changed > 0,
            "sixty turns and the map is untouched — nothing can take a province");
    }

    [Fact]
    public void TheSameSeedTellsTheSameStory()
    {
        CampaignState a = Run(99, 50);
        CampaignState b = Run(99, 50);

        Assert.Equal(a.Chronicle, b.Chronicle);
        Assert.Equal(
            a.Provinces.Select(p => (p.Id, p.Owner, p.Militia)),
            b.Provinces.Select(p => (p.Id, p.Owner, p.Militia)));
    }

    [Fact]
    public void AnArmyOrderedSomewhereItCannotReachStaysPut()
    {
        // Orders come from an AI and, later, from a player. Neither should be able to
        // teleport, and the check belongs in the turn rather than in the order — so it is
        // tested by giving an illegal order rather than by trusting the giver.
        CampaignState state = CampaignBuilder.Build(new CampaignSetup { Seed = 5 });

        CampaignArmy army = state.Armies[0];
        int from = army.Province;

        int distant = state.Provinces
            .First(p => p.Id != from && !state[from].Neighbours.Contains(p.Id)).Id;

        army.Destination = distant;
        CampaignSim.EndTurn(state);

        Assert.NotEqual(distant, state.Armies.First(a => a.Id == army.Id).Province);
    }

    [Fact]
    public void ArmiesOnlyEverMoveByMarchingAndBeingDrivenBack()
    {
        // Two steps, not one: an army may march into a province and then lose the battle
        // it found there, which pushes it out again the same turn. Anything further than
        // that is a unit crossing the map without passing through it.
        CampaignState state = CampaignBuilder.Build(new CampaignSetup { Seed = 5 });

        for (int turn = 0; turn < 40; turn++)
        {
            var before = state.Armies.ToDictionary(a => a.Id, a => a.Province);
            CampaignSim.EndTurn(state);

            foreach (CampaignArmy army in state.Armies)
            {
                if (!before.TryGetValue(army.Id, out int from) || from == army.Province) continue;

                bool withinTwoSteps = state[from].Neighbours.Contains(army.Province)
                    || state[from].Neighbours.Any(n => state[n].Neighbours.Contains(army.Province));

                Assert.True(withinTwoSteps,
                    $"an army went from {state[from].Name} to {state[army.Province].Name} in one turn");
            }
        }
    }

    [Fact]
    public void NobodyEndsTheTurnWithGhostRegiments()
    {
        CampaignState state = Run(11, 80);

        foreach (CampaignArmy army in state.Armies)
        {
            Assert.NotEmpty(army.Regiments);
            foreach (Regiment regiment in army.Regiments)
                Assert.InRange(regiment.Strength, 1, regiment.Establishment);
        }
    }

    [Fact]
    public void ACampaignReachesAConclusionOrACredibleStalemate()
    {
        // Not a balance assertion — just that the thing terminates in a recognisable
        // state rather than grinding every power down to nothing.
        CampaignState state = Run(4, 200);
        int standing = state.Powers.Values.Count(p => !p.Destroyed);

        Assert.InRange(standing, 1, 5);
        Assert.Contains(state.Provinces, p => p.Owner != null);
    }
}
