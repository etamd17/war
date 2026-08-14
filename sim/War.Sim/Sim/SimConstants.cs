using War.Sim.Core;

namespace War.Sim.Sim;

/// <summary>
/// Global simulation constants. These are the numbers that shape how the whole battle
/// feels, gathered in one place so tuning is a single file rather than a treasure hunt.
/// </summary>
public static class SimConstants
{
    /// <summary>
    /// Simulation ticks per second. Fixed forever — the sim never sees a frame delta.
    /// 30 Hz is fine granularity for melee exchanges while leaving the renderer free to
    /// interpolate to whatever the monitor wants.
    /// </summary>
    public const int TickRate = 30;

    /// <summary>Seconds per tick, as an exact rational.</summary>
    public static readonly Fix TickSeconds = Fix.Ratio(1, TickRate);

    /// <summary>Converts a per-second rate into a per-tick amount.</summary>
    public static Fix PerSecond(Fix ratePerSecond) => ratePerSecond * TickSeconds;

    /// <summary>Converts a duration in seconds into a whole number of ticks.</summary>
    public static int Ticks(Fix seconds) => (seconds * TickRate).RoundToInt;

    // ------------------------------------------------------------------ spatial

    /// <summary>
    /// Spatial hash cell size in metres. Roughly the radius of the widest common
    /// neighbour query, which keeps the cell sweep down to a 3×3 for most lookups.
    /// </summary>
    public static readonly Fix SpatialCellSize = Fix.FromInt(4);

    /// <summary>How close two soldiers must be before they can trade blows.</summary>
    public static readonly Fix MeleeReach = Fix.Ratio(13, 10);

    /// <summary>Reach for pikes and levelled spears, which engage a rank early.</summary>
    public static readonly Fix PikeReach = Fix.Ratio(35, 10);

    /// <summary>Radius within which a soldier pushes against his neighbours.</summary>
    public static readonly Fix SeparationRadius = Fix.Ratio(9, 10);

    // ------------------------------------------------------------------- combat

    /// <summary>Base probability that a strike lands before any modifiers.</summary>
    public static readonly Fix BaseHitChance = Fix.Ratio(35, 100);

    /// <summary>How much each point of offense-over-defence shifts the hit chance.</summary>
    public static readonly Fix HitChancePerPoint = Fix.Ratio(3, 100);

    /// <summary>Even a hopeless attacker connects sometimes.</summary>
    public static readonly Fix MinHitChance = Fix.Ratio(5, 100);

    /// <summary>And even a hopeless defender is never a free kill.</summary>
    public static readonly Fix MaxHitChance = Fix.Ratio(90, 100);

    /// <summary>Attack bonus for striking a soldier from outside his frontal arc.</summary>
    public const int FlankAttackBonus = 4;

    /// <summary>Attack bonus for striking a soldier from behind.</summary>
    public const int RearAttackBonus = 8;

    /// <summary>Half-width of the frontal arc, as a dot-product threshold. About ±90°.</summary>
    public static readonly Fix FrontArcCosine = Fix.Zero;

    /// <summary>Behind this dot product an attack counts as coming from the rear.</summary>
    public static readonly Fix RearArcCosine = Fix.Ratio(-7, 10);

    /// <summary>Attack points per metre of height advantage, capped by <see cref="MaxHeightBonus"/>.</summary>
    public static readonly Fix HeightBonusPerMetre = Fix.Ratio(4, 10);

    public const int MaxHeightBonus = 4;

    /// <summary>Seconds over which a charge bonus bleeds away once contact is made.</summary>
    public static readonly Fix ChargeDecaySeconds = Fix.FromInt(5);

    // ------------------------------------------------------------------ fatigue

    /// <summary>Fatigue runs 0 (fresh) to 1 (exhausted).</summary>
    public static readonly Fix FatiguePerSecondRunning = Fix.Ratio(1, 90);
    public static readonly Fix FatiguePerSecondFighting = Fix.Ratio(1, 120);
    public static readonly Fix FatiguePerSecondWalking = Fix.Ratio(1, 400);
    public static readonly Fix FatigueRecoveryPerSecond = Fix.Ratio(1, 150);

    /// <summary>Attack and defence points lost at full exhaustion.</summary>
    public const int MaxFatiguePenalty = 6;

    /// <summary>Fraction of speed an exhausted man loses.</summary>
    public static readonly Fix FatigueSpeedLoss = Fix.Ratio(40, 100);

    // ------------------------------------------------------------------- morale

    /// <summary>Morale runs 0 to 100. Units break well before they run out of men.</summary>
    public static readonly Fix MoraleWaveringThreshold = Fix.FromInt(25);
    public static readonly Fix MoraleBreakThreshold = Fix.FromInt(10);
    public static readonly Fix MoraleRallyThreshold = Fix.FromInt(30);

    /// <summary>Ticks a unit must hold below the break line before it actually routs.</summary>
    public const int BreakConfirmTicks = 20;

    /// <summary>Seconds a routing unit must be clear of pursuit before it may rally.</summary>
    public static readonly Fix RallyCooldownSeconds = Fix.FromInt(12);

    /// <summary>Radius within which a general steadies his men.</summary>
    public static readonly Fix GeneralAuraRadius = Fix.FromInt(60);

    /// <summary>Radius within which a friendly rout is contagious.</summary>
    public static readonly Fix RoutContagionRadius = Fix.FromInt(45);

    /// <summary>Radius used when working out whether a unit is locally outnumbered.</summary>
    public static readonly Fix LocalOddsRadius = Fix.FromInt(40);

    // ------------------------------------------------------------------ victory

    /// <summary>Fraction of an army that must be dead or routed for it to lose the field.</summary>
    public static readonly Fix ArmyBreakFraction = Fix.Ratio(70, 100);

    /// <summary>How far past the map edge a routing unit runs before it is gone for good.</summary>
    public static readonly Fix WithdrawDistance = Fix.FromInt(40);
}
