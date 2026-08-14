using War.Sim.Core;
using War.Sim.Units;
using War.Sim.World;

namespace War.Sim.Sim;

public enum SoldierState : byte
{
    /// <summary>Standing in, or moving to, his place in the formation.</summary>
    Formed = 0,
    /// <summary>Moving to a position that is not his formation slot.</summary>
    Moving = 1,
    /// <summary>Closing at speed with the charge bonus live.</summary>
    Charging = 2,
    /// <summary>In contact and trading blows.</summary>
    Fighting = 3,
    /// <summary>Running away and not coming back unless rallied.</summary>
    Routing = 4,
    Dead = 5,
}

public enum BattleEventType : byte
{
    SoldierKilled = 0,
    MissileLoosed = 1,
    MissileImpact = 2,
    ChargeImpact = 3,
    UnitBroke = 4,
    UnitRallied = 5,
    UnitDestroyed = 6,
    GeneralKilled = 7,
    BattleEnded = 8,
}

/// <summary>
/// Something the presentation layer may want to react to — a sound, a splash of blood,
/// a banner going down. The simulation never reads these back; they are write-only
/// output, drained by the renderer each frame.
/// </summary>
public readonly struct BattleEvent
{
    public required BattleEventType Type { get; init; }
    public FixVec2 Position { get; init; }
    public FixVec2 Direction { get; init; }
    /// <summary>Meaning depends on the event: soldier id, unit id, or army id.</summary>
    public int A { get; init; }
    public int B { get; init; }
}

/// <summary>A missile in flight. Damage is resolved on arrival, not on release.</summary>
public struct Missile
{
    public FixVec2 Origin;
    public FixVec2 Target;
    public Fix ElapsedTicks;
    public Fix FlightTicks;
    public int ShooterUnit;
    public int TargetUnit;
    public int Attack;
    public Fix ArmourPiercing;
    public MissileType Type;
}

public enum BattleResult : byte
{
    InProgress = 0,
    ArmyVictory = 1,
    Draw = 2,
}

/// <summary>An army: a side in the battle, its units, and its commander.</summary>
public sealed class Army
{
    public required int Id { get; init; }
    public required Faction Faction { get; init; }
    public required string Name { get; init; }

    /// <summary>True for the side the human is commanding. The AI skips it.</summary>
    public bool IsPlayer { get; init; }

    public required int[] UnitIds { get; init; }

    /// <summary>Unit id of the general's bodyguard, or −1 if this army has no general.</summary>
    public int GeneralUnit { get; set; } = -1;

    /// <summary>Where this army deployed. Routers run back toward it.</summary>
    public FixVec2 DeploymentCentre { get; set; }

    /// <summary>The direction this army faces at the start — its axis of advance.</summary>
    public FixVec2 AdvanceDirection { get; set; } = FixVec2.North;

    public int InitialMen { get; set; }

    /// <summary>True once the general's bodyguard has been destroyed.</summary>
    public bool GeneralDead { get; set; }
}

/// <summary>
/// The whole battle, in flat arrays.
///
/// Soldiers are stored struct-of-arrays: parallel arrays indexed by a global soldier id,
/// with each unit owning a contiguous range. At 2400 men the hot arrays total roughly
/// 150 KB, which fits in L2 — so a tick is a handful of linear sweeps over memory the
/// CPU has already prefetched, rather than a pointer chase through 2400 objects.
///
/// Nothing in here is allocated during a tick. The scratch buffers, spatial indices, and
/// missile pool are all sized at construction, because a GC pause in the middle of a
/// battle is both a stutter and, if it ever reordered anything, a determinism bug.
/// </summary>
public sealed class BattleState
{
    public Terrain Terrain { get; }

    /// <summary>Ticks elapsed. At 30 Hz this is the battle clock.</summary>
    public int Tick { get; internal set; }

    public Fix ElapsedSeconds => Fix.Ratio(Tick, SimConstants.TickRate);

    public uint Seed { get; }

    // ------------------------------------------------------------- soldier data

    public int SoldierCount { get; }

    public readonly FixVec2[] Position;

    /// <summary>Position at the end of the previous tick, so the renderer can interpolate.</summary>
    public readonly FixVec2[] PreviousPosition;

    public readonly FixVec2[] Facing;
    public readonly int[] Health;
    public readonly SoldierState[] State;

    /// <summary>0 fresh to 1 spent.</summary>
    public readonly Fix[] Fatigue;

    public readonly int[] SoldierUnit;

    /// <summary>Which place in the formation this man is trying to stand in.</summary>
    public readonly int[] Slot;

    /// <summary>Ticks until this soldier may strike again.</summary>
    public readonly int[] AttackCooldown;

    /// <summary>Soldier id currently being fought, or −1.</summary>
    public readonly int[] MeleeTarget;

    /// <summary>Ticks of charge bonus remaining, counting down from impact.</summary>
    public readonly int[] ChargeTicks;

    public readonly int[] Ammo;
    public readonly int[] ReloadCooldown;

    // ---------------------------------------------------------------- structure

