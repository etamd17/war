using War.Sim.Core;
using War.Sim.Units;
using War.Sim.World;

namespace War.Sim.Sim.Systems;

/// <summary>
/// Archery, slinging, and throwing.
///
/// Missiles are simulated as objects in flight rather than as instant hits, and they are
/// resolved against whoever is actually standing at the impact point when they arrive —
/// not against the man they were aimed at. That one decision buys a great deal for free:
/// shots lead moving targets badly, dense formations catch far more than loose ones, and
/// shooting into a melee kills your own men. None of those are special cases in the code.
/// </summary>
public static class MissileSystem
{
    private static Fix SpeedOf(MissileType type) => type switch
    {
        MissileType.Bow => Fix.FromInt(55),
        MissileType.Sling => Fix.FromInt(45),
        MissileType.Javelin => Fix.FromInt(22),
        MissileType.Pilum => Fix.FromInt(18),
        _ => Fix.FromInt(40),
    };

    public static void Run(BattleState state)
    {
        AdvanceMissiles(state);
        Fire(state);
    }

    // ------------------------------------------------------------------ firing

    private static void Fire(BattleState state)
    {
        foreach (Unit unit in state.Units)
        {
            if (unit.IsOutOfAction || unit.Alive == 0) continue;
            if (!unit.Type.HasMissiles || !unit.FireAtWill) continue;

            // You cannot shoot while men are hacking at you, and you do not shoot while
            // running for your life.
            if (unit.InContact || unit.MoraleState == MoraleState.Routing) continue;

            Unit? target = ChooseTarget(state, unit);
            if (target == null) continue;

            for (int s = unit.FirstSoldier; s < unit.EndSoldier; s++)
            {
                if (state.State[s] == SoldierState.Dead) continue;

                if (state.ReloadCooldown[s] > 0)
                {
                    state.ReloadCooldown[s]--;
                    continue;
                }

                if (state.Ammo[s] <= 0) continue;
                if (state.MissileCount >= state.Missiles.Length) return;

                int mark = PickMark(state, target);
                if (mark < 0) break;

                Loose(state, s, unit, target, mark);
            }
        }
    }

    /// <summary>
    /// Nearest enemy unit that is in range and can actually be seen. Line of sight is a
    /// real check against the terrain, so a unit behind a ridge or inside a wood simply
    /// is not a target.
    /// </summary>
    private static Unit? ChooseTarget(BattleState state, Unit shooter)
    {
        Unit? best = null;
        long bestSqr = long.MaxValue;
        Fix range = shooter.Type.MissileRange;

        // High ground carries a shot further.
        Fix height = state.Terrain.HeightAt(shooter.Centre);

        // A target the commander picked wins outright, provided it can actually be shot.
        int preferred = shooter.PreferredMissileTarget;
        if (preferred >= 0 && preferred < state.Units.Length)
        {
            Unit wanted = state.Units[preferred];
            if (!wanted.IsOutOfAction && wanted.Alive > 0 &&
                FixVec2.WithinDistance(shooter.Centre, wanted.Centre, range) &&
                state.Terrain.HasLineOfSight(shooter.Centre, wanted.Centre, Fix.Ratio(17, 10)))
            {
                return wanted;
            }
        }

        foreach (int unitId in state.Armies[1 - shooter.ArmyId].UnitIds)
        {
            Unit candidate = state.Units[unitId];
            if (candidate.IsOutOfAction || candidate.Alive == 0) continue;

            Fix effectiveRange = range +
                FixMath.Clamp(height - state.Terrain.HeightAt(candidate.Centre),
                    -Fix.FromInt(20), Fix.FromInt(20));

            long sqr = FixVec2.SqrDistanceRaw(shooter.Centre, candidate.Centre);
            if (sqr > FixMath.SqrRaw(effectiveRange)) continue;
            if (sqr >= bestSqr) continue;

            if (!state.Terrain.HasLineOfSight(shooter.Centre, candidate.Centre, Fix.Ratio(17, 10)))
                continue;

            bestSqr = sqr;
            best = candidate;
        }

        return best;
    }

    /// <summary>Picks a living man in the target unit to aim at.</summary>
    private static int PickMark(BattleState state, Unit target)
    {
        if (target.Alive <= 0) return -1;

        // Walk forward from a random offset rather than rejection-sampling, so the cost
        // stays bounded even when a unit is nearly wiped out.
        int span = target.Strength;
        int start = state.RngMissile.NextInt(span);

        for (int i = 0; i < span; i++)
        {
            int s = target.FirstSoldier + (start + i) % span;
            if (state.State[s] != SoldierState.Dead) return s;
        }

        return -1;
    }

