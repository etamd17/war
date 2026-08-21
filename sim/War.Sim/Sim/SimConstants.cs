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
    /// <summary>
    /// Seconds to ticks, in 64-bit.
    ///
    /// The obvious one-liner — multiply the Fix by the tick rate and round — overflows at
    /// about eighteen minutes of simulated time, because a Q16.16 second is already
    /// sixty-five thousand raw units and thirty of those per second runs an int out at
    /// 2^31. Everything in a battle is measured in seconds or fractions of one, so this
    /// never fired in anger.
    ///
    /// It fired the moment the campaign layer asked for the battle time limit, which is
    /// forty minutes. The count came back negative, Run was handed a negative number of
    /// ticks and did nothing at all, and every battle came back InProgress — reported as a
    /// stalemate, because that is what the result mapping does with a battle that has not
    /// finished. Nineteen campaign battles in twenty resolved as draws and the fault
    /// looked, from the outside, like a badly written estimate model.
    /// </summary>
    public static int Ticks(Fix seconds) =>
        (int)(((long)seconds.Raw * TickRate + (Fix.OneRaw >> 1)) >> Fix.FractionalBits);

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

    /// <summary>
    /// Base probability that a strike lands at a typical attack-versus-defence
    /// differential.
    ///
    /// Deliberately low. Most blows in a real melee were blocked, parried, turned by a
    /// shield, or simply missed; men did not fall at every exchange. Tuned together with
    /// <see cref="MeleeTempo"/> so that roughly fifty men in contact kill about one man
    /// per second between them — which puts a decisive head-on clash between equal
    /// heavy infantry at a minute or two, decided by one side breaking. At four times
    /// this rate a hundred and twenty men evaporated in ten seconds and the battle was
    /// over before the player could react to it.
    /// </summary>
    public static readonly Fix BaseHitChance = Fix.Ratio(135, 10000);

    /// <summary>
    /// Recentres offense against defence. Defence sums three stats — skill, shield and
    /// armour — while offense is essentially one, so the two are not on the same scale
    /// and a raw subtraction sits permanently near the floor. This offset puts a normal
    /// head-on matchup at <see cref="BaseHitChance"/> instead, which is what makes the
    /// modifiers on either side meaningful rather than academic.
    /// </summary>
    public static readonly Fix DefenceOffset = Fix.FromInt(7);

    /// <summary>
    /// How much each point of offense-over-defence shifts the hit chance.
    ///
    /// Scaled in lockstep with <see cref="BaseHitChance"/>, which is the point: every
    /// ratio in the combat model — flanking against frontal, spear against horse, fresh
    /// against spent — is a ratio of these two numbers, so scaling both leaves the shape
    /// of every fight untouched and changes only how long it takes.
    /// </summary>
    public static readonly Fix HitChancePerPoint = Fix.Ratio(40, 10000);

    /// <summary>Even a hopeless attacker connects sometimes.</summary>
    public static readonly Fix MinHitChance = Fix.Ratio(30, 10000);

    /// <summary>
    /// And even a hopeless defender is never a free kill. Deliberately scaled down less
    /// than the rest, so an elephant hitting a charge home is still doing something
    /// visibly different from two swordsmen trading blows.
    /// </summary>
    public static readonly Fix MaxHitChance = Fix.Ratio(18, 100);

    /// <summary>
    /// Global multiplier on how often men swing. The roster carries the relative timings
    /// — a spearman is slower than a swordsman, an elephant slower still — and this one
    /// knob sets the pace of the whole battle without touching any of them.
    ///
    /// Tuned so a head-on fight between evenly matched heavy infantry takes minutes and
    /// is decided by one side breaking, rather than by one side being killed to a man in
    /// half a minute.
    /// </summary>
    public static readonly Fix MeleeTempo = Fix.Ratio(26, 10);

    /// <summary>
    /// The battle-pace knob. Multiplies every hit chance, melee and missile alike, after
    /// all modifiers have been applied.
    ///
    /// This is the one to reach for when battles are the wrong length, in preference to
    /// <see cref="MeleeTempo"/>. Tempo changes how often men swing, which also changes
    /// how many blows land inside the five-second charge window and so quietly reweights
    /// charges against grinding. Lethality changes only how often a swing kills, leaving
    /// every ratio in the model — flank against front, spear against horse, fresh
    /// against spent, charge against standing — exactly where it was.
    ///
    /// It has to scale missiles too. Archery is capped by ammunition rather than by
    /// time, so slowing melee alone lets a fixed budget of arrows spent in the opening
    /// minutes carry units to their breaking point before the infantry fight has
    /// happened, and the battle ends on schedule no matter how gentle the melee is.
    ///
    /// Lower is longer. 1.0 gave roughly three-and-a-half-minute battles.
    /// </summary>
    public static readonly Fix Lethality = Fix.Ratio(32, 100);

    /// <summary>Attack bonus for striking a soldier from outside his frontal arc.</summary>
    public const int FlankAttackBonus = 4;

    /// <summary>Attack bonus for striking a soldier from behind.</summary>
    public const int RearAttackBonus = 8;

    /// <summary>Half-width of the frontal arc, as a dot-product threshold. About ±90°.</summary>
    public static readonly Fix FrontArcCosine = Fix.Zero;

    /// <summary>Behind this dot product an attack counts as coming from the rear.</summary>
    public static readonly Fix RearArcCosine = Fix.Ratio(-7, 10);

    /// <summary>
    /// Attack points per unit of ground <em>slope</em> between two men in contact.
    ///
    /// Slope rather than elevation difference, which sounds like a detail and is not.
    /// Two soldiers close enough to hit each other are about a metre and a half apart;
    /// on a one-in-eight hillside that is twenty centimetres of height, and scoring the
    /// bonus on elevation gives almost exactly zero — terrain stops mattering, and a
    /// unit that fought its way to the crest gains nothing for it. What actually decides
    /// the exchange is that one man is on the slope below the other: poor footing,
    /// striking upward, the other man's weight coming down on him. That is the gradient,
    /// and it does not shrink just because the two of them are standing close together.
    /// </summary>
    /// <summary>
    /// What one chevron of experience is worth in a fight.
    ///
    /// Three chevrons for a point of attack and a point of defence, so a fully veteran
    /// regiment is worth about three points of each — real, and nothing like enough to
    /// carry a bad matchup. Veterancy should mean the army you kept alive is better than
    /// the one you bought last turn, not that it is unbeatable.
    /// </summary>
    public static readonly Fix ExperiencePerChevron = Fix.Ratio(1, 3);

    /// <summary>
    /// And what it is worth to their nerve, on the hundred-point morale scale.
    ///
    /// Deliberately the larger effect. Men who have already stood in a line and watched it
    /// hold are slower to decide that this one will not, and morale is what decides ancient
    /// battles — so this is where veterancy should be felt.
    /// </summary>
    public static readonly Fix MoralePerChevron = Fix.Two;

    /// <summary>Chevrons a regiment can earn. Nine, as is traditional.</summary>
    public const int MaxExperience = 9;

    public static readonly Fix SlopeCombatFactor = Fix.FromInt(16);

    public const int MaxHeightBonus = 4;

    /// <summary>Seconds over which a charge bonus bleeds away once contact is made.</summary>
    public static readonly Fix ChargeDecaySeconds = Fix.FromInt(5);

    // ----------------------------------------------------------------- missiles

    // Missiles are deliberately far less lethal per shot than a sword stroke. Ancient
    // archery suppressed, disordered, and wore down; it rarely decided a battle on its
    // own. These numbers put a good archer unit at roughly thirty casualties over
    // thirty seconds of sustained fire against a shielded line, and far more against
    // an unarmoured one — which is the difference that should drive your decisions.
    // Halved alongside the melee rebalance, and for a reason that is easy to miss:
    // missile output is capped by ammunition, not by time. Slowing melee without
    // slowing archery does not merely shift the ratio — it lets a fixed budget of
    // arrows, all spent in the first two minutes, carry units to their breaking point
    // before the infantry grind has had a chance to happen at all. The battle then
    // finishes on schedule regardless of how gentle the swordfighting has become.
    public static readonly Fix MissileBaseHitChance = Fix.Ratio(5, 100);
    public static readonly Fix MissileHitChancePerPoint = Fix.Ratio(1, 100);
    public static readonly Fix MissileMinHitChance = Fix.Ratio(1, 100);
    public static readonly Fix MissileMaxHitChance = Fix.Ratio(30, 100);

    /// <summary>
    /// How close a shot must land to a man to have a chance of hitting him. This is what
    /// makes formation density matter: the same volley into a packed line and into loose
    /// order finds very different numbers of bodies, before any modifier is applied.
    /// </summary>
    public static readonly Fix MissileBodyRadius = Fix.Ratio(6, 10);

    /// <summary>Scatter as a fraction of the distance shot, added to a fixed metre.</summary>
    public static readonly Fix MissileScatterPerMetre = Fix.Ratio(5, 100);

    /// <summary>Fraction of missile hits that woodland canopy stops outright.</summary>
    public static readonly Fix ForestMissileCover = Fix.Ratio(45, 100);

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

    /// <summary>
    /// Morale runs 0 to 100, and a fresh unit of decent troops sits near 80. These
    /// thresholds are set high on purpose.
    ///
    /// With a break line at 10 the numbers said units broke "well before they ran out of
    /// men", and battles said otherwise: units were grinding down to eighty percent
    /// casualties before anyone left, and the winning army was finishing with half its
    /// strength gone. That is not what happened in ancient battles and it is not what
    /// the genre feels like — a beaten unit leaves at around a third to a half, and the
    /// victor walks off the field largely intact.
    /// </summary>
    public static readonly Fix MoraleWaveringThreshold = Fix.FromInt(45);
    public static readonly Fix MoraleBreakThreshold = Fix.FromInt(30);
    public static readonly Fix MoraleRallyThreshold = Fix.FromInt(50);

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
