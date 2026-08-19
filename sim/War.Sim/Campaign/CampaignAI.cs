using War.Sim.Core;
using War.Sim.Units;

namespace War.Sim.Campaign;

/// <summary>
/// A power's strategy, such as it is.
///
/// Like the battlefield commander, this is not trying to play well. It is trying to be a
/// credible opponent by doing the small number of things that a human notices the absence
/// of: expanding into weakness rather than into strength, coming home when its own ground
/// is threatened, and keeping an army it can actually pay for.
///
/// The one thing it is careful about is not throwing armies away. An AI that attacks
/// whatever is adjacent bleeds itself dry in ten turns and leaves a dead map; the odds
/// check below is most of what makes the campaign still have five powers in it at turn
/// fifty.
/// </summary>
public static class CampaignAI
{
    /// <summary>Coin kept back rather than spent, so there is always something for an emergency.</summary>
    private const int Reserve = 300;

    /// <summary>Regiments in a field army before it stops taking reinforcements.</summary>
    private const int FullArmy = 10;

    public static void IssueOrders(CampaignState state, CampaignPower power)
    {
        DetRandom random = state.Random(RngStream.Campaign, (int)power.Faction);

        foreach (CampaignArmy army in state.Armies)
        {
            if (army.Owner != power.Faction || army.IsDestroyed) continue;

            Province here = state[army.Province];
            long ourStrength = Strength(army);

            // Press a siege already under way.
            //
            // Staying put scores nothing, so an army that invested a province marched off
            // the following turn and the clock reset behind it. The map filled with sieges
            // that were laid over and over and never once completed: at turn a hundred and
            // twenty every power still held roughly what it started with, and the log was
            // nothing but "lays siege to" with no province ever falling.
            if (here.Owner != power.Faction && here.Besieger == power.Faction)
            {
                army.Destination = null;
                continue;
            }

            int? best = null;
            long bestScore = 0;

            foreach (int neighbourId in here.Neighbours)
            {
                long score = Appraise(state, power, army, ourStrength, neighbourId);

                // A little noise, so two armies in identical situations do not march in
                // lockstep forever and the same border is not probed at the same place
                // every single turn.
                score += random.NextInt(-40, 41);

                if (score <= bestScore) continue;
                bestScore = score;
                best = neighbourId;
            }

            army.Destination = best;
        }
    }

    /// <summary>What a neighbouring province is worth marching into, or zero for "stay put".</summary>
    private static long Appraise(
        CampaignState state, CampaignPower power, CampaignArmy army, long ourStrength, int provinceId)
    {
        Province province = state[provinceId];

        long theirStrength = 0;
        foreach (CampaignArmy other in state.ArmiesIn(provinceId))
            if (other.Owner != power.Faction) theirStrength += Strength(other);

        // The levy counts. It will not beat an army, but marching a battered stack at a
        // fully raised province in the hills is how an army stops existing.
        if (province.Owner != power.Faction) theirStrength += province.Militia * 16L;

        // Never march into something that will win. Two to one against is a lost army and
        // a lost army is a lost campaign — this single check is the difference between a
        // map with five powers on it at turn fifty and a map with two.
        if (theirStrength > 0 && ourStrength < theirStrength * 5 / 4) return 0;

        long score = province.Wealth / 4;

        if (province.Owner == power.Faction)
        {
            // Our own ground is only worth moving to if somebody is standing on it.
            return theirStrength > 0 ? score + 260 : 0;
        }

        // Independent ground is the cheap expansion every power should take first, and it
        // does not widen a war.
        score += province.Owner is null ? 200 : 120;

        // Stay joined up.
        //
        // Without this the AI reads the map as a list of prizes and marches at whichever
        // is worth most, so Egyptian armies turned up in Samnium and Gauls took Zeugitana
        // while their own borders were open. An army that cannot be supported from home is
        // not conquering, it is wandering — and a province with no friendly neighbour
        // cannot be held once taken.
        int friendlyNeighbours = 0;
        foreach (int neighbour in province.Neighbours)
            if (state[neighbour].Owner == power.Faction) friendlyNeighbours++;

        if (friendlyNeighbours == 0) return 0;
        score += friendlyNeighbours * 60;

        // Undefended is worth far more than merely winnable: taking a province costs a
        // turn of standing on it, and a fight makes that two.
        if (theirStrength == 0) score += 140;

        return score;
    }

    private static long Strength(CampaignArmy army)
    {
        long total = 0;
        foreach (Regiment regiment in army.Regiments)
            total += (long)regiment.Strength * (regiment.Type.Attack + regiment.Type.DefenceSkill + 4);
        return total;
    }

    // ------------------------------------------------------------- recruitment

    public static void Recruit(CampaignState state, CampaignPower power)
    {
        DetRandom random = state.Random(RngStream.Campaign, 500 + (int)power.Faction);

        while (power.Treasury > Reserve)
        {
            CampaignArmy? army = ArmyToReinforce(state, power);
            if (army == null) return;

            UnitType? wanted = NextUnitFor(state, army, power.Faction, random);
            if (wanted == null) return;
            if (wanted.Cost > power.Treasury - Reserve) return;

            power.Treasury -= wanted.Cost;
            army.Regiments.Add(new Regiment { TypeId = wanted.Id, Strength = wanted.DefaultStrength });
        }
    }

