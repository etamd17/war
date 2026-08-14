using War.Sim.Core;
using War.Sim.Sim;

namespace War.Sim.Units;

public enum Faction : byte
{
    Rome = 0,
    Carthage = 1,
    Gaul = 2,
    Greece = 3,
    Egypt = 4,
}

/// <summary>
/// Broad role. This is what the counter web keys off: spears beat mounts, mounts beat
/// missiles, missiles beat dense infantry, and so on round the circle.
/// </summary>
public enum UnitClass : byte
{
    /// <summary>Close-order swordsmen and axemen. The line that holds.</summary>
    Infantry = 0,
    /// <summary>Spearmen. Slower to kill, murderous to anything that charges them.</summary>
    Spear = 1,
    /// <summary>Pikemen. A frontal wall and an open invitation on the flank.</summary>
    Pike = 2,
    /// <summary>Javelins, bows, slings on foot. Screen, harass, retire.</summary>
    Missile = 3,
    /// <summary>Shock cavalry.</summary>
    Cavalry = 4,
    /// <summary>Horse archers and javelin cavalry.</summary>
    MissileCavalry = 5,
    /// <summary>Chariots. Terrifying, fragile, and hard to steer.</summary>
    Chariot = 6,
    /// <summary>Elephants. Break anything, then break themselves.</summary>
    Elephant = 7,
    /// <summary>The general and his bodyguard. Killing him is worth more than killing the unit.</summary>
    General = 8,
}

public enum MissileType : byte
{
    None = 0,
    Bow = 1,
    Sling = 2,
    Javelin = 3,
    /// <summary>Thrown once at close range just before contact, then the swords come out.</summary>
    Pilum = 4,
}

/// <summary>
/// An immutable unit template. Instances of these become the units in an army.
///
/// Stat scale, so the numbers mean something consistent:
/// attack 4–20, defence skill 2–15, shield 0–8, armour 0–12, charge 2–20,
/// morale 4–18, discipline 0–10. Speeds are metres per second.
/// </summary>
public sealed class UnitType
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Faction Faction { get; init; }
    public required UnitClass Class { get; init; }

    /// <summary>One line of flavour, shown on the unit card.</summary>
    public string Description { get; init; } = "";

    // -------------------------------------------------------------------- melee

    public int Attack { get; init; } = 7;

    /// <summary>Added to attack on impact, then bleeding away over five seconds.</summary>
    public int Charge { get; init; } = 2;

    public int DefenceSkill { get; init; } = 5;

    /// <summary>Counts only against attacks arriving in the frontal arc.</summary>
    public int Shield { get; init; }

    /// <summary>Counts from every direction, and blunts missiles especially well.</summary>
    public int Armour { get; init; }

    public int Hitpoints { get; init; } = 1;

    /// <summary>Seconds between strikes. Jittered per soldier so casualties stream rather than pulse.</summary>
    public Fix AttackInterval { get; init; } = Fix.Ratio(6, 5);

    /// <summary>How far a soldier of this type can reach to strike.</summary>
    public Fix Reach { get; init; } = SimConstants.MeleeReach;

    // ----------------------------------------------------------------- counters

    /// <summary>Added against cavalry, chariots, and elephants. This is what a spear wall is for.</summary>
    public int BonusVsMounted { get; init; }

    /// <summary>Added against foot. Cavalry get this once they are into a broken line.</summary>
    public int BonusVsInfantry { get; init; }

    // ------------------------------------------------------------------- morale

    public int Morale { get; init; } = 9;

    /// <summary>Resistance to panic, and how readily a broken unit will re-form.</summary>
    public int Discipline { get; init; } = 5;

    /// <summary>Elephants and chariots do not fear elephants and chariots.</summary>
    public bool ImmuneToFear { get; init; }

    /// <summary>Panics nearby enemies, and horses worst of all.</summary>
    public bool CausesFear { get; init; }

    // ----------------------------------------------------------------- movement

    public Fix WalkSpeed { get; init; } = Fix.Ratio(13, 10);
    public Fix RunSpeed { get; init; } = Fix.Ratio(32, 10);

    /// <summary>Turn rate in radians per second. Cavalry and chariots are notably worse.</summary>
    public Fix TurnRate { get; init; } = Fix.Ratio(25, 10);

    /// <summary>Used when soldiers shove: heavier men win the push.</summary>
    public Fix Mass { get; init; } = Fix.One;

    /// <summary>Collision radius in metres.</summary>
    public Fix Radius { get; init; } = Fix.Ratio(2, 5);

    public bool IsMounted => Class is UnitClass.Cavalry or UnitClass.MissileCavalry
        or UnitClass.Chariot or UnitClass.Elephant or UnitClass.General;

    // ---------------------------------------------------------------- missiles

    public MissileType Missile { get; init; } = MissileType.None;
    public Fix MissileRange { get; init; } = Fix.Zero;
    public int MissileAttack { get; init; }

    /// <summary>Shots per man. When it runs out, they are ordinary and rather bad infantry.</summary>
    public int Ammunition { get; init; }

    public Fix ReloadInterval { get; init; } = Fix.FromInt(3);

    /// <summary>Armour-piercing missiles ignore part of the target's armour. Slings dent helmets.</summary>
    public Fix ArmourPiercing { get; init; } = Fix.Zero;

    public bool HasMissiles => Missile != MissileType.None && Ammunition > 0;

    // ------------------------------------------------------------ organisation

    public int DefaultStrength { get; init; } = 120;

    public FormationType DefaultFormation { get; init; } = FormationType.Line;

    public FormationMask AllowedFormations { get; init; } =
        FormationMask.Line | FormationMask.Column | FormationMask.Square;

    /// <summary>Metres between men side by side, before the formation's own multiplier.</summary>
    public Fix FileSpacing { get; init; } = Fix.Ratio(8, 10);

    /// <summary>Metres between ranks, before the formation's own multiplier.</summary>
    public Fix RankSpacing { get; init; } = Fix.One;

    /// <summary>Recruitment cost. Unused in milestone 1; the campaign will want it.</summary>
    public int Cost { get; init; } = 300;

    // ---------------------------------------------------------------- derived

    private FixVec2? _turnStep;

    /// <summary>
    /// The maximum rotation this unit's men can perform in one tick, as a
    /// (cos θ, sin θ) pair ready to hand to <see cref="FixVec2.TurnTowards"/>.
    /// Computed once and cached, so pivoting costs no trigonometry at all.
    /// </summary>
    public FixVec2 TurnStepPerTick =>
        _turnStep ??= FixVec2.FromAngle(TurnRate * SimConstants.TickSeconds);

    public bool CanUse(FormationType formation) =>
        (AllowedFormations & formation.ToMask()) != 0;

    public override string ToString() => Name;
}
