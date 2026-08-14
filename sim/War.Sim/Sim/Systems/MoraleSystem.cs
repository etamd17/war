using War.Sim.Core;
using War.Sim.Units;

namespace War.Sim.Sim.Systems;

/// <summary>
/// Morale: the system that actually decides battles.
///
/// Units almost never fight to the last man. They take losses, watch the unit beside them
/// disintegrate, notice horsemen appearing behind their right flank, and leave. Almost
/// every casualty in an ancient battle came after one side broke, not before — so a model
/// that resolves combat honestly and morale badly will produce battles that feel nothing
/// like the real thing regardless of how good the swordfighting is.
///
/// Morale runs 0 to 100. Every contribution below is a separate, readable term, and the
/// result is a target that the unit's current morale chases — quickly downward, slowly
/// upward. Panic is fast; recovering your nerve is not.
/// </summary>
public static class MoraleSystem
{
    private static readonly Fix FallRatePerSecond = Fix.FromInt(45);
    private static readonly Fix RiseRatePerSecond = Fix.FromInt(12);

    /// <summary>Within this distance a unit counts as being in contact for morale purposes.</summary>
    private static readonly Fix ContactRadius = Fix.FromInt(12);

    private static readonly Fix FearRadius = Fix.FromInt(45);

    public static void Run(BattleState state)
    {
        DecayRecentTallies(state);

        foreach (Unit unit in state.Units)
        {
            if (unit.IsOutOfAction || unit.Alive == 0) continue;

            Fix target = ComputeTarget(state, unit);
            ChaseTarget(unit, target);
            UpdateState(state, unit);
        }
    }

    /// <summary>
    /// "Recent" losses and kills fade, so a unit that took a mauling two minutes ago and
    /// has since steadied is not permanently marked by it.
    /// </summary>
    private static void DecayRecentTallies(BattleState state)
    {
        if (state.Tick % 15 != 0) return;

        foreach (Unit unit in state.Units)
        {
            unit.RecentLosses -= unit.RecentLosses / 2;
            unit.RecentKills -= unit.RecentKills / 2;
        }
    }

    // ------------------------------------------------------------------ target

    private static Fix ComputeTarget(BattleState state, Unit unit)
    {
        Army army = state.ArmyOf(unit);

        // Base standing, from the unit's own quality. Morale 4 lands near 50, morale 18
        // near 100, so even a levy starts the day willing to fight.
        Fix morale = Fix.FromInt(35) + Fix.FromInt(unit.Type.Morale) * Fix.Ratio(36, 10);

        morale += CasualtyPenalty(unit);
        morale += MomentumTerm(unit);
        morale += FlankingPenalty(state, unit);
        morale += GeneralTerm(state, unit, army);
        morale += NearbyRoutsTerm(state, unit);
        morale += LocalOddsTerm(state, unit);
        morale += FearTerm(state, unit);
        morale += GroundTerm(state, unit);

        // Exhausted men lose heart.
        morale -= unit.Fatigue * Fix.FromInt(25);

        // A formation that has come apart is a formation about to break.
        morale += (unit.Cohesion - Fix.One) * Fix.FromInt(15);

        // Discipline is a floor as well as a bonus: drilled troops hold when levies would not.
        morale += Fix.FromInt(unit.Type.Discipline);

        return FixMath.Clamp(morale, Fix.Zero, Fix.FromInt(100));
    }

    /// <summary>
    /// Losses hurt more than linearly. A unit at 20% casualties is annoyed; a unit at
    /// 60% has watched most of the men it trained with die and is nearly finished.
    /// </summary>
    private static Fix CasualtyPenalty(Unit unit)
    {
        Fix lost = Fix.One - unit.StrengthFraction;
        return -(lost * lost * Fix.FromInt(40) + lost * Fix.FromInt(30));
    }

    /// <summary>Whether this unit feels like it is winning or losing right now.</summary>
    private static Fix MomentumTerm(Unit unit)
    {
        Fix losing = Fix.FromInt(unit.RecentLosses) * Fix.Ratio(15, 10);
        Fix winning = Fix.FromInt(unit.RecentKills);
        return FixMath.Clamp(winning - losing, -Fix.FromInt(25), Fix.FromInt(12));
    }

