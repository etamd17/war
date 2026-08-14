using War.Sim.Core;
using War.Sim.Sim;
using War.Sim.Units;
using War.Sim.World;

namespace War.Sim.Tests.Sim;

/// <summary>
/// Scenario builders for the behavioural tests.
///
/// Most of these are deliberately artificial — two units, flat ground, no AI — because
/// the point is to isolate one rule at a time. If a flanking test also involved terrain,
/// morale contagion, and an AI commander, a failure would tell you almost nothing.
///
/// Every scenario quietly parks a <em>distant reserve</em> in each army, well off in a
/// corner and never involved. Without it the army-break victory check fires the moment
/// the single unit under test drops to thirty percent, the simulation stops, and every
/// measurement gets truncated at the same arbitrary point — which is how three very
/// different flank attacks came to produce identical casualty figures. The reserve is
/// far enough away to contribute nothing to morale, and only exists so the fight being
/// measured runs to its natural conclusion.
/// </summary>
public static class BattleFixtures
{
    /// <summary>Featureless flat ground, so only the rule under test can affect the result.</summary>
    public static Terrain FlatField(int size = 512)
    {
        var terrain = new Terrain(65, Fix.FromInt(size));
        terrain.RebuildGradients();
        return terrain;
    }

    /// <summary>A single even slope rising to the east, for high-ground tests.</summary>
    public static Terrain Hillside(int size = 512, int rise = 1)
    {
        const int resolution = 65;
        var terrain = new Terrain(resolution, Fix.FromInt(size));

        for (int y = 0; y < resolution; y++)
            for (int x = 0; x < resolution; x++)
                terrain.SetHeight(x, y, Fix.FromInt(x * rise) * terrain.CellSize / 8);

        terrain.RebuildGradients();
        return terrain;
    }

    /// <summary>The far-corner unit that keeps the army-break check from ending a micro-fight.</summary>
    private static UnitBlueprint DistantReserve(Faction faction) =>
        new() { TypeId = Roster.GeneralOf(faction).Id, Strength = 24 };

    /// <summary>Parks the reserves in opposite corners, out of everyone's way.</summary>
    private static void StowReserves(BattleState state, params Unit[] reserves)
    {
        Fix edge = Fix.FromInt(20);
        Fix far = state.Terrain.Size - edge;

        for (int i = 0; i < reserves.Length; i++)
        {
            FixVec2 corner = i == 0 ? new FixVec2(edge, edge) : new FixVec2(far, far);
            state.Reposition(reserves[i], corner, FixVec2.North);
            reserves[i].Order = UnitOrder.Hold();
            reserves[i].FireAtWill = false;
        }
    }

    /// <summary>
    /// Two units, nothing else. Both armies are marked as player-controlled so the AI
    /// stays out of it and the test controls the orders.
    /// </summary>
    public static (BattleState state, Unit left, Unit right) Duel(
        string leftType, string rightType,
        Terrain? terrain = null,
        int leftStrength = 0, int rightStrength = 0,
        FormationType? leftFormation = null, FormationType? rightFormation = null,
        uint seed = 1)
    {
        UnitType left = Roster.Get(leftType);
        UnitType right = Roster.Get(rightType);

        var setup = new BattleSetup
        {
            Terrain = terrain ?? FlatField(),
            Seed = seed,
            Armies =
            [
                new ArmyBlueprint
                {
                    Faction = left.Faction,
                    Name = "Left",
                    IsPlayer = true,
                    Units =
                    [
                        new UnitBlueprint
                        {
                            TypeId = leftType,
                            Strength = leftStrength,
                            Formation = leftFormation,
                        },
                        DistantReserve(left.Faction),
                    ],
                },
                new ArmyBlueprint
                {
                    Faction = right.Faction,
                    Name = "Right",
                    IsPlayer = true,
                    Units =
                    [
                        new UnitBlueprint
                        {
                            TypeId = rightType,
                            Strength = rightStrength,
                            Formation = rightFormation,
                        },
                        DistantReserve(right.Faction),
                    ],
                },
            ],
        };

        BattleState state = BattleBuilder.Build(setup);
        StowReserves(state, state.Units[1], state.Units[3]);

        return (state, state.Units[0], state.Units[2]);
    }

    /// <summary>
    /// Sets a duel up as a specific tactical situation: the attacker placed at a given
    /// bearing from the defender, with the defender facing whichever way the test wants.
    /// </summary>
    public static void Engage(
        BattleState state, Unit attacker, Unit defender,
        FixVec2 defenderFacing, FixVec2 attackerBearing, Fix distance)
    {
        FixVec2 centre = new(state.Terrain.Size / 2, state.Terrain.Size / 2);

        state.Reposition(defender, centre, defenderFacing);
        state.Reposition(attacker, centre + attackerBearing.Normalized * distance, -attackerBearing.Normalized);

        attacker.Order = UnitOrder.Attack(defender.Id);
        defender.Order = UnitOrder.Hold();

        state.RebuildSpatialIndices();
    }

