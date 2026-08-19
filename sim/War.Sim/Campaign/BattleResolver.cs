using War.Sim.Core;
using War.Sim.Sim;
using War.Sim.Units;
using War.Sim.World;

namespace War.Sim.Campaign;

public enum BattleOutcome : byte
{
    AttackerWon = 0,
    DefenderWon = 1,
    Stalemate = 2,
}

public readonly record struct BattleReport(
    BattleOutcome Outcome,
    int AttackerLosses,
    int DefenderLosses,
    bool FoughtInFull);

/// <summary>
/// Turns two campaign armies standing in the same province into a result.
///
/// There are two ways to do it and the campaign needs both.
///
/// <see cref="Fight"/> builds a real <see cref="BattleSetup"/> from the two armies, on the
/// ground the province actually generates, and runs the tactical simulation to its
/// conclusion. It is the truth. It is also seconds of work per battle, which is fine for
/// the one battle the player is watching and hopeless for the six being fought elsewhere
/// on the same turn.
///
/// <see cref="Estimate"/> is the model used for everything the player is not watching. It
/// is not a coin flip weighted by headcount: it reads the same stats the melee system
/// reads, respects the same counters, and gives the defender the ground. Its job is to
/// agree with the real thing often enough that the map does not tell lies about the war —
/// which is a claim that can be measured, and is, by holding it against <see cref="Fight"/>
/// over a spread of matchups.
/// </summary>
public static class BattleResolver
{
    /// <summary>
    /// What a man of this type is worth in a line, roughly.
    ///
    /// Offence and defence added rather than multiplied, because a battle is decided by
    /// whichever side stops wanting to be there and both halves feed that equally. Charge
    /// counts half: it is enormous for ten seconds and then gone.
    /// </summary>
    private static int Quality(UnitType type)
    {
        int worth = type.Attack
                  + type.DefenceSkill
                  + type.Shield
                  + type.Armour
                  + type.Charge / 2
                  + type.MissileAttack / 2;

        // Nerve is not a tiebreaker in this period, it is the whole result. A unit that
        // will not break is worth more than one that hits harder.
        worth += (type.Morale + type.Discipline) / 2;

        // Hitpoints are a straight multiplier on how long a man lasts. Elephants have
        // several and it is most of what they are.
        return worth * type.Hitpoints;
    }

    // A hypothesis worth testing properly, recorded rather than shipped.
    //
    // Power below is linear in men, and the calibration run says that is wrong in a
    // specific way: in four of nine disagreements with the engine the SMALLER army won and
    // the model had picked the bigger one. The clearest was two Egyptian armies — eight
    // regiments of three hundred and thirty-seven men against seven of six hundred and
    // seventy-eight, twice the men, and it lost. A battle is fought along a frontage, so
    // the extra men stood in rear ranks all afternoon.
    //
    // Weighting men beyond the first forty in a regiment at a third was tried and moved
    // agreement from 31/40 to 29/40 — indistinguishable from noise at that sample size, so
    // it is not in. Settling it needs a few hundred calibration fights, which is twenty
    // minutes of engine time, not four.

    private static long Power(CampaignArmy army)
    {
        long total = 0;

        foreach (Regiment regiment in army.Regiments)
        {
            if (regiment.Strength <= 0) continue;
            total += (long)regiment.Strength * Quality(regiment.Type);
        }

        // A general on the field is worth more than his bodyguard: the whole line steadies
        // around him. The morale system models this as an aura; here it is a flat tenth.
        if (army.HasGeneral) total += total / 10;

        return total;
    }

    /// <summary>How much the ground is worth to whoever is standing on it already.</summary>
    private static long DefenderAdvantage(long power, Landscape landscape) => landscape switch
    {
        // The measured value of high ground in the tactical layer is about 1.65x on the
        // fighting itself; a fifth on the whole army's power is the conservative read of
        // that, since not every unit gets the hill.
        Landscape.Hills => power / 5,

        // Trees break up a line and make cavalry nearly useless. The defender knows the
        // wood.
        Landscape.Forest => power / 6,

        // Nowhere to hide, nothing to anchor on.
        Landscape.Desert => 0,

        _ => power / 12,
    };