    /// <summary>
    /// Being hit from a direction you are not facing. This is the single biggest swing in
    /// the model, and it is why manoeuvre beats statistics: the same two units produce a
    /// completely different result depending on which way one of them is pointing.
    ///
    /// The arc is measured against the unit's facing rather than each man's, because what
    /// panics a formation is the line being turned, not one soldier pivoting to defend
    /// himself.
    /// </summary>
    private static Fix FlankingPenalty(BattleState state, Unit unit)
    {
        if (!unit.InContact) return Fix.Zero;

        int flanked = 0;
        int rear = 0;
        int engaged = 0;

        for (int s = unit.FirstSoldier; s < unit.EndSoldier; s++)
        {
            if (state.State[s] == SoldierState.Dead) continue;

            int attacker = state.MeleeTarget[s];
            if (attacker < 0 || !state.IsAlive(attacker)) continue;

            engaged++;

            FixVec2 toEnemy = (state.Position[attacker] - unit.Centre).Normalized;
            Fix dot = FixVec2.Dot(unit.Facing, toEnemy);

            if (dot < SimConstants.RearArcCosine) rear++;
            else if (dot < Fix.Ratio(3, 10)) flanked++;
        }

        if (engaged == 0) return Fix.Zero;

        Fix flankShare = Fix.Ratio(flanked, engaged);
        Fix rearShare = Fix.Ratio(rear, engaged);

        return -(flankShare * Fix.FromInt(20) + rearShare * Fix.FromInt(35));
    }

    /// <summary>
    /// The general steadies everything near him, and his death is felt across the army.
    /// This is what makes a commander a target rather than a stat bonus.
    /// </summary>
    private static Fix GeneralTerm(BattleState state, Unit unit, Army army)
    {
        if (army.GeneralDead) return -Fix.FromInt(20);
        if (army.GeneralUnit < 0) return Fix.Zero;

        Unit general = state.Units[army.GeneralUnit];
        if (general.IsOutOfAction || general.Alive == 0) return -Fix.FromInt(20);

        if (general.Id == unit.Id) return Fix.FromInt(8);

        // A general who has run away is steadying nobody.
        if (general.MoraleState == MoraleState.Routing) return -Fix.FromInt(10);

        if (!FixVec2.WithinDistance(unit.Centre, general.Centre, SimConstants.GeneralAuraRadius))
            return Fix.Zero;

        return Fix.FromInt(12);
    }

    /// <summary>
    /// Panic is contagious and so is confidence. Watching the unit next to you break is
    /// how a line unravels from one weak point, which is exactly what should happen.
    /// </summary>
    private static Fix NearbyRoutsTerm(BattleState state, Unit unit)
    {
        Fix penalty = Fix.Zero;
        Fix bonus = Fix.Zero;

        foreach (Unit other in state.Units)
        {
            if (other.Id == unit.Id || other.IsOutOfAction || other.Alive == 0) continue;
            if (other.MoraleState != MoraleState.Routing) continue;
            if (!FixVec2.WithinDistance(unit.Centre, other.Centre, SimConstants.RoutContagionRadius))
                continue;

            if (other.ArmyId == unit.ArmyId) penalty += Fix.FromInt(10);
            else bonus += Fix.FromInt(8);
        }

        return FixMath.Min(bonus, Fix.FromInt(16)) - FixMath.Min(penalty, Fix.FromInt(30));
    }

    /// <summary>
    /// Being locally outnumbered. Not the army-wide count — what matters is how many
    /// enemies are visibly around this particular unit right now.
    /// </summary>
    private static Fix LocalOddsTerm(BattleState state, Unit unit)
    {
        Fix radius = SimConstants.LocalOddsRadius;

        int friends = state.HashFor(unit.ArmyId).CountWithin(unit.Centre, radius, state.Position);
        int enemies = state.HashFor(1 - unit.ArmyId).CountWithin(unit.Centre, radius, state.Position);

        if (enemies == 0) return Fix.FromInt(4);
        if (friends >= enemies) return Fix.Zero;

        // At even odds this is zero; at three to one against it is a serious penalty.
        Fix ratio = Fix.Ratio(friends, enemies);
        return -(Fix.One - ratio) * Fix.FromInt(22);
    }

    /// <summary>
    /// Elephants and chariots. Horses in particular will not stand in front of an
    /// elephant, which is the whole tactical point of fielding one.
    /// </summary>
    private static Fix FearTerm(BattleState state, Unit unit)
    {
        if (unit.Type.ImmuneToFear) return Fix.Zero;

        Fix fear = Fix.Zero;

        foreach (int unitId in state.Armies[1 - unit.ArmyId].UnitIds)
        {
            Unit enemy = state.Units[unitId];
            if (!enemy.Type.CausesFear || enemy.IsOutOfAction || enemy.Alive == 0) continue;
            if (enemy.MoraleState == MoraleState.Routing) continue;
            if (!FixVec2.WithinDistance(unit.Centre, enemy.Centre, FearRadius)) continue;

            fear += Fix.FromInt(15);

            // Horses panic worse than men do.
            if (unit.Type.IsMounted && enemy.Type.Class == UnitClass.Elephant)
                fear += Fix.FromInt(10);
        }

        return -FixMath.Min(fear, Fix.FromInt(30));
    }