    public Unit[] Units { get; }
    public Army[] Armies { get; }

    // ------------------------------------------------------------------ spatial

    /// <summary>One index per army, so "find me an enemy" never sifts through friends.</summary>
    private readonly SpatialHash[] _armyHash;

    private readonly int[][] _hashIdScratch;
    private readonly FixVec2[][] _hashPositionScratch;

    /// <summary>Shared scratch for neighbour queries. Sized for the worst case so nothing allocates mid-tick.</summary>
    internal readonly int[] QueryScratch;

    /// <summary>
    /// Accumulated separation push per soldier. Collision is resolved Jacobi-style —
    /// every push is computed against the same starting positions, then all are applied
    /// at once. Resolving them one at a time would make the result depend on iteration
    /// order, which is exactly the kind of thing that quietly breaks determinism.
    /// </summary>
    internal readonly FixVec2[] PushScratch;

    /// <summary>Per-soldier movement goal for this tick.</summary>
    internal readonly FixVec2[] GoalScratch;

    // --------------------------------------------------------------------- rng

    public DetRandom RngMelee { get; }
    public DetRandom RngMissile { get; }
    public DetRandom RngMorale { get; }
    public DetRandom RngFatigue { get; }
    public DetRandom RngRout { get; }
    public DetRandom RngAi { get; }

    // -------------------------------------------------------------------- output

    private readonly List<BattleEvent> _events = new(512);

    /// <summary>Events raised since the last drain. Presentation only.</summary>
    public IReadOnlyList<BattleEvent> Events => _events;

    public Missile[] Missiles { get; }
    public int MissileCount { get; internal set; }

    public BattleResult Result { get; internal set; } = BattleResult.InProgress;

    /// <summary>Army id of the winner once <see cref="Result"/> is a victory, otherwise −1.</summary>
    public int Victor { get; internal set; } = -1;

    // ------------------------------------------------------------------ construct

    internal BattleState(Terrain terrain, Unit[] units, Army[] armies, int soldierCount, uint seed)
    {
        Terrain = terrain;
        Units = units;
        Armies = armies;
        SoldierCount = soldierCount;
        Seed = seed;

        Position = new FixVec2[soldierCount];
        PreviousPosition = new FixVec2[soldierCount];
        Facing = new FixVec2[soldierCount];
        Health = new int[soldierCount];
        State = new SoldierState[soldierCount];
        Fatigue = new Fix[soldierCount];
        SoldierUnit = new int[soldierCount];
        Slot = new int[soldierCount];
        AttackCooldown = new int[soldierCount];
        MeleeTarget = new int[soldierCount];
        ChargeTicks = new int[soldierCount];
        Ammo = new int[soldierCount];
        ReloadCooldown = new int[soldierCount];

        QueryScratch = new int[soldierCount];
        PushScratch = new FixVec2[soldierCount];
        GoalScratch = new FixVec2[soldierCount];

        _armyHash = new SpatialHash[armies.Length];
        _hashIdScratch = new int[armies.Length][];
        _hashPositionScratch = new FixVec2[armies.Length][];

        for (int a = 0; a < armies.Length; a++)
        {
            int men = 0;
            foreach (int unitId in armies[a].UnitIds) men += units[unitId].Strength;

            _armyHash[a] = new SpatialHash(terrain.Size, SimConstants.SpatialCellSize, men + 1);
            _hashIdScratch[a] = new int[men];
            _hashPositionScratch[a] = new FixVec2[men];
        }

        // Missile capacity: a generous ceiling on shots in the air at once. Volleys are
        // large but flight times are short, so this is never close to being hit.
        Missiles = new Missile[4096];

        RngMelee = new DetRandom(seed, RngStream.Melee);
        RngMissile = new DetRandom(seed, RngStream.Missile);
        RngMorale = new DetRandom(seed, RngStream.Morale);
        RngFatigue = new DetRandom(seed, RngStream.Fatigue);
        RngRout = new DetRandom(seed, RngStream.Rout);
        RngAi = new DetRandom(seed, RngStream.Ai);
    }

    // ------------------------------------------------------------------ helpers

    public bool IsAlive(int soldier) => State[soldier] != SoldierState.Dead;

    public Unit UnitOf(int soldier) => Units[SoldierUnit[soldier]];

    public Army ArmyOf(Unit unit) => Armies[unit.ArmyId];

    public SpatialHash HashFor(int armyId) => _armyHash[armyId];

    public FixVec2[] PositionsFor(int armyId) => Position;

    public void Raise(BattleEvent e) => _events.Add(e);

    /// <summary>Called by the presentation layer once it has consumed the frame's events.</summary>
    public void DrainEvents() => _events.Clear();