    /// <summary>
    /// The fast model. Used for every battle the player is not present at.
    /// </summary>
    public static BattleReport Estimate(
        CampaignArmy attacker, CampaignArmy defender, Landscape landscape, DetRandom random)
    {
        long attackPower = Power(attacker);
        long defencePower = Power(defender) ;
        defencePower += DefenderAdvantage(defencePower, landscape);

        if (attackPower <= 0) return new BattleReport(BattleOutcome.DefenderWon, 0, 0, false);
        if (defencePower <= 0) return new BattleReport(BattleOutcome.AttackerWon, 0, 0, false);

        // Odds as a fraction of the whole, in thousandths so the fixed-point maths has
        // room to work at army scale.
        long total = attackPower + defencePower;
        int attackerShare = (int)(attackPower * 1000 / total);

        // Luck, but not much of it. Ancient battles are not lotteries — a two-to-one
        // advantage wins nearly always — but they do turn on which flank breaks first,
        // and that is worth about a tenth either way.
        attackerShare += random.NextInt(-90, 91);
        attackerShare = Math.Clamp(attackerShare, 20, 980);

        // A near-run thing leaves both armies on the field and neither able to press.
        //
        // A narrow band, because the tactical engine almost never ends a battle undecided:
        // one line breaks and the other holds the ground. Calling a stalemate where the
        // engine would have picked a winner is the model's most common way of being wrong.
        if (attackerShare is > 488 and < 512)
        {
            return new BattleReport(
                BattleOutcome.Stalemate,
                Apply(attacker, Fix.Ratio(22, 100), random),
                Apply(defender, Fix.Ratio(22, 100), random),
                false);
        }

        bool attackerWon = attackerShare >= 500;

        // The butcher's bill.
        //
        // Both sides' losses are scaled off the LOSER's size, not their own, because the
        // number of men you lose depends on how many enemies were swinging at you. Scaling
        // each side by its own headcount gets this exactly backwards: a four-to-one army
        // routing a small one came away having lost more men in absolute terms than the
        // army it destroyed, purely because fifteen per cent of a big number is large.
        //
        // The margin then decides the split. A battle won narrowly costs nearly as much as
        // it gains; a battle won at three to one is a massacre, because the losing army
        // breaks early and is ridden down from behind. That asymmetry — losers losing far
        // more than winners — is the single most important thing to get right here, and it
        // is the entire reason a campaign army is worth preserving rather than spending.
        int margin = Math.Abs(attackerShare - 500);

        CampaignArmy winner = attackerWon ? attacker : defender;
        CampaignArmy loser = attackerWon ? defender : attacker;

        int loserMen = Math.Max(1, loser.Men);
        int winnerMen = Math.Max(1, winner.Men);

        Fix loserLoss = Fix.Ratio(40 + margin * 45 / 480, 100);

        // Men the winner leaves on the field, as a share of what he was fighting — then
        // converted into a share of his own army so the same casualty routine can apply it.
        Fix winnerDead = Fix.Ratio(42 - margin * 32 / 480, 100) * loserMen;
        Fix winnerLoss = winnerDead / winnerMen;

        int winnerLosses = Apply(winner, winnerLoss, random);
        int loserLosses = Apply(loser, loserLoss, random);

        int attackerLosses = attackerWon ? winnerLosses : loserLosses;
        int defenderLosses = attackerWon ? loserLosses : winnerLosses;

        return new BattleReport(
            attackerWon ? BattleOutcome.AttackerWon : BattleOutcome.DefenderWon,
            attackerLosses, defenderLosses, false);
    }

