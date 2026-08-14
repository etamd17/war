using War.Sim.Core;
using War.Sim.Units;
using War.Sim.World;

namespace War.Sim.Sim.Systems;

/// <summary>
/// Moves the army: anchors follow orders, men follow anchors, and everyone shoves
/// everyone else out of the way.
///
/// The formation is driven by an <em>anchor</em> — a notional point and facing that the
/// unit is drawn up on — rather than by the mean position of the men. This matters: if
/// the formation chased its own centre of mass, casualties on one flank would drag the
/// whole line sideways, and a unit shot at from the left would slowly walk left. The
/// anchor moves because it was ordered to, and the men move to keep up with it.
/// </summary>
public static class MovementSystem
{
    /// <summary>How much faster than the formation a straggler may run to catch up.</summary>
    private static readonly Fix CatchUpSpeed = Fix.Ratio(14, 10);

    /// <summary>A man this far from his slot counts as out of place when scoring cohesion.</summary>
    private static readonly Fix CohesionTolerance = Fix.Ratio(25, 10);

    /// <summary>Distance at which a charging unit commits and the men break into a run.</summary>
    private static readonly Fix ChargeRange = Fix.FromInt(45);

    /// <summary>Routers panic-run at this multiple of their normal run.</summary>
    private static readonly Fix PanicSpeed = Fix.Ratio(11, 10);

    public static void Run(BattleState state)
    {
        AdvanceAnchors(state);
        ResolveGoals(state);
        ComputeSeparation(state);
        ApplyMovement(state);
        ScoreCohesion(state);
    }

    // --------------------------------------------------------------- anchors

    /// <summary>Moves each unit's anchor according to its order.</summary>
    private static void AdvanceAnchors(BattleState state)
    {
        foreach (Unit unit in state.Units)
        {
            if (unit.IsOutOfAction || unit.Alive == 0) continue;

            if (unit.MoraleState == MoraleState.Routing)
            {
                AdvanceRoutingAnchor(state, unit);
                continue;
            }

            switch (unit.Order.Type)
            {
                case OrderType.Hold:
                    // Nothing to do — but still turn to face an enemy we are fighting,
                    // so a unit charged in the flank does not stand there sideways.
                    TurnAnchorToward(state, unit, ThreatDirection(state, unit));
                    break;

                case OrderType.MoveTo:
                    MoveAnchorTo(state, unit, unit.Order.Position, unit.Order.Facing, unit.Order.Run);
                    break;

                case OrderType.Attack:
                    AdvanceAttackAnchor(state, unit);
                    break;

                case OrderType.Withdraw:
                    // Fall back facing the way we came, so the men are not caught with
                    // their backs turned if the enemy catches up.
                    FixVec2 away = (unit.Order.Position - unit.Anchor).Normalized;
                    MoveAnchorTo(state, unit, unit.Order.Position, -away, run: true);
                    break;
            }
        }
    }

    private static void AdvanceAttackAnchor(BattleState state, Unit unit)
    {
        int targetId = unit.Order.TargetUnit;
        if (targetId < 0 || targetId >= state.Units.Length)
        {
            unit.Order = UnitOrder.Hold();
            return;
        }

        Unit target = state.Units[targetId];
        if (target.IsOutOfAction)
        {
            // Target is gone. Stand where we are rather than charging off the map.
            unit.Order = UnitOrder.Hold();
            return;
        }

        FixVec2 toTarget = target.Centre - unit.Anchor;
        FixVec2 direction = toTarget.Normalized;
        if (direction.IsZero) direction = unit.AnchorFacing;

        // Our anchor is our own front rank, so we stop it at the near edge of the
        // target's footprint. ExtentAlong measures that edge properly for the direction
        // we are actually coming from — half-depth head-on, half-frontage from the
        // flank, and the right blend in between.
        Fix standOff = target.ExtentAlong(direction) + Fix.One;
        FixVec2 destination = target.Centre - direction * standOff;

        // Once in contact, stop pushing the anchor forward — the fight decides the
        // ground now, and a still-advancing anchor drags men out of the melee.
        if (unit.InContact)
        {
            TurnAnchorToward(state, unit, direction);
            return;
        }

        bool charging = FixVec2.Distance(unit.Anchor, target.Centre) < ChargeRange + standOff;
        MoveAnchorTo(state, unit, destination, direction, unit.Order.Run || charging);
    }

    private static void AdvanceRoutingAnchor(BattleState state, Unit unit)
    {
        // Run for the friendly rear, biased away from whatever is closest and dangerous.
        Army army = state.ArmyOf(unit);
        FixVec2 home = army.DeploymentCentre - army.AdvanceDirection * Fix.FromInt(400);
        FixVec2 away = (home - unit.Centre).Normalized;

        FixVec2 threat = ThreatDirection(state, unit);
        if (!threat.IsZero) away = (away - threat).Normalized;
        if (away.IsZero) away = -army.AdvanceDirection;

        Fix speed = RoutSpeed(state, unit, away);
        unit.Anchor += away * speed;
        unit.AnchorFacing = away;
        unit.Facing = away;
    }

