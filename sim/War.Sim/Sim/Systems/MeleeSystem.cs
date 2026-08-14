using War.Sim.Core;
using War.Sim.Units;
using War.Sim.World;

namespace War.Sim.Sim.Systems;

/// <summary>
/// Hand-to-hand combat: who is fighting whom, and what happens when they swing.
///
/// Every strike is one roll against a sum of legible modifiers. Nothing here is a magic
/// number buried in a formula — you can read a fight off the stat card, which is what
/// makes the game teachable and the balance tunable.
/// </summary>
public static class MeleeSystem
{
    /// <summary>
    /// How far past his reach a soldier will look for someone to fight. Must exceed the
    /// largest collision radius in the roster, so the search never misses an opponent
    /// that <see cref="ReachBetween"/> would consider in range.
    /// </summary>
    private static readonly Fix SearchMargin = Fix.FromInt(3);

    /// <summary>
    /// Melee reach between two specific units, measured surface to surface rather than
    /// centre to centre.
    ///
    /// This is not a refinement — it is load-bearing. An elephant has a collision radius
    /// of 1.5 metres and an infantryman 0.4, so separation holds their centres 1.9 metres
    /// apart, which is further than a 1.3 metre reach. Measured centre to centre, war
    /// elephants and chariots simply cannot touch infantry: they push the line around
    /// forever and never land a blow. A bigger animal strikes from further away because
    /// it is bigger.
    /// </summary>
    public static Fix ReachBetween(Unit attacker, Unit defender) =>
        attacker.Type.Reach
        + attacker.FormationProfile.ExtraReach
        + attacker.Type.Radius
        + defender.Type.Radius;

    /// <summary>Radius to search for an opponent, before the exact pairwise check.</summary>
    public static Fix SearchRadius(Unit attacker) =>
        attacker.Type.Reach
        + attacker.FormationProfile.ExtraReach
        + attacker.Type.Radius
        + SearchMargin;

    public static void Run(BattleState state)
    {
        foreach (Unit unit in state.Units) unit.Engaged = 0;

        AcquireTargets(state);
        Resolve(state);
        DecayCharges(state);
    }

    // --------------------------------------------------------------- targeting

    private static void AcquireTargets(BattleState state)
    {
        for (int s = 0; s < state.SoldierCount; s++)
        {
            if (state.State[s] == SoldierState.Dead) continue;

            Unit unit = state.UnitOf(s);
            if (unit.Withdrawn) continue;

            // Routing men do not fight. They are cut down from behind instead.
            if (unit.MoraleState == MoraleState.Routing)
            {
                state.MeleeTarget[s] = -1;
                continue;
            }

            // Keep an existing target if he is still alive and still within arm's length.
            int current = state.MeleeTarget[s];
            if (current >= 0 && state.IsAlive(current))
            {
                Fix currentReach = ReachBetween(unit, state.UnitOf(current));
                if (FixVec2.WithinDistance(state.Position[s], state.Position[current], currentReach + SearchMargin))
                {
                    if (FixVec2.WithinDistance(state.Position[s], state.Position[current], currentReach))
                        unit.Engaged++;
                    continue;
                }
            }

            int enemyArmy = 1 - unit.ArmyId;
            int found = state.HashFor(enemyArmy)
                .FindNearest(state.Position[s], SearchRadius(unit), state.Position);

            state.MeleeTarget[s] = found;
            if (found < 0) continue;

            bool inReach = FixVec2.WithinDistance(
                state.Position[s], state.Position[found], ReachBetween(unit, state.UnitOf(found)));
            if (inReach) unit.Engaged++;

            // A man who arrives at the run gets his charge bonus. One who was already
            // standing here does not — which is the entire reason to receive a charge
            // rather than deliver one.
            bool arrivedAtSpeed = state.State[s] is SoldierState.Moving or SoldierState.Charging;
            if (inReach && arrivedAtSpeed && state.ChargeTicks[s] == 0)
            {
                state.ChargeTicks[s] = SimConstants.Ticks(SimConstants.ChargeDecaySeconds);

                state.Raise(new BattleEvent
                {
                    Type = BattleEventType.ChargeImpact,
                    Position = state.Position[s],
                    Direction = state.Facing[s],
                    A = s,
                    B = unit.Id,
                });
            }
        }
    }

    // -------------------------------------------------------------- resolution