    /// <summary>
    /// Rebuilds every army's spatial index from the living. Corpses are simply left out
    /// rather than filtered at query time, so queries do not get slower as the field
    /// fills with bodies.
    /// </summary>
    public void RebuildSpatialIndices()
    {
        for (int a = 0; a < Armies.Length; a++)
        {
            int[] ids = _hashIdScratch[a];
            FixVec2[] positions = _hashPositionScratch[a];
            int n = 0;

            foreach (int unitId in Armies[a].UnitIds)
            {
                Unit unit = Units[unitId];
                if (unit.Withdrawn) continue;

                for (int s = unit.FirstSoldier; s < unit.EndSoldier; s++)
                {
                    if (State[s] == SoldierState.Dead) continue;
                    ids[n] = s;
                    positions[n] = Position[s];
                    n++;
                }
            }

            _armyHash[a].Build(ids.AsSpan(0, n), positions.AsSpan(0, n));
        }
    }

    /// <summary>
    /// Recomputes each unit's centre, living count, mean fatigue, and how well the men
    /// are actually holding their places.
    /// </summary>
    public void RefreshUnitAggregates()
    {
        foreach (Unit unit in Units)
        {
            if (unit.Withdrawn) continue;

            int alive = 0;
            // Positions must accumulate in 64 bits — see FixVec2Sum. Summing them into
            // a Fix overflows at around 128 men and silently teleports the unit.
            var sum = new FixVec2Sum();
            long fatigueRaw = 0;

            for (int s = unit.FirstSoldier; s < unit.EndSoldier; s++)
            {
                if (State[s] == SoldierState.Dead) continue;
                alive++;
                sum.Add(Position[s]);
                fatigueRaw += Fatigue[s].Raw;
            }

            unit.Alive = alive;

            if (alive == 0)
            {
                unit.Fatigue = Fix.Zero;
                unit.Cohesion = Fix.Zero;
                continue;
            }

            unit.Centre = sum.Mean;
            unit.Fatigue = Fix.FromRaw((int)(fatigueRaw / alive));
        }
    }

    /// <summary>
    /// Assigns formation slots to the living, front rank first, in soldier-id order.
    ///
    /// Slots are only rebuilt when the unit's shape actually changes — a new order, a
    /// new formation, or enough casualties to leave visible holes. Reassigning every
    /// tick would have the whole unit shuffling sideways continuously as men fall.
    /// </summary>
    public void RebuildSlots(Unit unit)
    {
        int slot = 0;
        for (int s = unit.FirstSoldier; s < unit.EndSoldier; s++)
        {
            if (State[s] == SoldierState.Dead)
            {
                Slot[s] = -1;
                continue;
            }
            Slot[s] = slot++;
        }
        unit.SlotsBuiltFor = unit.Alive;
    }

    /// <summary>
    /// Whether a unit has lost enough men since its slots were built that the formation
    /// has visible gaps and should close up.
    /// </summary>
    public bool NeedsReform(Unit unit)
    {
        if (unit.SlotsBuiltFor < 0) return true;
        if (unit.Alive <= 0) return false;
        int lost = unit.SlotsBuiltFor - unit.Alive;
        if (lost <= 0) return false;
        // Close up once a tenth of the men standing at last re-form are gone.
        return lost * 10 >= unit.SlotsBuiltFor;
    }

    /// <summary>World position of a soldier's place in his unit's formation.</summary>
    public FixVec2 SlotPosition(Unit unit, int slot)
    {
        FixVec2 local = Formations.SlotOffset(
            unit.Formation, slot, unit.EffectiveWidth,
            unit.Alive > 0 ? unit.Alive : unit.Strength,
            unit.Type.FileSpacing, unit.Type.RankSpacing);

        return unit.Anchor + local.Rotate(unit.AnchorFacing);
    }

    /// <summary>
    /// Picks a unit up and sets it down somewhere else, fully formed. Used by the
    /// deployment phase before a battle starts, by scenario setup, and by tests that
    /// need two units placed in an exact relationship to each other.
    /// </summary>
    public void Reposition(Unit unit, FixVec2 anchor, FixVec2 facing)
    {
        unit.Anchor = Terrain.ClampToBounds(anchor);
        unit.AnchorFacing = facing;
        unit.Facing = facing;
        unit.Centre = unit.Anchor;

        RebuildSlots(unit);

        for (int s = unit.FirstSoldier; s < unit.EndSoldier; s++)
        {
            if (State[s] == SoldierState.Dead) continue;

            Position[s] = Terrain.ClampToBounds(SlotPosition(unit, Slot[s]));
            PreviousPosition[s] = Position[s];
            Facing[s] = facing;
            MeleeTarget[s] = -1;
            ChargeTicks[s] = 0;
        }

        RefreshUnitAggregates();
    }

    /// <summary>Kills a soldier, raising the event and crediting the killer's unit.</summary>
    public void KillSoldier(int soldier, int killerUnit)
    {
        if (State[soldier] == SoldierState.Dead) return;

        State[soldier] = SoldierState.Dead;
        Health[soldier] = 0;
        MeleeTarget[soldier] = -1;

        Unit victim = UnitOf(soldier);
        victim.RecentLosses++;

        if (killerUnit >= 0) Units[killerUnit].RecentKills++;

        Raise(new BattleEvent
        {
            Type = BattleEventType.SoldierKilled,
            Position = Position[soldier],
            Direction = Facing[soldier],
            A = soldier,
            B = victim.Id,
        });
    }
}