    /// <summary>Unit vector toward the nearest enemy unit in contact or close by, or zero.</summary>
    private static FixVec2 ThreatDirection(BattleState state, Unit unit)
    {
        int enemyArmy = 1 - unit.ArmyId;
        SpatialHash hash = state.HashFor(enemyArmy);

        int nearest = hash.FindNearest(unit.Centre, Fix.FromInt(60), state.Position);
        if (nearest < 0) return FixVec2.Zero;

        return (state.Position[nearest] - unit.Centre).Normalized;
    }

    private static void MoveAnchorTo(BattleState state, Unit unit, FixVec2 destination, FixVec2 facing, bool run)
    {
        FixVec2 toDestination = destination - unit.Anchor;
        Fix distance = toDestination.Magnitude;

        FixVec2 direction = toDestination.Normalized;
        if (direction.IsZero) direction = unit.AnchorFacing;

        if (distance > Fix.Ratio(1, 4))
        {
            Fix speed = UnitSpeed(state, unit, direction, run);

            // A unit that has left its stragglers behind slows down for them, so a line
            // arrives as a line rather than as a trickle of men.
            speed *= FixMath.Clamp(unit.Cohesion, Fix.Ratio(45, 100), Fix.One);

            unit.Anchor = state.Terrain.ClampToBounds(
                unit.Anchor + direction * FixMath.Min(speed, distance));

            // While marching any real distance the formation faces where it is going.
            if (distance > Fix.FromInt(4)) facing = direction;
        }

        TurnAnchorToward(state, unit, facing);
    }

    /// <summary>How much of its turn rate a unit keeps once it is locked in melee.</summary>
    private static readonly Fix EngagedTurnScale = Fix.Ratio(18, 100);

    private static void TurnAnchorToward(BattleState state, Unit unit, FixVec2 desired)
    {
        if (desired.IsZero) return;

        FixVec2 step = unit.Type.TurnStepPerTick;

        // A formation already fighting cannot simply wheel. The front rank is engaged,
        // the ranks behind are in each other's way, and there is nowhere to step to.
        //
        // This rule is what makes flanking worth anything. Without it, any unit hit from
        // the side just pivots to face the attacker within a second, the flank bonus
        // evaporates, and a rear attack ends up no more effective than a frontal one —
        // which is exactly what the numbers showed before it existed. Pin a unit to its
        // front and its flank stays open for several seconds; leave it unengaged and it
        // will quite correctly turn to meet you.
        if (unit.InContact)
            step = FixVec2.FromAngle(unit.Type.TurnRate * SimConstants.TickSeconds * EngagedTurnScale);

        unit.AnchorFacing = FixVec2.TurnTowards(unit.AnchorFacing, desired, step);
        unit.Facing = unit.AnchorFacing;
    }

    /// <summary>Distance a unit's anchor may travel this tick, after every modifier.</summary>
    private static Fix UnitSpeed(BattleState state, Unit unit, FixVec2 direction, bool run)
    {
        Fix baseSpeed = run ? unit.Type.RunSpeed : unit.Type.WalkSpeed;

        Fix speed = baseSpeed
            * unit.FormationProfile.SpeedScale
            * (Fix.One - unit.Fatigue * SimConstants.FatigueSpeedLoss)
            * state.Terrain.SpeedMultiplierAt(unit.Anchor, direction);

        // Men who are losing heart do not march briskly.
        if (unit.MoraleState == MoraleState.Wavering) speed *= Fix.Ratio(85, 100);
        if (unit.MoraleState == MoraleState.Rallying) speed *= Fix.Ratio(7, 10);

        return speed * SimConstants.TickSeconds;
    }

    private static Fix RoutSpeed(BattleState state, Unit unit, FixVec2 direction)
    {
        // Panic outruns exhaustion, but only for a while.
        Fix speed = unit.Type.RunSpeed * PanicSpeed
            * (Fix.One - unit.Fatigue * Fix.Ratio(25, 100))
            * state.Terrain.SpeedMultiplierAt(unit.Anchor, direction);

        return speed * SimConstants.TickSeconds;
    }

    // ------------------------------------------------------------------ goals