    private static void Loose(BattleState state, int shooter, Unit unit, Unit target, int mark)
    {
        UnitType type = unit.Type;
        FixVec2 from = state.Position[shooter];
        FixVec2 at = state.Position[mark];

        Fix distance = FixVec2.Distance(from, at);
        if (distance <= Fix.Zero) return;

        // Scatter grows with range: a shot at 140 metres is a shot at an area, not a man.
        Fix scatter = Fix.One + distance * SimConstants.MissileScatterPerMetre;
        FixVec2 aimPoint = at + new FixVec2(
            state.RngMissile.NextSpread() * scatter,
            state.RngMissile.NextSpread() * scatter);

        Fix flightSeconds = distance / SpeedOf(type.Missile);

        state.Missiles[state.MissileCount++] = new Missile
        {
            Origin = from,
            Target = aimPoint,
            ElapsedTicks = Fix.Zero,
            FlightTicks = FixMath.Max(flightSeconds * SimConstants.TickRate, Fix.One),
            ShooterUnit = unit.Id,
            TargetUnit = target.Id,
            Attack = type.MissileAttack,
            ArmourPiercing = type.ArmourPiercing,
            Type = type.Missile,
        };

        state.Ammo[shooter]--;

        int reload = SimConstants.Ticks(type.ReloadInterval);
        int jitter = reload / 3;
        state.ReloadCooldown[shooter] = reload + state.RngMissile.NextInt(-jitter, jitter + 1);

        state.Raise(new BattleEvent
        {
            Type = BattleEventType.MissileLoosed,
            Position = from,
            Direction = (aimPoint - from).Normalized,
            A = (int)type.Missile,
            B = unit.Id,
        });
    }

    // ------------------------------------------------------------------ flight

    private static void AdvanceMissiles(BattleState state)
    {
        int i = 0;
        while (i < state.MissileCount)
        {
            ref Missile missile = ref state.Missiles[i];
            missile.ElapsedTicks += Fix.One;

            if (missile.ElapsedTicks < missile.FlightTicks)
            {
                i++;
                continue;
            }

            Impact(state, in missile);

            // Swap-remove. Deterministic, and it keeps the live missiles contiguous.
            state.Missiles[i] = state.Missiles[--state.MissileCount];
        }
    }

    private static void Impact(BattleState state, in Missile missile)
    {
        state.Raise(new BattleEvent
        {
            Type = BattleEventType.MissileImpact,
            Position = missile.Target,
            A = (int)missile.Type,
            B = missile.ShooterUnit,
        });

        // Whoever is standing here takes it — friend or enemy. This is why you do not
        // shoot into a melee, and it costs nothing to implement.
        int victim = FindVictim(state, missile.Target);
        if (victim < 0) return;

        Unit shooter = state.Units[missile.ShooterUnit];
        Unit hitUnit = state.UnitOf(victim);

        if (!state.RngMissile.Chance(HitChance(state, in missile, victim, hitUnit))) return;

        state.Health[victim]--;
        if (state.Health[victim] > 0) return;

        state.KillSoldier(victim, shooter.Id);

        if (hitUnit.IsGeneral && hitUnit.Alive <= 1)
        {
            state.ArmyOf(hitUnit).GeneralDead = true;
            state.Raise(new BattleEvent
            {
                Type = BattleEventType.GeneralKilled,
                Position = state.Position[victim],
                A = hitUnit.Id,
                B = hitUnit.ArmyId,
            });
        }
    }

    /// <summary>Nearest living man to the impact point, from either army, or −1 for a clean miss.</summary>
    private static int FindVictim(BattleState state, FixVec2 impact)
    {
        int best = -1;
        long bestSqr = FixMath.SqrRaw(SimConstants.MissileBodyRadius);

        for (int army = 0; army < state.Armies.Length; army++)
        {
            int found = state.HashFor(army)
                .FindNearest(impact, SimConstants.MissileBodyRadius, state.Position);
            if (found < 0) continue;

            long sqr = FixVec2.SqrDistanceRaw(impact, state.Position[found]);
            if (sqr >= bestSqr) continue;

            bestSqr = sqr;
            best = found;
        }

        return best;
    }

    /// <summary>
    /// Missile lethality. Armour is the main defence, shields count only if the man is
    /// facing the shooter, and armour-piercing shot ignores part of the armour entirely —
    /// which is why Balearic slingers are the answer to a Roman line.
    /// </summary>
    public static Fix HitChance(BattleState state, in Missile missile, int victim, Unit hitUnit)
    {
        UnitType type = hitUnit.Type;

        Fix armour = Fix.FromInt(type.Armour) * (Fix.One - FixMath.Clamp01(missile.ArmourPiercing));
        Fix defence = armour;

        FixVec2 toShooter = (missile.Origin - state.Position[victim]).Normalized;
        if (FixVec2.Dot(state.Facing[victim], toShooter) > Fix.Zero)
            defence += Fix.FromInt(type.Shield);

        Fix chance = SimConstants.MissileBaseHitChance
            + (Fix.FromInt(missile.Attack) - defence) * SimConstants.MissileHitChancePerPoint;

        chance = FixMath.Clamp(chance,
            SimConstants.MissileMinHitChance, SimConstants.MissileMaxHitChance);

        // Locked shields or loose order, whichever this formation is doing.
        chance *= hitUnit.FormationProfile.MissileVulnerability;

        // Canopy stops a good deal of what is shot into a wood.
        Fix forest = state.Terrain.ForestAt(state.Position[victim]);
        chance *= Fix.One - forest * SimConstants.ForestMissileCover;

        return FixMath.Clamp01(chance);
    }
}
