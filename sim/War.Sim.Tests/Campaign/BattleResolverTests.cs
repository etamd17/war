using War.Sim.Campaign;
using War.Sim.Core;
using War.Sim.Units;
using Xunit;

namespace War.Sim.Tests.Campaign;

/// <summary>
/// Holds the campaign's fast battle model against the real one.
///
/// Most battles in a campaign are fought somewhere the player is not, and resolving each
/// of them with the full tactical simulation costs tens of seconds — so they are
/// estimated. That estimate is a promise: that the map is telling the truth about the war.
/// If the quick model disagrees with the engine, then armies the player never sees are
/// winning battles they would have lost, and the campaign is quietly a different game from
/// the one being played on the field.
///
/// So the promise is measured rather than asserted in a comment.
/// </summary>
public class BattleResolverTests
{
    private static CampaignArmy Army(Faction faction, params (string id, int count)[] units)
    {
        var army = new CampaignArmy { Id = 0, Owner = faction, Province = 0 };

        foreach ((string id, int count) in units)
            for (int i = 0; i < count; i++)
                army.Regiments.Add(new Regiment
                {
                    TypeId = id,
                    Strength = Roster.Get(id).DefaultStrength,
                });

        return army;
    }

    private static CampaignArmy Copy(CampaignArmy army)
    {
        var clone = new CampaignArmy { Id = army.Id, Owner = army.Owner, Province = army.Province };
        foreach (Regiment regiment in army.Regiments)
            clone.Regiments.Add(new Regiment { TypeId = regiment.TypeId, Strength = regiment.Strength });
        return clone;
    }

    [Fact]
    public void TheLoserOfAOneSidedFightLosesFarMoreMen()
    {
        // Ancient battles are decided by rout, and a rout is a chase. If both sides came
        // away equally scratched there would be no reason ever to preserve an army, and
        // the whole campaign layer would collapse into arithmetic.
        CampaignArmy strong = Army(Faction.Rome, ("rome_principes", 6), ("rome_equites", 2));
        CampaignArmy weak = Army(Faction.Greece, ("greece_militia_hoplites", 2));

        BattleReport report = BattleResolver.Estimate(
            strong, weak, Landscape.Farmland, new DetRandom(17, RngStream.CampaignBattle));

        Assert.Equal(BattleOutcome.AttackerWon, report.Outcome);
        Assert.True(report.DefenderLosses > report.AttackerLosses * 2,
            $"winner lost {report.AttackerLosses}, loser lost {report.DefenderLosses}");
    }

    [Fact]
    public void GroundIsWorthSomethingToWhoeverIsStandingOnIt()
    {
        // The same two armies, twice, differing only in where they meet. A defender in the
        // hills should win more often than the same defender in open desert — that is the
        // link that makes choosing where to fight a campaign decision.
        int wonInHills = 0, wonInDesert = 0;

        for (uint seed = 0; seed < 60; seed++)
        {
            CampaignArmy attackA = Army(Faction.Rome, ("rome_hastati", 4));
            CampaignArmy defendA = Army(Faction.Greece, ("greece_hoplites", 4));
            if (BattleResolver.Estimate(attackA, defendA, Landscape.Hills,
                    new DetRandom(seed, RngStream.CampaignBattle)).Outcome == BattleOutcome.DefenderWon)
                wonInHills++;

            CampaignArmy attackB = Army(Faction.Rome, ("rome_hastati", 4));
            CampaignArmy defendB = Army(Faction.Greece, ("greece_hoplites", 4));
            if (BattleResolver.Estimate(attackB, defendB, Landscape.Desert,
                    new DetRandom(seed, RngStream.CampaignBattle)).Outcome == BattleOutcome.DefenderWon)
                wonInDesert++;
        }

        Assert.True(wonInHills > wonInDesert,
            $"the hills won {wonInHills} of 60 and the open desert {wonInDesert} — the ground is doing nothing");
    }

    /// <summary>
    /// The calibration itself: does the quick model pick the same winner as the engine?
    ///
    /// Deliberately lopsided matchups. Nobody can predict a near-run thing — the tactical
    /// layer turns those on which flank breaks first, and it is supposed to — but a model
    /// that cannot tell two-to-one from one-to-two is not a model, it is a coin. These are
    /// the cases where being wrong would be visible on the map.
    /// </summary>
    [Fact]
    public void TheQuickModelAgreesWithTheEngineOnLopsidedFights()
    {
        (CampaignArmy attacker, CampaignArmy defender, BattleOutcome expected)[] fights =
        [
            (Army(Faction.Rome, ("rome_principes", 6), ("rome_equites", 2)),
             Army(Faction.Greece, ("greece_militia_hoplites", 2)),
             BattleOutcome.AttackerWon),

            (Army(Faction.Egypt, ("egypt_nile_spearmen", 2)),
             Army(Faction.Rome, ("rome_triarii", 4), ("rome_principes", 3)),
             BattleOutcome.DefenderWon),

            (Army(Faction.Gaul, ("gaul_warband", 8), ("gaul_cavalry", 2)),
             Army(Faction.Greece, ("greece_peltasts", 2)),
             BattleOutcome.AttackerWon),

            (Army(Faction.Carthage, ("carthage_iberian", 2)),
             Army(Faction.Carthage, ("carthage_sacred_band", 4), ("carthage_elephants", 1)),
             BattleOutcome.DefenderWon),
        ];

        int agreed = 0;

        foreach ((CampaignArmy attacker, CampaignArmy defender, BattleOutcome expected) in fights)
        {
            BattleOutcome quick = BattleResolver.Estimate(
                Copy(attacker), Copy(defender), Landscape.Farmland,
                new DetRandom(31, RngStream.CampaignBattle)).Outcome;

            Assert.Equal(expected, quick);

            // And the engine, on ground the same province would generate.
            var province = new Province
            {
                Id = 0, Name = "Test", Position = FixVec2.Zero,
                Landscape = Landscape.Farmland, Wealth = 400,
            };

            BattleOutcome real = BattleResolver
                .Fight(Copy(attacker), Copy(defender), province, 31).Outcome;

            if (real == quick) agreed++;
        }

        Assert.True(agreed >= fights.Length - 1,
            $"the quick model matched the engine on {agreed} of {fights.Length} lopsided fights");
    }
}