    /// <summary>Works out where every living soldier is trying to be this tick.</summary>
    private static void ResolveGoals(BattleState state)
    {
        for (int s = 0; s < state.SoldierCount; s++)
        {
            if (state.State[s] == SoldierState.Dead) continue;

            Unit unit = state.UnitOf(s);
            if (unit.Withdrawn) continue;

            if (unit.MoraleState == MoraleState.Routing)
            {
                state.State[s] = SoldierState.Routing;
                state.MeleeTarget[s] = -1;
                // Routers scatter rather than keeping ranks, so they spread as they flee.
                state.GoalScratch[s] = unit.Anchor + Spread(state, s, Fix.FromInt(8));
                continue;
            }

            int target = state.MeleeTarget[s];
            if (target >= 0 && state.IsAlive(target))
            {
                state.GoalScratch[s] = state.Position[target];
                continue;
            }

            int slot = state.Slot[s];
            state.GoalScratch[s] = slot < 0 ? state.Position[s] : state.SlotPosition(unit, slot);
        }
    }

    /// <summary>
    /// A stable per-soldier offset, so scattering routers spread out instead of piling
    /// onto one point — and do it the same way on every replay.
    /// </summary>
    private static FixVec2 Spread(BattleState state, int soldier, Fix radius)
    {
        // Derived from the soldier id rather than drawn from an RNG, so it costs nothing
        // and cannot desynchronise.
        uint h = (uint)soldier * 2654435761u;
        Fix angle = Fix.Ratio((int)(h >> 20), 4096) * Fix.TwoPi;
        Fix distance = Fix.Ratio((int)((h >> 8) & 0xFFF), 4096) * radius;
        return FixVec2.FromAngle(angle) * distance;
    }

    // ------------------------------------------------------------- separation

    /// <summary>
    /// Soldiers push each other apart. This is not physics — it is just enough shoving
    /// to stop men occupying the same ground, which is what makes a line look like a
    /// line and lets a heavy unit bodily displace a light one.
    /// </summary>
    private static void ComputeSeparation(BattleState state)
    {
        Array.Clear(state.PushScratch);
        Fix radius = SimConstants.SeparationRadius;

        for (int s = 0; s < state.SoldierCount; s++)
        {
            if (state.State[s] == SoldierState.Dead) continue;

            Unit unit = state.UnitOf(s);
            if (unit.Withdrawn) continue;

            FixVec2 position = state.Position[s];
            Fix myRadius = unit.Type.Radius;
            Fix myMass = unit.Type.Mass;
            FixVec2 push = FixVec2.Zero;

            // Check against both armies: men jostle friends and enemies alike.
            for (int army = 0; army < state.Armies.Length; army++)
            {
                SpatialHash hash = state.HashFor(army);
                hash.CellRange(position, radius, out int minX, out int minY, out int maxX, out int maxY);

                for (int cy = minY; cy <= maxY; cy++)
                {
                    for (int cx = minX; cx <= maxX; cx++)
                    {
                        foreach (int other in hash.CellItems(cx, cy))
                        {
                            if (other == s) continue;

                            FixVec2 delta = position - state.Position[other];
                            long sqr = delta.SqrMagnitudeRaw;

                            Fix minimum = myRadius + state.UnitOf(other).Type.Radius;
                            if (sqr >= FixMath.SqrRaw(minimum)) continue;

                            Fix otherMass = state.UnitOf(other).Type.Mass;

                            FixVec2 direction;
                            if (sqr == 0)
                            {
                                // Exactly co-located. Push apart along a stable, id-derived
                                // direction rather than picking one at random.
                                direction = Spread(state, s, Fix.One).Normalized;
                                if (direction.IsZero) direction = FixVec2.East;
                            }
                            else
                            {
                                direction = delta.Normalized;
                            }

                            Fix overlap = minimum - Fix.FromRaw((int)FixMath.ISqrt64((ulong)sqr));

                            // The lighter man gives way. An elephant at mass 12 barely
                            // notices the infantryman it is walking through.
                            Fix share = otherMass / (myMass + otherMass);
                            push += direction * (overlap * share);
                        }
                    }
                }
            }

            state.PushScratch[s] = push.ClampMagnitude(Fix.Ratio(6, 10));
        }
    }

    // ---------------------------------------------------------------- movement

