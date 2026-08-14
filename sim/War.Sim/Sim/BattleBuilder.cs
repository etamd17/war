using War.Sim.Core;
using War.Sim.Units;
using War.Sim.World;

namespace War.Sim.Sim;

/// <summary>One unit in an army list.</summary>
public sealed class UnitBlueprint
{
    public required string TypeId { get; init; }

    /// <summary>Men in the unit. Zero uses the type's default strength.</summary>
    public int Strength { get; init; }

    /// <summary>Formation to deploy in. Null uses the type's default.</summary>
    public FormationType? Formation { get; init; }

    /// <summary>Frontage in files. Zero uses the formation's natural width.</summary>
    public int Width { get; init; }
}

public sealed class ArmyBlueprint
{
    public required Faction Faction { get; init; }
    public required string Name { get; init; }
    public bool IsPlayer { get; init; }
    public required IReadOnlyList<UnitBlueprint> Units { get; init; }
}

public sealed class BattleSetup
{
    public required Terrain Terrain { get; init; }
    public required ArmyBlueprint[] Armies { get; init; }
    public uint Seed { get; init; } = 1;

    /// <summary>Metres between the two deployment lines at the start.</summary>
    public Fix Separation { get; init; } = Fix.FromInt(320);

    /// <summary>
    /// Start the battle paused with the armies drawn up, so a commander can rearrange
    /// his line before anything moves. The game wants this; auto-resolve, balance sweeps
    /// and tests do not.
    /// </summary>
    public bool DeploymentPhase { get; init; }

    /// <summary>How deep the ground behind each starting line is, for deployment.</summary>
    public Fix DeploymentDepth { get; init; } = Fix.FromInt(90);
}

/// <summary>
/// Turns army lists into a deployed battle.
///
/// Deployment follows the obvious ancient practice, because it is obvious for good
/// reasons: skirmishers screen out in front, heavy foot forms the line, horse takes the
/// wings where it has room to manoeuvre, and the general sits behind the centre where
/// his morale aura covers the most men and he is hardest to reach.
/// </summary>
public static class BattleBuilder
{
    public static BattleState Build(BattleSetup setup)
    {
        if (setup.Armies.Length != 2)
            throw new ArgumentException("A battle needs exactly two armies", nameof(setup));

        var units = new List<Unit>();
        var armies = new Army[setup.Armies.Length];
        int nextSoldier = 0;

        for (int a = 0; a < setup.Armies.Length; a++)
        {
            ArmyBlueprint blueprint = setup.Armies[a];
            var unitIds = new List<int>();

            foreach (UnitBlueprint spec in blueprint.Units)
            {
                UnitType type = Roster.Get(spec.TypeId);

                if (type.Faction != blueprint.Faction)
                    throw new ArgumentException(
                        $"{type.Name} is a {type.Faction} unit and cannot serve in a {blueprint.Faction} army");

                int strength = spec.Strength > 0 ? spec.Strength : type.DefaultStrength;
                FormationType formation = spec.Formation ?? type.DefaultFormation;

                if (!type.CanUse(formation))
                    throw new ArgumentException($"{type.Name} cannot form {formation}");

                var unit = new Unit
                {
                    Id = units.Count,
                    Type = type,
                    ArmyId = a,
                    Faction = blueprint.Faction,
                    FirstSoldier = nextSoldier,
                    Strength = strength,
                    Alive = strength,
                    Formation = formation,
                    Width = spec.Width,
                    Morale = Fix.FromInt(100),
                    SkirmishStance = type.Class is UnitClass.Missile or UnitClass.MissileCavalry,
                };

                unitIds.Add(unit.Id);
                units.Add(unit);
                nextSoldier += strength;
            }

            armies[a] = new Army
            {
                Id = a,
                Faction = blueprint.Faction,
                Name = blueprint.Name,
                IsPlayer = blueprint.IsPlayer,
                UnitIds = unitIds.ToArray(),
            };
        }

        var state = new BattleState(setup.Terrain, units.ToArray(), armies, nextSoldier, setup.Seed);

        Deploy(state, setup);

        if (setup.DeploymentPhase) state.Phase = BattlePhase.Deploying;
        state.RefreshUnitAggregates();
        state.RebuildSpatialIndices();

        return state;
    }

    // ------------------------------------------------------------------ deploy

    private static void Deploy(BattleState state, BattleSetup setup)
    {
        Fix mapCentre = setup.Terrain.Size / 2;
        Fix half = setup.Separation / 2;

        for (int a = 0; a < state.Armies.Length; a++)
        {
            Army army = state.Armies[a];

            // Army 0 deploys south facing north; army 1 north facing south.
            bool south = a == 0;
            FixVec2 facing = south ? FixVec2.North : -FixVec2.North;
            Fix line = south ? mapCentre - half : mapCentre + half;

            army.AdvanceDirection = facing;
            army.DeploymentCentre = new FixVec2(mapCentre, line);

            // The ground behind the starting line, spanning the width of the field, plus
            // a little in front of it — enough that the boundary is visibly ahead of the
            // troops rather than drawn through them, and enough to let a commander push
            // his line forward a few paces if he wants the ridge.
            Fix allowance = Fix.FromInt(28);
            Fix behind = south ? line - setup.DeploymentDepth : line - allowance;
            Fix ahead = south ? line + allowance : line + setup.DeploymentDepth;

            army.Zone = new DeploymentZone
            {
                Min = new FixVec2(Fix.Zero, FixMath.Max(behind, Fix.Zero)),
                Max = new FixVec2(setup.Terrain.Size, FixMath.Min(ahead, setup.Terrain.Size)),
            };

            DeployArmy(state, army, new FixVec2(mapCentre, line), facing);

            foreach (int unitId in army.UnitIds)
            {
                Unit unit = state.Units[unitId];
                army.InitialMen += unit.Strength;
                if (unit.IsGeneral) army.GeneralUnit = unitId;
            }
        }
    }