    /// <summary>
    /// Buys one regiment in a named province, for a power giving its own orders.
    ///
    /// Deliberately the same choice the AI would have made. A player pressing the button
    /// repeatedly gets a balanced army rather than ten regiments of whatever is strongest,
    /// and there is exactly one definition in the codebase of what an army should look
    /// like — which is the only way the two stay in step as the roster changes.
    /// </summary>
    public static bool RecruitOne(CampaignState state, CampaignPower power, int provinceId)
    {
        if (state[provinceId].Owner != power.Faction) return false;
        if (state.ArmiesIn(provinceId).Any(a => a.Owner != power.Faction)) return false;

        CampaignArmy army = state.ArmiesIn(provinceId).FirstOrDefault(a => a.Owner == power.Faction)
            ?? Raise(state, power.Faction, provinceId);

        DetRandom random = state.Random(RngStream.Campaign, 900 + army.Regiments.Count);

        UnitType? wanted = NextUnitFor(state, army, power.Faction, random);
        if (wanted == null || wanted.Cost > power.Treasury) return false;

        power.Treasury -= wanted.Cost;
        army.Regiments.Add(new Regiment { TypeId = wanted.Id, Strength = wanted.DefaultStrength });
        return true;
    }

    private static CampaignArmy Raise(CampaignState state, Faction faction, int provinceId)
    {
        var army = new CampaignArmy
        {
            Id = state.NextArmyId++,
            Owner = faction,
            Province = provinceId,
        };

        state.Armies.Add(army);
        return army;
    }

    /// <summary>
    /// Where new troops appear.
    ///
    /// In a province the power actually holds and nobody is contesting, joining the army
    /// there if there is one — so reinforcements reach a field army rather than
    /// accumulating in the capital while the border collapses. Failing that, a new army is
    /// raised in the richest quiet province, which is where the money to pay for it is.
    /// </summary>
    private static CampaignArmy? ArmyToReinforce(CampaignState state, CampaignPower power)
    {
        CampaignArmy? best = null;
        int bestSize = int.MaxValue;

        foreach (CampaignArmy army in state.Armies)
        {
            if (army.Owner != power.Faction || army.Regiments.Count >= FullArmy) continue;
            if (state.ArmiesIn(army.Province).Any(a => a.Owner != power.Faction)) continue;
            if (state[army.Province].Owner != power.Faction) continue;

            if (army.Regiments.Count >= bestSize) continue;
            bestSize = army.Regiments.Count;
            best = army;
        }

        if (best != null) return best;

        Province? home = state.Held(power.Faction)
            .Where(p => !state.ArmiesIn(p.Id).Any(a => a.Owner != power.Faction))
            .OrderByDescending(p => p.Wealth)
            .FirstOrDefault();

        if (home == null) return null;

        return Raise(state, power.Faction, home.Id);
    }

    /// <summary>
    /// What the army is short of.
    ///
    /// Armies are built to a shape rather than by picking the best unit available, because
    /// picking the best unit available produces ten regiments of the same heavy infantry
    /// and an army with no answer to anything. The shape is roughly the one the tactical
    /// layer rewards: a line to hold, something to shoot with, horse for the flanks, and
    /// exactly one general.
    /// </summary>
    private static UnitType? NextUnitFor(
        CampaignState state, CampaignArmy army, Faction faction, DetRandom random)
    {
        int line = 0, missile = 0, horse = 0;
        bool hasGeneral = false;

        foreach (Regiment regiment in army.Regiments)
        {
            switch (regiment.Type.Class)
            {
                case UnitClass.General: hasGeneral = true; break;
                case UnitClass.Missile: missile++; break;
                case UnitClass.Cavalry:
                case UnitClass.MissileCavalry:
                case UnitClass.Chariot:
                case UnitClass.Elephant: horse++; break;
                default: line++; break;
            }
        }

        int total = line + missile + horse;

        UnitClass[] wanted =
            !hasGeneral && total >= 3 ? [UnitClass.General]
            : missile * 5 < total ? [UnitClass.Missile]
            : horse * 4 < total ? [UnitClass.Cavalry, UnitClass.MissileCavalry, UnitClass.Elephant, UnitClass.Chariot]
            : [UnitClass.Infantry, UnitClass.Spear, UnitClass.Pike];

        var options = Roster.ByFaction(faction)
            .Where(u => wanted.Contains(u.Class))
            .OrderBy(u => u.Id, StringComparer.Ordinal)
            .ToList();

        if (options.Count == 0) return null;

        // Buy the best that can be afforded rather than always the cheapest, so a rich
        // power fields a visibly better army than a poor one.
        CampaignPower power = state.Power(faction);
        var affordable = options.Where(u => u.Cost <= power.Treasury).ToList();
        if (affordable.Count == 0) return null;

        int ceiling = affordable.Max(u => u.Cost);
        var pick = affordable.Where(u => u.Cost >= ceiling * 2 / 3).ToList();

        return pick[random.NextInt(pick.Count)];
    }
}
