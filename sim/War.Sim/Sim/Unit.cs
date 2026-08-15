using War.Sim.Core;
using War.Sim.Units;

namespace War.Sim.Sim;

public enum OrderType : byte
{
    /// <summary>Stand in place. Will still fight anything that reaches them.</summary>
    Hold = 0,
    /// <summary>Move to a position and form up on a given facing.</summary>
    MoveTo = 1,
    /// <summary>Close with a specific enemy unit and fight it.</summary>
    Attack = 2,
    /// <summary>Fall back toward friendly lines, fighting only if caught.</summary>
    Withdraw = 3,
}

/// <summary>Where a unit's morale currently sits. Hysteresis lives in the transitions, not here.</summary>
public enum MoraleState : byte
{
    /// <summary>Fighting properly.</summary>
    Steady = 0,
    /// <summary>Losing heart: slower, worse in a fight, one push from breaking.</summary>
    Wavering = 1,
    /// <summary>Running. Will not fight, will not obey, may be ridden down.</summary>
    Routing = 2,
    /// <summary>Stopped running and re-forming. Fragile, but back in the battle.</summary>
    Rallying = 3,
}

public readonly struct UnitOrder
{
    public required OrderType Type { get; init; }

    /// <summary>Destination for <see cref="OrderType.MoveTo"/> and <see cref="OrderType.Withdraw"/>.</summary>
    public FixVec2 Position { get; init; }

    /// <summary>Facing to adopt on arrival. Zero means "keep whatever facing you have".</summary>
    public FixVec2 Facing { get; init; }

    /// <summary>Target for <see cref="OrderType.Attack"/>, or −1.</summary>
    public int TargetUnit { get; init; }

    /// <summary>Run rather than walk. Fast, and it costs a great deal of stamina.</summary>
    public bool Run { get; init; }

    public static UnitOrder Hold() => new() { Type = OrderType.Hold, TargetUnit = -1 };

    public static UnitOrder MoveTo(FixVec2 position, FixVec2 facing, bool run = false) => new()
    {
        Type = OrderType.MoveTo,
        Position = position,
        Facing = facing,
        TargetUnit = -1,
        Run = run,
    };

    public static UnitOrder Attack(int targetUnit, bool run = true) => new()
    {
        Type = OrderType.Attack,
        TargetUnit = targetUnit,
        Run = run,
    };

    public static UnitOrder Withdraw(FixVec2 position) => new()
    {
        Type = OrderType.Withdraw,
        Position = position,
        TargetUnit = -1,
        Run = true,
    };
}

/// <summary>
/// A unit: a body of men that moves, fights, and breaks together.
///
/// Soldiers live in flat arrays on <see cref="BattleState"/>; a unit owns a contiguous
/// range of them. Everything here is the state that belongs to the body rather than to
/// the individual — morale, order, formation, cohesion.
/// </summary>
public sealed class Unit
{
    public required int Id { get; init; }
    public required UnitType Type { get; init; }
    public required int ArmyId { get; init; }
    public required Faction Faction { get; init; }

    /// <summary>Index of this unit's first soldier in the battle-wide arrays.</summary>
    public required int FirstSoldier { get; init; }

    /// <summary>Total soldiers ever in this unit, alive or not.</summary>
    public required int Strength { get; init; }

    /// <summary>One past this unit's last soldier.</summary>
    public int EndSoldier => FirstSoldier + Strength;

    // -------------------------------------------------------------------- state

    public int Alive { get; set; }

    public UnitOrder Order { get; set; } = UnitOrder.Hold();

    public FormationType Formation { get; set; } = FormationType.Line;

    /// <summary>Frontage in files. Zero means "use the formation's natural width".</summary>
    public int Width { get; set; }

    /// <summary>Mean position of the living. Recomputed every tick.</summary>
    public FixVec2 Centre { get; set; }

    /// <summary>The direction the formation is drawn up to face.</summary>
    public FixVec2 Facing { get; set; } = FixVec2.North;

    /// <summary>
    /// Where the formation is anchored — specifically, the middle of its <em>front rank</em>,
    /// not its centre of mass. Slot offsets run backward from here, so placing a unit
    /// places the line you can see rather than an invisible point inside it, and a unit
    /// ordered to a spot arrives with its front rank on that spot.
    ///
    /// Men seek slots relative to this rather than to <see cref="Centre"/>. If the
    /// formation chased its own centre of mass, casualties on one flank would drag the
    /// whole line sideways.
    /// </summary>
    public FixVec2 Anchor { get; set; }

    public FixVec2 AnchorFacing { get; set; } = FixVec2.North;

    public Fix Morale { get; set; } = Fix.FromInt(100);

    public MoraleState MoraleState { get; set; } = MoraleState.Steady;

    /// <summary>Mean fatigue of the living, 0 fresh to 1 spent.</summary>
    public Fix Fatigue { get; set; }

    /// <summary>How well the men are actually holding their slots, 0 to 1.</summary>
    public Fix Cohesion { get; set; } = Fix.One;

