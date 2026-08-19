using War.Sim.Core;
using War.Sim.Units;

namespace War.Sim.Campaign;

/// <summary>
/// Two armies that movement has put in the same province, before anyone has fought.
///
/// Exists so a turn can be stopped halfway. The battle the player is standing in wants to
/// be handed to the tactical engine and fought over the next several minutes in a window,
/// which a turn that resolves everything in one call cannot allow.
/// </summary>
public sealed record PendingBattle(CampaignArmy Attacker, CampaignArmy Defender, Province Province);

/// <summary>
/// The campaign turn.
///
/// One method does the work and the order inside it is the design. Movement resolves
/// before battle, so an army that marches into an occupied province fights there rather
/// than passing through. Battle resolves before occupation, so a province only changes
/// hands once the last enemy in it is gone. Money is counted after all of that, so a
/// province taken this turn pays its new owner and a province lost does not.
///
/// Everything is driven from <see cref="CampaignState"/> and its seeded random source, so
/// a campaign replays identically from its seed — the same property the battle layer has,
/// for the same reasons, and it is what lets a campaign be tested at all.
/// </summary>
public static class CampaignSim
{
    /// <summary>Provinces needed to win outright. Two thirds of the map.</summary>
    public const int VictoryProvinces = 21;

    /// <summary>Fraction of a unit's cost paid every turn to keep it in the field, as a divisor.</summary>
    private const int UpkeepDivisor = 14;

    /// <summary>
    /// The whole turn, with every battle estimated.
    ///
    /// This is what a campaign nobody is watching does. A campaign somebody IS watching
    /// calls the three phases below instead, so that the one battle the player is standing
    /// in can be handed to the tactical engine and fought properly while the rest of the
    /// world resolves around it.
    /// </summary>
    public static void EndTurn(CampaignState state)
    {
        foreach (PendingBattle battle in BeginTurn(state))
        {
            if (battle.Attacker.IsDestroyed || battle.Defender.IsDestroyed) continue;

            DetRandom random = state.Random(RngStream.CampaignBattle, battle.Province.Id);
            Settle(state, battle, BattleResolver.Estimate(
                battle.Attacker, battle.Defender, battle.Province.Landscape, random));
        }

        CompleteTurn(state);
    }

    /// <summary>
    /// Orders, movement, and the list of fights that movement has caused.
    ///
    /// Stops before resolving any of them, because the caller may want to fight one of them
    /// in a window over the next several minutes.
    /// </summary>
    public static List<PendingBattle> BeginTurn(CampaignState state)
    {
        foreach (CampaignPower power in state.Powers.Values)
        {
            if (power.Destroyed || power.IsPlayer) continue;
            CampaignAI.IssueOrders(state, power);
        }

        ResolveMovement(state);
        MergeStacks(state);
        return FindBattles(state);
    }

    /// <summary>Everything after the fighting: sieges, ground taken, money, new troops.</summary>
    public static void CompleteTurn(CampaignState state)
    {
        state.Armies.RemoveAll(a => a.IsDestroyed);
        Besiege(state);
        ResolveOccupation(state);
        CollectRevenue(state);
        Recruit(state);

        foreach (Province province in state.Provinces)
        {
            if (province.Unrest > 0) province.Unrest--;
            province.RaiseLevy();
        }

        state.Armies.RemoveAll(a => a.IsDestroyed);
        CheckForTheEnd(state);

        state.Turn++;
    }

    // ---------------------------------------------------------------- movement

    private static void ResolveMovement(CampaignState state)
    {
        foreach (CampaignArmy army in state.Armies)
        {
            int? destination = army.Destination;
            army.Destination = null;

            if (destination is not { } target || army.IsDestroyed) continue;

            // Adjacency is checked here rather than trusted from the order, because orders
            // come from the AI and from the player and neither should be able to teleport.
            if (!Array.Exists(state[army.Province].Neighbours, n => n == target)) continue;

            army.Province = target;
        }
    }