    private static void DeployArmy(BattleState state, Army army, FixVec2 centre, FixVec2 facing)
    {
        var screen = new List<Unit>();
        var line = new List<Unit>();
        var horse = new List<Unit>();
        Unit? general = null;

        foreach (int unitId in army.UnitIds)
        {
            Unit unit = state.Units[unitId];
            switch (unit.Type.Class)
            {
                case UnitClass.Missile:
                    screen.Add(unit);
                    break;
                case UnitClass.Cavalry:
                case UnitClass.MissileCavalry:
                case UnitClass.Chariot:
                case UnitClass.Elephant:
                    horse.Add(unit);
                    break;
                case UnitClass.General:
                    general = unit;
                    break;
                default:
                    line.Add(unit);
                    break;
            }
        }

        FixVec2 forward = facing;
        FixVec2 left = facing.Left;

        // The main line sets the frontage everything else is arranged around.
        Fix lineFrontage = PlaceRow(state, line, centre, forward, left, Fix.Zero);

        // Skirmishers 30 m out in front, ready to pelt the enemy and then retire.
        PlaceRow(state, screen, centre, forward, left, Fix.FromInt(30));

        // Cavalry on the wings, clear of the line so it has room to swing.
        if (horse.Count > 0)
        {
            Fix wing = lineFrontage / 2 + Fix.FromInt(25);
            int half = (horse.Count + 1) / 2;

            var leftWing = horse.Take(half).ToList();
            var rightWing = horse.Skip(half).ToList();

            if (leftWing.Count > 0)
                PlaceRow(state, leftWing, centre + left * wing, forward, left, Fix.Zero);
            if (rightWing.Count > 0)
                PlaceRow(state, rightWing, centre - left * wing, forward, left, Fix.Zero);
        }

        // The general sits behind the centre: his aura covers the most men there, and
        // he is the hardest thing on the field to get at.
        if (general != null)
            PlaceUnit(state, general, centre - forward * Fix.FromInt(40), forward);
    }

    /// <summary>
    /// Lays units out side by side, centred on <paramref name="centre"/>, and returns the
    /// total frontage consumed.
    /// </summary>
    private static Fix PlaceRow(
        BattleState state, List<Unit> row, FixVec2 centre, FixVec2 forward, FixVec2 left, Fix advance)
    {
        if (row.Count == 0) return Fix.Zero;

        Fix gap = Fix.FromInt(12);
        Fix total = Fix.Zero;
        foreach (Unit unit in row) total += unit.HalfFrontage * 2;
        total += gap * (row.Count - 1);

        // Walk from the left edge of the row inward.
        Fix cursor = total / 2;

        foreach (Unit unit in row)
        {
            Fix halfWidth = unit.HalfFrontage;
            cursor -= halfWidth;

            FixVec2 at = centre + left * cursor + forward * advance;
            PlaceUnit(state, unit, at, forward);

            cursor -= halfWidth + gap;
        }

        return total;
    }

    /// <summary>Anchors a unit and drops every one of its men into their formation slot.</summary>
    private static void PlaceUnit(BattleState state, Unit unit, FixVec2 anchor, FixVec2 facing)
    {
        anchor = state.Terrain.ClampToBounds(anchor);

        unit.Anchor = anchor;
        unit.AnchorFacing = facing;
        unit.Facing = facing;
        unit.Centre = anchor;
        unit.Order = UnitOrder.Hold();

        int slot = 0;
        for (int s = unit.FirstSoldier; s < unit.EndSoldier; s++)
        {
            state.SoldierUnit[s] = unit.Id;
            state.Slot[s] = slot;
            state.Health[s] = unit.Type.Hitpoints;
            state.State[s] = SoldierState.Formed;
            state.Facing[s] = facing;
            state.Fatigue[s] = Fix.Zero;
            state.MeleeTarget[s] = -1;
            state.ChargeTicks[s] = 0;
            state.Ammo[s] = unit.Type.Ammunition;
            state.ReloadCooldown[s] = 0;

            // Stagger the first swing so casualties arrive as a stream rather than in
            // synchronised pulses every 1.2 seconds.
            state.AttackCooldown[s] = state.RngMelee.NextInt(0, SimConstants.Ticks(unit.Type.AttackInterval));

            FixVec2 position = state.SlotPosition(unit, slot);
            state.Position[s] = state.Terrain.ClampToBounds(position);
            state.PreviousPosition[s] = state.Position[s];

            slot++;
        }

        unit.SlotsBuiltFor = unit.Strength;
    }
}