    /// <summary>
    /// Takes a fraction of an army's men, spread unevenly across its regiments.
    ///
    /// Unevenly on purpose. Losses fall on whoever was in the front line, so a battle that
    /// costs a quarter of an army does not cost every unit exactly a quarter — it wrecks
    /// two regiments and barely touches the rest, which is what leaves a campaign army
    /// with a distinct history rather than a uniformly worn one.
    /// </summary>
    private static int Apply(CampaignArmy army, Fix fraction, DetRandom random)
    {
        int killed = 0;

        foreach (Regiment regiment in army.Regiments)
        {
            if (regiment.Strength <= 0) continue;

            Fix share = fraction * random.NextFix(Fix.Ratio(45, 100), Fix.Ratio(155, 100));
            int losses = (share * regiment.Strength).RoundToInt;

            losses = Math.Clamp(losses, 0, regiment.Strength);
            regiment.Strength -= losses;
            killed += losses;
        }

        army.BuryTheDead();
        return killed;
    }

    /// <summary>
    /// The real thing: build the battle and run it.
    ///
    /// Used when the player fights, and by the calibration test that keeps
    /// <see cref="Estimate"/> honest. Casualties come back per regiment from the actual
    /// soldiers left standing, so a unit that held the right flank all afternoon returns
    /// at the strength it earned rather than at an average.
    /// </summary>
    public static BattleReport Fight(
        CampaignArmy attacker, CampaignArmy defender, Province province, uint seed)
    {
        Terrain terrain = TerrainGenerator.Generate(province.Battlefield(seed));

        BattleSetup setup = new()
        {
            Terrain = terrain,
            Seed = seed,
            Separation = Fix.FromInt(380),
            Armies =
            [
                Blueprint(attacker),
                Blueprint(defender),
            ],
        };

        BattleSim sim = BattleSim.Create(setup);
        sim.Run(SimConstants.Ticks(BattleSim.TimeLimitSeconds));

        int attackerLosses = WriteBack(attacker, sim.State, 0);
        int defenderLosses = WriteBack(defender, sim.State, 1);

        BattleOutcome outcome = sim.State.Result switch
        {
            BattleResult.ArmyVictory when sim.State.Victor == 0 => BattleOutcome.AttackerWon,
            BattleResult.ArmyVictory => BattleOutcome.DefenderWon,
            _ => BattleOutcome.Stalemate,
        };

        return new BattleReport(outcome, attackerLosses, defenderLosses, true);
    }

    private static ArmyBlueprint Blueprint(CampaignArmy army) => new()
    {
        Faction = army.Owner,
        Name = army.Owner.ToString(),
        Units = army.Regiments
            .Where(r => r.Strength > 0)
            .Select(r => new UnitBlueprint { TypeId = r.TypeId, Strength = r.Strength })
            .ToList(),
    };

    /// <summary>
    /// Copies survivors out of a finished battle and back into the campaign army.
    ///
    /// Order matters and is load-bearing: <see cref="Blueprint"/> emits regiments in the
    /// army's own order, skipping the destroyed, and the builder preserves that order — so
    /// the nth surviving regiment maps to the nth battle unit. Anything else would hand a
    /// unit's casualties to its neighbour.
    /// </summary>
    private static int WriteBack(CampaignArmy army, BattleState state, int armyId)
    {
        var fielded = army.Regiments.Where(r => r.Strength > 0).ToList();
        int[] unitIds = state.Armies[armyId].UnitIds;
        int losses = 0;

        for (int i = 0; i < fielded.Count && i < unitIds.Length; i++)
        {
            Unit unit = state.Units[unitIds[i]];

            // Men who ran are not dead. A routed unit that leaves the field keeps the
            // survivors it ran away with, which is the difference between a defeat and an
            // annihilation and the reason a beaten army is still worth withdrawing.
            int survivors = Math.Max(0, unit.Alive);

            losses += fielded[i].Strength - survivors;
            fielded[i].Strength = survivors;
        }

        army.BuryTheDead();
        return losses;
    }
}