    /// <summary>
    /// Folds each faction's armies in a province into one.
    ///
    /// Two friendly stacks standing on the same ground are one army in every sense that
    /// matters, and treating them separately means resolving two battles in the same
    /// province on the same turn — the second of which is fought by whoever survived the
    /// first, against an enemy that has already won. Merging first makes reinforcement
    /// work the way a player expects: send a second army and the fight is bigger.
    /// </summary>
    private static void MergeStacks(CampaignState state)
    {
        var lead = new Dictionary<(int, Faction), CampaignArmy>();

        foreach (CampaignArmy army in state.Armies.OrderBy(a => a.Id))
        {
            if (army.IsDestroyed) continue;

            var key = (army.Province, army.Owner);
            if (!lead.TryGetValue(key, out CampaignArmy? first))
            {
                lead[key] = army;
                continue;
            }

            first.Regiments.AddRange(army.Regiments);
            army.Regiments.Clear();
        }

        state.Armies.RemoveAll(a => a.Regiments.Count == 0);
    }

    // ----------------------------------------------------------------- battles

    private static List<PendingBattle> FindBattles(CampaignState state)
    {
        var battles = new List<PendingBattle>();

        foreach (Province province in state.Provinces)
        {
            var present = state.ArmiesIn(province.Id).ToList();
            if (present.Count < 2) continue;

            // The province owner defends it. If none of the armies present owns the ground
            // — two powers colliding in somebody else's province — the larger is treated as
            // holding it, which is the closest thing to arriving first that a simultaneous
            // turn can offer.
            CampaignArmy defender =
                present.FirstOrDefault(a => a.Owner == province.Owner)
                ?? present.OrderByDescending(a => a.Men).First();

            foreach (CampaignArmy attacker in present.Where(a => a.Owner != defender.Owner))
                battles.Add(new PendingBattle(attacker, defender, province));
        }

        return battles;
    }

    /// <summary>Applies a result, however it was arrived at, and pushes the loser out.</summary>
    public static void Settle(CampaignState state, PendingBattle battle, BattleReport report)
    {
        CampaignArmy attacker = battle.Attacker;
        CampaignArmy defender = battle.Defender;
        Province province = battle.Province;

        string verdict = report.Outcome switch
        {
            BattleOutcome.AttackerWon => $"{attacker.Owner} breaks {defender.Owner}",
            BattleOutcome.DefenderWon => $"{defender.Owner} holds against {attacker.Owner}",
            _ => $"{attacker.Owner} and {defender.Owner} maul each other to no purpose",
        };

        state.Record(
            $"battle in {province.Name}: {verdict} " +
            $"({report.AttackerLosses} and {report.DefenderLosses} dead)" +
            (report.FoughtInFull ? ", fought in full" : ""));

        // The loser quits the province. Not destroyed — a beaten army that got away is
        // still an army, and letting it retreat is what makes the map move rather than
        // simply emptying.
        CampaignArmy? beaten = report.Outcome switch
        {
            BattleOutcome.AttackerWon => attacker.IsDestroyed ? null : defender,
            BattleOutcome.DefenderWon => defender.IsDestroyed ? null : attacker,
            _ => null,
        };

        if (beaten is { IsDestroyed: false }) Retreat(state, beaten, province);
    }