    private static void Resolve(BattleState state)
    {
        for (int s = 0; s < state.SoldierCount; s++)
        {
            if (state.State[s] == SoldierState.Dead) continue;

            if (state.AttackCooldown[s] > 0)
            {
                state.AttackCooldown[s]--;
                continue;
            }

            int target = state.MeleeTarget[s];
            if (target < 0 || !state.IsAlive(target)) continue;

            Unit attacker = state.UnitOf(s);
            if (attacker.Withdrawn || attacker.MoraleState == MoraleState.Routing) continue;

            Unit defender = state.UnitOf(target);
            if (!FixVec2.WithinDistance(state.Position[s], state.Position[target], ReachBetween(attacker, defender)))
                continue;

            Strike(state, s, target, attacker, defender);

            // Things that go through people rather than duelling them.
            if (attacker.Type.AttacksPerStrike > 1) Trample(state, s, attacker, target);

            // Jitter the next swing so a unit's casualties stream in rather than
            // arriving in synchronised pulses.
            int interval = SimConstants.Ticks(attacker.Type.AttackInterval * SimConstants.MeleeTempo);
            int jitter = interval / 4;
            state.AttackCooldown[s] = interval + state.RngMelee.NextInt(-jitter, jitter + 1);
        }
    }

    /// <summary>
    /// Extra victims for units that hit more than one man per swing. An elephant in
    /// contact with eight legionaries is not picking one of them and taking turns.
    /// </summary>
    private static void Trample(BattleState state, int soldier, Unit attacker, int primaryTarget)
    {
        int remaining = attacker.Type.AttacksPerStrike - 1;
        FixVec2 at = state.Position[soldier];

        int found = state.HashFor(1 - attacker.ArmyId)
            .Query(at, SearchRadius(attacker), state.Position, state.QueryScratch);

        for (int i = 0; i < found && remaining > 0; i++)
        {
            int victim = state.QueryScratch[i];
            if (victim == primaryTarget || !state.IsAlive(victim)) continue;

            Unit victimUnit = state.UnitOf(victim);
            if (!FixVec2.WithinDistance(at, state.Position[victim], ReachBetween(attacker, victimUnit)))
                continue;

            Strike(state, soldier, victim, attacker, victimUnit);
            remaining--;
        }
    }

    private static void Strike(BattleState state, int attackerSoldier, int defenderSoldier,
        Unit attacker, Unit defender)
    {
        Fix chance = HitChance(state, attackerSoldier, defenderSoldier, attacker, defender);

        if (!state.RngMelee.Chance(chance)) return;

        state.Health[defenderSoldier]--;
        if (state.Health[defenderSoldier] > 0) return;

        state.KillSoldier(defenderSoldier, attacker.Id);

        if (defender.IsGeneral && defender.Alive <= 1)
        {
            // Counted when the last of the bodyguard falls; MoraleSystem reads the flag.
            state.ArmyOf(defender).GeneralDead = true;
            state.Raise(new BattleEvent
            {
                Type = BattleEventType.GeneralKilled,
                Position = state.Position[defenderSoldier],
                A = defender.Id,
                B = defender.ArmyId,
            });
        }
    }