    /// <summary>Ticks spent below the break threshold. Stops a single bad moment from routing a unit.</summary>
    public int BreakTicks { get; set; }

    /// <summary>Ticks since this unit last took a casualty or was in contact. Gates rallying.</summary>
    public int UnmolestedTicks { get; set; }

    /// <summary>Loose order: shoot and fall back rather than stand and be charged.</summary>
    public bool SkirmishStance { get; set; }

    /// <summary>Shoot at anything in range without being told to.</summary>
    public bool FireAtWill { get; set; } = true;

    /// <summary>
    /// Unit id this unit should shoot at in preference to the nearest target, or −1.
    /// Set by the commander (player or AI) so archery can be concentrated where it
    /// matters instead of dribbling into whatever happens to be closest.
    /// </summary>
    public int PreferredMissileTarget { get; set; } = -1;

    /// <summary>Living soldiers currently in contact with an enemy.</summary>
    public int Engaged { get; set; }

    /// <summary>Casualties taken in the last few seconds, for the "we are losing" morale term.</summary>
    public int RecentLosses { get; set; }

    /// <summary>Kills scored in the last few seconds, for the "we are winning" morale term.</summary>
    public int RecentKills { get; set; }

    /// <summary>The slot layout the men are currently seeking was built for this many living.</summary>
    public int SlotsBuiltFor { get; set; } = -1;

    /// <summary>
    /// Tick before which the commander will not change this unit's formation again.
    ///
    /// Re-forming is not free: the men walk to new slots, cohesion drops, and a unit
    /// halfway between a line and a square is worse than either. Without a lock, a unit
    /// sitting on a decision threshold flaps between two formations and spends the battle
    /// shuffling instead of fighting.
    /// </summary>
    public int FormationHoldUntil { get; set; }

    /// <summary>Set once the unit has left the field entirely and stops being simulated.</summary>
    public bool Withdrawn { get; set; }

    // ------------------------------------------------------------------ derived

    public bool IsDestroyed => Alive <= 0;

    public bool IsRouting => MoraleState == MoraleState.Routing;

    /// <summary>Out of the battle for good: dead, or run off the map.</summary>
    public bool IsOutOfAction => IsDestroyed || Withdrawn;

    /// <summary>Still able to influence the battle.</summary>
    public bool IsEffective => !IsOutOfAction && !IsRouting;

    public bool IsGeneral => Type.Class == UnitClass.General;

    /// <summary>Fraction of the unit still standing.</summary>
    public Fix StrengthFraction => Strength <= 0 ? Fix.Zero : Fix.Ratio(Alive, Strength);

    /// <summary>Frontage actually in use, resolving zero to the formation's natural width.</summary>
    public int EffectiveWidth =>
        Width > 0 ? Width : Formations.DefaultWidth(Formation, Alive > 0 ? Alive : Strength);

    public FormationProfile FormationProfile => Formations.Profile(Formation);

    /// <summary>Half the ground this unit covers, in metres. The AI uses it to match frontages.</summary>
    public Fix HalfFrontage => Formations.HalfFrontage(
        Formation, EffectiveWidth, Alive > 0 ? Alive : Strength, Type.FileSpacing);

    /// <summary>Ranks deep, given the current formation, width and losses.</summary>
    public int Ranks
    {
        get
        {
            int strength = Alive > 0 ? Alive : Strength;
            int width = EffectiveWidth;
            if (width < 1) width = 1;

            return Formation switch
            {
                FormationType.Square => width,
                FormationType.Wedge => width,
                _ => (strength + width - 1) / width,
            };
        }
    }

    /// <summary>
    /// Half the unit's depth in metres. Used to work out how close a unit must get
    /// before its front rank is actually in contact with an enemy's front rank.
    /// </summary>
    public Fix HalfDepth =>
        Fix.Ratio(Ranks - 1, 2) * Type.RankSpacing * FormationProfile.RankSpacingScale;

    /// <summary>True while any of this unit's men are in contact.</summary>
    public bool InContact => Engaged > 0;

    /// <summary>
    /// How far the unit's footprint reaches from its centre in a given direction,
    /// treating the formation as an oriented box.
    ///
    /// This is how an attacker works out where the enemy's edge actually is. Aiming at
    /// the target's centre offset by a fixed amount does not work: approach a wide line
    /// head-on and you stop far too short, approach it from the flank and you aim at a
    /// point on the far side and march your unit straight through theirs — which puts
    /// both formations inside each other, hands everyone rear attacks, and produces
    /// results that have nothing to do with what either commander intended.
    /// </summary>
    public Fix ExtentAlong(FixVec2 direction) =>
        FixMath.Abs(FixVec2.Dot(direction, Facing)) * HalfDepth +
        FixMath.Abs(FixVec2.Dot(direction, Facing.Right)) * HalfFrontage;

    public override string ToString() => $"{Type.Name} #{Id} ({Alive}/{Strength}, morale {Morale})";
}