    /// <summary>
    /// Pushes a beaten army into the nearest friendly ground it can reach.
    ///
    /// Falling back onto your own territory if any touches this province, and otherwise
    /// anywhere at all that is not the battlefield. An army with nowhere to go is
    /// surrounded and is destroyed where it stands — which is how an encircled force
    /// should end, and gives cutting off a retreat a point.
    /// </summary>
    private static void Retreat(CampaignState state, CampaignArmy army, Province from)
    {
        int? friendly = null;
        int? anywhere = null;

        foreach (int neighbour in from.Neighbours)
        {
            bool contested = state.ArmiesIn(neighbour).Any(a => a.Owner != army.Owner);
            if (contested) continue;

            anywhere ??= neighbour;
            if (state[neighbour].Owner == army.Owner) { friendly = neighbour; break; }
        }

        int? refuge = friendly ?? anywhere;

        if (refuge is not { } destination)
        {
            state.Record($"{army.Owner}'s army is surrounded in {from.Name} and destroyed");
            army.Regiments.Clear();
            return;
        }

        army.Province = destination;
    }

    /// <summary>
    /// Sieges: armies sitting on ground they do not own.
    ///
    /// An army alone in a hostile province invests it. Each turn the levy loses men to
    /// sorties and assaults, and the siege clock runs; when the clock runs out the province
    /// falls. Drive the besieger off — or simply march an army in beside the defenders —
    /// and the clock resets to nothing.
    ///
    /// That reset is the whole mechanism. It means a border is held by being able to
    /// threaten a relief, not by standing on every province at once, and it is what turns
    /// a map of sweeping stacks into a campaign with sieges to raise and armies to
    /// intercept.
    /// </summary>
    private static void Besiege(CampaignState state)
    {
        foreach (Province province in state.Provinces)
        {
            var invaders = state.ArmiesIn(province.Id)
                .Where(a => a.Owner != province.Owner)
                .ToList();

            bool defended = state.ArmiesIn(province.Id).Any(a => a.Owner == province.Owner);

            if (invaders.Count == 0 || defended)
            {
                province.LiftSiege();
                continue;
            }

            CampaignArmy besieger = invaders.OrderByDescending(a => a.Men).First();

            // A different power taking over the siege starts the clock again. Nobody
            // inherits somebody else's investment.
            if (province.Besieger != besieger.Owner)
            {
                province.Siege = 0;
                province.Besieger = besieger.Owner;
            }

            province.Siege++;

            DetRandom random = state.Random(RngStream.CampaignBattle, 4000 + province.Id);

            if (province.Militia > 0)
            {
                // The levy sorties and is worn down. It cannot win, and it is not supposed
                // to — it is buying the turns the siege clock is counting.
                long attack = 0;
                foreach (Regiment regiment in besieger.Regiments)
                    attack += (long)regiment.Strength * (regiment.Type.Attack + 8);

                long defence = (long)province.Militia * 20;
                defence += province.Landscape switch
                {
                    Landscape.Hills => defence / 3,
                    Landscape.Forest => defence / 4,
                    _ => defence / 10,
                };

                long total = Math.Max(1, attack + defence);
                int share = (int)(attack * 1000 / total) + random.NextInt(-70, 71);

                province.Militia -= Math.Max(1, province.Militia * Math.Clamp(share, 100, 900) / 1100);
                province.Militia = Math.Max(0, province.Militia);

                // Assaulting walls costs the besieger too, and more when the levy is
                // fresh. This is what stops one stack from besieging the world.
                Bleed(besieger, Fix.Ratio(Math.Clamp(11 - share / 120, 2, 11), 100), random);
            }

            if (province.Siege == 1)
                state.Record($"{besieger.Owner} lays siege to {province.Name}");
        }

        state.Armies.RemoveAll(a => a.IsDestroyed);
    }

    private static void Bleed(CampaignArmy army, Fix fraction, DetRandom random)
    {
        foreach (Regiment regiment in army.Regiments)
        {
            if (regiment.Strength <= 0) continue;
            int losses = (fraction * regiment.Strength).RoundToInt;
            regiment.Strength -= Math.Clamp(losses, 0, regiment.Strength);
        }
        army.BuryTheDead();
    }

    // -------------------------------------------------------------- occupation