    private static void ApplyMovement(BattleState state)
    {
        for (int s = 0; s < state.SoldierCount; s++)
        {
            state.PreviousPosition[s] = state.Position[s];

            if (state.State[s] == SoldierState.Dead) continue;

            Unit unit = state.UnitOf(s);
            if (unit.Withdrawn) continue;

            UnitType type = unit.Type;
            FixVec2 position = state.Position[s];
            FixVec2 goal = state.GoalScratch[s];
            FixVec2 toGoal = goal - position;
            Fix distance = toGoal.Magnitude;

            bool routing = unit.MoraleState == MoraleState.Routing;
            int meleeTarget = state.MeleeTarget[s];

            // In contact and within reach: stand and fight rather than shuffling. Reach
            // is pairwise — see MeleeSystem.ReachBetween — so a big animal stops at the
            // distance it can actually strike from rather than walking into the line.
            bool standingToFight = meleeTarget >= 0 && !routing && state.IsAlive(meleeTarget) &&
                distance <= MeleeSystem.ReachBetween(unit, state.UnitOf(meleeTarget));

            FixVec2 step = FixVec2.Zero;

            if (!standingToFight && distance > Fix.Ratio(1, 10))
            {
                FixVec2 direction = toGoal.Normalized;

                bool run = routing
                    || unit.Order.Run
                    || meleeTarget >= 0
                    || distance > Fix.FromInt(12);

                Fix speed = routing
                    ? RoutSpeed(state, unit, direction)
                    : UnitSpeed(state, unit, direction, run) * CatchUpSpeed;

                step = direction * FixMath.Min(speed, distance);
            }

            FixVec2 next = position + step + state.PushScratch[s];

            // Routers are allowed off the map — leaving is how they quit the battle.
            if (!routing) next = state.Terrain.ClampToBounds(next);

            state.Position[s] = next;

            UpdateFacing(state, s, unit, step, meleeTarget, routing);
            UpdateState(state, s, unit, distance, standingToFight, routing, step);
        }
    }

    private static void UpdateFacing(
        BattleState state, int soldier, Unit unit, FixVec2 step, int meleeTarget, bool routing)
    {
        FixVec2 desired;

        if (routing)
        {
            desired = step.IsZero ? unit.AnchorFacing : step.Normalized;
        }
        else if (meleeTarget >= 0 && state.IsAlive(meleeTarget))
        {
            // Fighting men face what they are fighting, which is what exposes a unit's
            // flank when it gets attacked from two sides at once.
            desired = (state.Position[meleeTarget] - state.Position[soldier]).Normalized;
        }
        else if (unit.FormationProfile.AllRoundDefence)
        {
            // A square faces outward: every man turns his back to the centre.
            desired = (state.Position[soldier] - unit.Anchor).Normalized;
            if (desired.IsZero) desired = unit.AnchorFacing;
        }
        else
        {
            // Formed troops face the way the formation faces, not the way they are
            // walking — a line stepping sideways still presents its shields forward.
            desired = unit.AnchorFacing;
        }

        state.Facing[soldier] = FixVec2.TurnTowards(
            state.Facing[soldier], desired, unit.Type.TurnStepPerTick);
    }

    private static void UpdateState(
        BattleState state, int soldier, Unit unit,
        Fix distanceToGoal, bool standingToFight, bool routing, FixVec2 step)
    {
        if (routing)
        {
            state.State[soldier] = SoldierState.Routing;
            return;
        }

        if (standingToFight)
        {
            state.State[soldier] = SoldierState.Fighting;
            return;
        }

        if (state.ChargeTicks[soldier] > 0)
        {
            state.State[soldier] = SoldierState.Charging;
            return;
        }

        if (state.MeleeTarget[soldier] >= 0 || distanceToGoal > Fix.Two)
        {
            state.State[soldier] = SoldierState.Moving;
            return;
        }

        state.State[soldier] = SoldierState.Formed;
    }

    // --------------------------------------------------------------- cohesion

    /// <summary>
    /// Scores how well each unit is actually holding its shape: the fraction of living
    /// men standing near their slot. Cohesion throttles the anchor so a unit does not
    /// run away from its own stragglers, and feeds the morale calculation — a formation
    /// that has come apart is a formation that is about to break.
    /// </summary>
    private static void ScoreCohesion(BattleState state)
    {
        foreach (Unit unit in state.Units)
        {
            if (unit.IsOutOfAction || unit.Alive == 0)
            {
                unit.Cohesion = Fix.Zero;
                continue;
            }

            if (unit.MoraleState == MoraleState.Routing)
            {
                unit.Cohesion = Fix.Ratio(2, 10);
                continue;
            }

            // Men locked in melee are where the fight put them, not where the diagram
            // says; scoring them as out of place would report a winning unit as broken.
            if (unit.InContact)
            {
                unit.Cohesion = FixMath.Max(unit.Cohesion, Fix.Ratio(7, 10));
                continue;
            }

            int inPlace = 0;
            for (int s = unit.FirstSoldier; s < unit.EndSoldier; s++)
            {
                if (state.State[s] == SoldierState.Dead) continue;
                int slot = state.Slot[s];
                if (slot < 0) continue;

                if (FixVec2.WithinDistance(state.Position[s], state.SlotPosition(unit, slot), CohesionTolerance))
                    inPlace++;
            }

            Fix cohesion = Fix.Ratio(inPlace, unit.Alive);

            // Woods break formations up regardless of how well drilled the men are.
            Fix forest = state.Terrain.ForestAt(unit.Centre);
            cohesion *= Fix.One - forest * Fix.Ratio(35, 100);

            unit.Cohesion = FixMath.Clamp01(cohesion);
        }
    }
}