    /// <summary>
    /// The whole combat model in one place.
    ///
    /// offense = attack + charge + counter bonus + flank + high ground + formation − fatigue
    /// defence = skill + shield (front and left only) + armour + formation − fatigue
    /// chance  = 0.35 + 0.03 × (offense − defence), clamped to [0.05, 0.90]
    /// </summary>
    public static Fix HitChance(BattleState state, int attackerSoldier, int defenderSoldier,
        Unit attacker, Unit defender)
    {
        UnitType attackType = attacker.Type;
        UnitType defendType = defender.Type;

        FixVec2 attackerAt = state.Position[attackerSoldier];
        FixVec2 defenderAt = state.Position[defenderSoldier];

        // Where the blow is coming from, in the defender's frame of reference.
        //
        // Measured against the formation's facing, not the individual's. This is the
        // difference between flanking mattering and flanking being decorative: soldiers
        // turn to face whoever is hitting them, so scoring the arc per man cancels the
        // penalty within a second of contact and a rear attack ends up no better than a
        // frontal one. A body of men drawn up facing one way cannot all turn — the ranks
        // are in each other's way, the depth is on the wrong axis, and every shield is on
        // the wrong side. A unit *can* reorient, but only by turning as a unit, which
        // takes time proportional to its turn rate. That delay is the tactical opening.
        //
        // The exceptions are formations that genuinely face outward, and men who have
        // already broken and scattered; both are flanked individually, because there is
        // no formation left to flank.
        bool individually = defender.FormationProfile.AllRoundDefence
            || defender.MoraleState == MoraleState.Routing;

        FixVec2 defenderFacing = individually ? state.Facing[defenderSoldier] : defender.Facing;

        FixVec2 toAttacker = (attackerAt - defenderAt).Normalized;
        Fix facingDot = FixVec2.Dot(defenderFacing, toAttacker);
        bool fromFront = facingDot > SimConstants.FrontArcCosine;
        bool fromRear = facingDot < SimConstants.RearArcCosine;

        // Shields are carried in the left hand, so they cover the front and the left
        // side and are worth nothing to a man attacked from his right or behind.
        bool fromLeft = FixVec2.Cross(defenderFacing, toAttacker) > Fix.Zero;
        bool shielded = fromFront || fromLeft;

        // ---- offense

        Fix offense = Fix.FromInt(attackType.Attack);

        offense += ChargeBonus(state, attackerSoldier, attacker, defender, fromFront);

        offense += Fix.FromInt(defendType.IsMounted
            ? attackType.BonusVsMounted
            : attackType.BonusVsInfantry);

        if (fromRear) offense += SimConstants.RearAttackBonus;
        else if (!fromFront) offense += SimConstants.FlankAttackBonus;

        // High ground, scored from the slope underfoot rather than the elevation gap
        // between the two men — see SimConstants.SlopeCombatFactor for why that matters.
        // A positive climb means the defender is up the slope, so the attacker is
        // striking uphill and pays for it.
        FixVec2 upslope = (defenderAt - attackerAt).Normalized;
        Fix climb = FixVec2.Dot(state.Terrain.GradientAt(attackerAt), upslope);
        Fix maxHeight = Fix.FromInt(SimConstants.MaxHeightBonus);
        offense -= FixMath.Clamp(climb * SimConstants.SlopeCombatFactor, -maxHeight, maxHeight);

        offense += Fix.FromInt(attacker.FormationProfile.AttackBonus);
        offense -= state.Fatigue[attackerSoldier] * SimConstants.MaxFatiguePenalty;

        // Men who are losing heart fight worse, which is how a wavering unit spirals.
        if (attacker.MoraleState == MoraleState.Wavering) offense -= Fix.Two;

        // ---- defence

        Fix defence = Fix.FromInt(defendType.DefenceSkill);
        if (shielded) defence += Fix.FromInt(defendType.Shield);
        defence += Fix.FromInt(defendType.Armour);

        defence += Fix.FromInt(fromFront
            ? defender.FormationProfile.FrontDefenceBonus
            : defender.FormationProfile.FlankDefenceBonus);

        defence -= state.Fatigue[defenderSoldier] * SimConstants.MaxFatiguePenalty;

        if (defender.MoraleState == MoraleState.Wavering) defence -= Fix.Two;

        // A man who cannot see it coming barely defends himself at all.
        if (defender.MoraleState == MoraleState.Routing) defence -= Fix.FromInt(6);

        // ---- roll

        Fix chance = SimConstants.BaseHitChance
            + (offense - defence + SimConstants.DefenceOffset) * SimConstants.HitChancePerPoint;

        return FixMath.Clamp(chance, SimConstants.MinHitChance, SimConstants.MaxHitChance);
    }

    /// <summary>
    /// The charge bonus, decaying linearly over five seconds from impact.
    ///
    /// A phalanx negates it outright from the front — levelled pikes mean the charge
    /// arrives on the points rather than on the men, and that single rule is why you
    /// go round a phalanx instead of through it.
    /// </summary>
    private static Fix ChargeBonus(BattleState state, int attackerSoldier,
        Unit attacker, Unit defender, bool fromDefendersFront)
    {
        int ticks = state.ChargeTicks[attackerSoldier];
        if (ticks <= 0) return Fix.Zero;

        if (fromDefendersFront && defender.FormationProfile.NegatesFrontalCharge)
            return Fix.Zero;

        int maxTicks = SimConstants.Ticks(SimConstants.ChargeDecaySeconds);
        Fix remaining = Fix.Ratio(ticks, maxTicks);

        return Fix.FromInt(attacker.Type.Charge)
            * attacker.FormationProfile.ChargeScale
            * remaining;
    }

    private static void DecayCharges(BattleState state)
    {
        for (int s = 0; s < state.SoldierCount; s++)
            if (state.ChargeTicks[s] > 0) state.ChargeTicks[s]--;
    }
}