    private static void ResolveOccupation(CampaignState state)
    {
        foreach (Province province in state.Provinces)
        {
            var present = state.ArmiesIn(province.Id).ToList();
            if (present.Count == 0) continue;

            Faction holder = present[0].Owner;
            if (present.Any(a => a.Owner != holder)) continue;   // still being fought over
            if (province.Owner == holder) continue;

            // The siege has to run its course. A province is not taken by standing on it
            // for an afternoon.
            if (province.Siege < province.SiegeLength || province.Militia > 0) continue;

            Faction? previous = province.Owner;
            province.Owner = holder;

            // Newly taken ground pays a quarter until it settles. Without this a fast
            // advance funds the next one and the campaign becomes a race the first mover
            // always wins.
            province.Unrest = 3;

            // The new owner posts a garrison of his own, but a thin one — freshly taken
            // ground is the easiest ground to take back, which is what makes an
            // over-extended advance punishable.
            province.Militia = province.MilitiaCap / 4;
            province.LiftSiege();

            state.Record(previous is { } lost
                ? $"{holder} takes {province.Name} from {lost}"
                : $"{holder} annexes {province.Name}");
        }
    }

    // ----------------------------------------------------------------- economy

    private static void CollectRevenue(CampaignState state)
    {
        foreach (CampaignPower power in state.Powers.Values)
        {
            if (power.Destroyed) continue;

            int income = 0;
            foreach (Province province in state.Held(power.Faction)) income += province.Income;

            int upkeep = 0;
            foreach (CampaignArmy army in state.Armies)
            {
                if (army.Owner != power.Faction) continue;
                foreach (Regiment regiment in army.Regiments)
                    upkeep += regiment.Type.Cost * regiment.Strength
                            / Math.Max(1, regiment.Establishment) / UpkeepDivisor;
            }

            power.Treasury += income - upkeep;

            // An army that cannot be paid goes home. This is the brake on the whole
            // campaign: without it every power recruits to the cap on turn one and the map
            // never moves again, because nobody can afford to lose anything.
            while (power.Treasury < 0)
            {
                CampaignArmy? worst = state.Armies
                    .Where(a => a.Owner == power.Faction && a.Regiments.Count > 0)
                    .OrderBy(a => a.Men)
                    .FirstOrDefault();

                if (worst == null) { power.Treasury = 0; break; }

                Regiment disbanded = worst.Regiments.OrderBy(r => r.Strength).First();
                worst.Regiments.Remove(disbanded);

                power.Treasury += disbanded.Type.Cost / UpkeepDivisor * 6;
                state.Record($"{power.Faction} cannot pay the {disbanded.Type.Name} and disbands them");
            }
        }

        state.Armies.RemoveAll(a => a.Regiments.Count == 0);
    }

    // ------------------------------------------------------------- recruitment

    private static void Recruit(CampaignState state)
    {
        foreach (CampaignPower power in state.Powers.Values)
        {
            if (power.Destroyed || power.IsPlayer) continue;
            CampaignAI.Recruit(state, power);
        }
    }

    // -------------------------------------------------------------- conclusion

    private static void CheckForTheEnd(CampaignState state)
    {
        foreach (CampaignPower power in state.Powers.Values)
        {
            if (power.Destroyed) continue;

            bool holdsNothing = state.ProvinceCount(power.Faction) == 0;
            bool fieldsNothing = !state.Armies.Any(a => a.Owner == power.Faction && !a.IsDestroyed);

            if (!holdsNothing || !fieldsNothing) continue;

            power.Destroyed = true;
            state.Record($"{power.Name} is finished");
        }
    }

    /// <summary>The winner, if there is one yet.</summary>
    public static Faction? Victor(CampaignState state)
    {
        var alive = state.Powers.Values.Where(p => !p.Destroyed).ToList();
        if (alive.Count == 1) return alive[0].Faction;

        foreach (CampaignPower power in alive)
            if (state.ProvinceCount(power.Faction) >= VictoryProvinces) return power.Faction;

        return null;
    }
}