    /// <summary>Standing on the high ground is worth something in itself.</summary>
    private static Fix GroundTerm(BattleState state, Unit unit)
    {
        int nearestEnemy = state.HashFor(1 - unit.ArmyId)
            .FindNearest(unit.Centre, Fix.FromInt(120), state.Position);
        if (nearestEnemy < 0) return Fix.Zero;

        Fix advantage = state.Terrain.HeightAdvantage(unit.Centre, state.Position[nearestEnemy]);
        return FixMath.Clamp(advantage, -Fix.FromInt(6), Fix.FromInt(6));
    }

    // ------------------------------------------------------------------- chase

    /// <summary>
    /// Morale falls fast and rises slowly. A unit can lose its nerve in a couple of
    /// seconds when its flank goes; getting it back takes the better part of a minute.
    /// </summary>
    private static void ChaseTarget(Unit unit, Fix target)
    {
        Fix rate = target < unit.Morale ? FallRatePerSecond : RiseRatePerSecond;
        unit.Morale = FixMath.MoveTowards(unit.Morale, target, rate * SimConstants.TickSeconds);
    }

    // ------------------------------------------------------------ state machine

    private static void UpdateState(BattleState state, Unit unit)
    {
        if (unit.MoraleState == MoraleState.Routing)
        {
            UpdateRouting(state, unit);
            return;
        }

        if (unit.Morale < SimConstants.MoraleBreakThreshold)
        {
            unit.BreakTicks++;

            // A confirmation window, so one dreadful second does not rout a unit that
            // would have held. Disciplined troops need longer to be convinced.
            int required = SimConstants.BreakConfirmTicks + unit.Type.Discipline * 2;
            if (unit.BreakTicks >= required)
            {
                Break(state, unit);
                return;
            }
        }
        else
        {
            unit.BreakTicks = unit.BreakTicks > 2 ? unit.BreakTicks - 2 : 0;
        }

        unit.MoraleState = unit.Morale < SimConstants.MoraleWaveringThreshold
            ? MoraleState.Wavering
            : unit.MoraleState == MoraleState.Rallying && unit.Morale < Fix.FromInt(45)
                ? MoraleState.Rallying
                : MoraleState.Steady;
    }

    private static void Break(BattleState state, Unit unit)
    {
        unit.MoraleState = MoraleState.Routing;
        unit.BreakTicks = 0;
        unit.UnmolestedTicks = 0;
        unit.Engaged = 0;

        for (int s = unit.FirstSoldier; s < unit.EndSoldier; s++)
        {
            if (state.State[s] == SoldierState.Dead) continue;
            state.MeleeTarget[s] = -1;
            state.ChargeTicks[s] = 0;
            state.State[s] = SoldierState.Routing;
        }

        state.Raise(new BattleEvent
        {
            Type = BattleEventType.UnitBroke,
            Position = unit.Centre,
            Direction = unit.Facing,
            A = unit.Id,
            B = unit.ArmyId,
        });
    }

    private static void UpdateRouting(BattleState state, Unit unit)
    {
        // Off the map and gone for good. Chasing them further is not worth your cavalry.
        if (state.Terrain.DistanceOutsideBounds(unit.Centre) > SimConstants.WithdrawDistance)
        {
            unit.Withdrawn = true;
            state.Raise(new BattleEvent
            {
                Type = BattleEventType.UnitDestroyed,
                Position = unit.Centre,
                A = unit.Id,
                B = unit.ArmyId,
            });
            return;
        }

        // Rallying needs breathing room: nobody chasing, nobody dying.
        bool harried = unit.RecentLosses > 0 ||
            state.HashFor(1 - unit.ArmyId).CountWithin(unit.Centre, ContactRadius, state.Position) > 0;

        unit.UnmolestedTicks = harried ? 0 : unit.UnmolestedTicks + 1;

        if (unit.Morale < SimConstants.MoraleRallyThreshold) return;
        if (unit.UnmolestedTicks < SimConstants.Ticks(SimConstants.RallyCooldownSeconds)) return;

        // Even then it is a roll, weighted by how well drilled the men are. Levies mostly
        // keep running.
        if (!state.RngRout.Chance(Fix.Ratio(unit.Type.Discipline + 2, 40))) return;

        unit.MoraleState = MoraleState.Rallying;
        unit.Order = UnitOrder.Hold();
        unit.Anchor = unit.Centre;
        unit.SlotsBuiltFor = -1;

        for (int s = unit.FirstSoldier; s < unit.EndSoldier; s++)
            if (state.State[s] != SoldierState.Dead) state.State[s] = SoldierState.Formed;

        state.Raise(new BattleEvent
        {
            Type = BattleEventType.UnitRallied,
            Position = unit.Centre,
            A = unit.Id,
            B = unit.ArmyId,
        });
    }
}
