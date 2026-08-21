using System.Linq;
using War.Sim.Campaign;
using War.Sim.Core;
using War.Sim.Sim;
using War.Sim.Units;
using Xunit;

namespace War.Sim.Tests.Campaign;

/// <summary>
/// Veterancy is the reason a campaign army is worth preserving.
///
/// Without it a legion that has fought across Sicily is just a smaller legion, and the
/// correct play is always to spend an army rather than withdraw it — which removes the one
/// decision the campaign layer exists to create.
/// </summary>
public class VeterancyTests
{
    private static CampaignArmy Army(Faction faction, string id, int count, int blooding = 0)
    {
        var army = new CampaignArmy { Id = 0, Owner = faction, Province = 0 };

        for (int i = 0; i < count; i++)
            army.Regiments.Add(new Regiment
            {
                TypeId = id,
                Strength = Roster.Get(id).DefaultStrength,
                Blooding = blooding,
            });

        return army;
    }

    [Fact]
    public void ChevronsComeOffBloodingAndStopAtNine()
    {
        var green = new Regiment { TypeId = "rome_hastati", Strength = 120 };
        Assert.Equal(0, green.Experience);

        green.Blooding = 3;
        Assert.Equal(1, green.Experience);

        green.Blooding = 900;
        Assert.Equal(SimConstants.MaxExperience, green.Experience);
    }

    [Fact]
    public void SurvivingABattleEarnsIt()
    {
        CampaignArmy strong = Army(Faction.Rome, "rome_principes", 6);
        CampaignArmy weak = Army(Faction.Greece, "greece_militia_hoplites", 1);

        BattleResolver.Estimate(strong, weak, Landscape.Farmland,
            new DetRandom(3, RngStream.CampaignBattle));

        // The winner learns more than the loser, and both learn something.
        Assert.All(strong.Regiments, r => Assert.Equal(2, r.Blooding));
        Assert.All(weak.Regiments, r => Assert.True(r.Blooding is 0 or 1));
    }

    [Fact]
    public void TheDeadLearnNothing()
    {
        // A regiment wiped out is buried before the blooding is handed round, so nothing
        // can accumulate experience it was not alive to earn.
        CampaignArmy overwhelming = Army(Faction.Rome, "rome_principes", 9);
        CampaignArmy doomed = Army(Faction.Greece, "greece_militia_hoplites", 1);

        for (int i = 0; i < 6; i++)
        {
            if (doomed.Regiments.Count == 0) break;
            BattleResolver.Estimate(overwhelming, doomed, Landscape.Farmland,
                new DetRandom((uint)i, RngStream.CampaignBattle));
        }

        Assert.All(doomed.Regiments, r => Assert.True(r.Strength > 0));
    }

    [Fact]
    public void MauledRegimentsRefillInQuietFriendlyProvinces()
    {
        // Played by the "player", so no AI orders move the army off its own ground and the
        // test measures reinforcement rather than the commander's marching decisions.
        CampaignState state = CampaignBuilder.Build(
            new CampaignSetup { Seed = 12, Player = Faction.Rome });

        CampaignArmy army = state.Armies.First(a => a.Owner == Faction.Rome);
        CampaignPower power = state.Power(army.Owner);
        power.Treasury = 20000;

        Regiment mauled = army.Regiments[0];
        mauled.Strength = 10;
        mauled.Blooding = 30;

        int before = mauled.Strength;
        CampaignSim.EndTurn(state);

        Assert.True(mauled.Strength > before,
            $"a regiment at {before} of {mauled.Establishment} in its own quiet province took no draft");
    }

    [Fact]
    public void TheDraftDilutesWhatItRefills()
    {
        // A regiment of ten veterans brought back to full is mostly recruits, and its
        // experience has to fall to match — otherwise a veteran unit is something you
        // rebuild rather than something you protect, and the decision disappears.
        CampaignState state = CampaignBuilder.Build(
            new CampaignSetup { Seed = 12, Player = Faction.Rome });

        CampaignArmy army = state.Armies.First(a => a.Owner == Faction.Rome);
        state.Power(army.Owner).Treasury = 20000;

        Regiment mauled = army.Regiments[0];
        mauled.Strength = 10;
        mauled.Blooding = 30;

        for (int turn = 0; turn < 25; turn++) CampaignSim.EndTurn(state);

        Assert.True(mauled.Blooding < 30,
            $"refilled from 10 to {mauled.Strength} and kept all {mauled.Blooding} blooding");
    }

    /// <summary>
    /// The measurement that makes the whole feature worth having: veterans beat green
    /// troops of the same type, in the actual engine, more often than not.
    /// </summary>
    [Fact]
    public void VeteransBeatGreenTroopsOfTheSameType()
    {
        var province = new Province
        {
            Id = 0, Name = "Test", Position = FixVec2.Zero,
            Landscape = Landscape.Farmland, Wealth = 400,
        };

        int veteransWon = 0;

        for (uint seed = 0; seed < 6; seed++)
        {
            // Identical armies but for the chevrons, and the veterans attack — so they are
            // giving up the defender's ground advantage as well.
            CampaignArmy veterans = Army(Faction.Rome, "rome_hastati", 4, blooding: 27);
            CampaignArmy green = Army(Faction.Rome, "rome_hastati", 4);

            if (BattleResolver.Fight(veterans, green, province, seed).Outcome
                == BattleOutcome.AttackerWon) veteransWon++;
        }

        Assert.True(veteransWon >= 4,
            $"nine chevrons won {veteransWon} of 6 against identical green troops");
    }
}