    /// <summary>
    /// The scenario that actually tests flanking: a defender pinned frontally by one
    /// unit while a second attacks from a chosen bearing.
    ///
    /// Testing a flank attack on an <em>unengaged</em> unit measures nothing, because an
    /// idle unit will simply turn to face the attacker — correctly. Flanking is worth
    /// something precisely when the target is already busy.
    /// </summary>
    public static (BattleState state, Unit pin, Unit hook, Unit defender) FlankScenario(
        string attackerType, string defenderType, FixVec2 bearing,
        uint seed = 1, int hookDistance = 22)
    {
        UnitType attacker = Roster.Get(attackerType);
        UnitType defender = Roster.Get(defenderType);

        var setup = new BattleSetup
        {
            Terrain = FlatField(),
            Seed = seed,
            Armies =
            [
                new ArmyBlueprint
                {
                    Faction = attacker.Faction,
                    Name = "Attackers",
                    IsPlayer = true,
                    Units =
                    [
                        new UnitBlueprint { TypeId = attackerType },
                        new UnitBlueprint { TypeId = attackerType },
                    ],
                },
                new ArmyBlueprint
                {
                    Faction = defender.Faction,
                    Name = "Defender",
                    IsPlayer = true,
                    Units =
                    [
                        new UnitBlueprint { TypeId = defenderType },
                        DistantReserve(defender.Faction),
                    ],
                },
            ],
        };

        BattleState state = BattleBuilder.Build(setup);

        Unit pin = state.Units[0];
        Unit hook = state.Units[1];
        Unit target = state.Units[2];

        StowReserves(state, state.Units[3]);

        FixVec2 centre = new(state.Terrain.Size / 2, state.Terrain.Size / 2);

        // The defender faces north throughout; the pin always comes from the north, so
        // only the hook's bearing varies between runs.
        state.Reposition(target, centre, FixVec2.North);
        state.Reposition(pin, centre + FixVec2.North * Fix.FromInt(14), -FixVec2.North);
        state.Reposition(hook, centre + bearing.Normalized * Fix.FromInt(hookDistance), -bearing.Normalized);

        pin.Order = UnitOrder.Attack(target.Id);
        hook.Order = UnitOrder.Attack(target.Id);
        target.Order = UnitOrder.Hold();

        state.RebuildSpatialIndices();
        return (state, pin, hook, target);
    }

    /// <summary>A full Rome versus Carthage field battle, eight units a side.</summary>
    public static BattleSetup RomeVersusCarthage(
        uint seed = 1, bool playerCommandsRome = false, Terrain? terrain = null)
    {
        return new BattleSetup
        {
            Terrain = terrain ?? TerrainGenerator.Generate(new BattlefieldSettings { Seed = seed }),
            Seed = seed,
            Separation = Fix.FromInt(320),
            Armies =
            [
                new ArmyBlueprint
                {
                    Faction = Faction.Rome,
                    Name = "Rome",
                    IsPlayer = playerCommandsRome,
                    Units =
                    [
                        new UnitBlueprint { TypeId = "rome_velites" },
                        new UnitBlueprint { TypeId = "rome_hastati" },
                        new UnitBlueprint { TypeId = "rome_hastati" },
                        new UnitBlueprint { TypeId = "rome_principes" },
                        new UnitBlueprint { TypeId = "rome_principes" },
                        new UnitBlueprint { TypeId = "rome_triarii" },
                        new UnitBlueprint { TypeId = "rome_equites" },
                        new UnitBlueprint { TypeId = "rome_general" },
                    ],
                },
                new ArmyBlueprint
                {
                    Faction = Faction.Carthage,
                    Name = "Carthage",
                    IsPlayer = false,
                    Units =
                    [
                        new UnitBlueprint { TypeId = "carthage_balearic_slingers" },
                        new UnitBlueprint { TypeId = "carthage_libyan_spearmen" },
                        new UnitBlueprint { TypeId = "carthage_libyan_spearmen" },
                        new UnitBlueprint { TypeId = "carthage_iberian" },
                        new UnitBlueprint { TypeId = "carthage_poeni" },
                        new UnitBlueprint { TypeId = "carthage_numidian_cavalry" },
                        new UnitBlueprint { TypeId = "carthage_sacred_band_cavalry" },
                        new UnitBlueprint { TypeId = "carthage_general" },
                    ],
                },
            ],
        };
    }

    /// <summary>
    /// Runs a battle until one side breaks, a unit routs, or the clock runs out.
    ///
    /// The <c>!sim.IsOver</c> guard is load-bearing: <see cref="BattleSim.Tick"/> is a
    /// no-op once the battle has been decided, so a loop that only watches the clock
    /// spins forever.
    /// </summary>
    public static int RunUntil(BattleSim sim, Func<bool> stop, int maxSeconds = 120)
    {
        BattleState state = sim.State;
        int limit = SimConstants.TickRate * maxSeconds;

        while (state.Tick < limit && !sim.IsOver)
        {
            sim.Tick();
            if (stop()) return state.Tick;
        }

        return -1;
    }

    /// <summary>A cheap fingerprint of the entire simulation state, for determinism tests.</summary>
    public static ulong Fingerprint(BattleState state)
    {
        ulong hash = 1469598103934665603UL;

        void Mix(long value)
        {
            hash ^= (ulong)value;
            hash *= 1099511628211UL;
        }

        Mix(state.Tick);

        for (int s = 0; s < state.SoldierCount; s++)
        {
            Mix(state.Position[s].X.Raw);
            Mix(state.Position[s].Y.Raw);
            Mix(state.Facing[s].X.Raw);
            Mix(state.Facing[s].Y.Raw);
            Mix(state.Health[s]);
            Mix((int)state.State[s]);
            Mix(state.Fatigue[s].Raw);
            Mix(state.MeleeTarget[s]);
            Mix(state.Ammo[s]);
        }

        foreach (Unit unit in state.Units)
        {
            Mix(unit.Alive);
            Mix(unit.Morale.Raw);
            Mix((int)unit.MoraleState);
            Mix(unit.Anchor.X.Raw);
            Mix(unit.Anchor.Y.Raw);
        }

        return hash;
    }

    /// <summary>Living men in an army, counting only units that have not broken.</summary>
    public static int EffectiveStrength(BattleState state, int armyId)
    {
        int total = 0;
        foreach (int unitId in state.Armies[armyId].UnitIds)
        {
            Unit unit = state.Units[unitId];
            if (unit.IsEffective) total += unit.Alive;
        }
        return total;
    }
}
