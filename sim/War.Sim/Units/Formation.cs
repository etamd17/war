using War.Sim.Core;

namespace War.Sim.Units;

public enum FormationType : byte
{
    /// <summary>The default. Wide frontage, a few ranks deep.</summary>
    Line = 0,
    /// <summary>Narrow and deep. For marching through gaps, useless for fighting.</summary>
    Column = 1,
    /// <summary>A triangle. Concentrates a cavalry charge onto one point to punch through.</summary>
    Wedge = 2,
    /// <summary>No flanks and no rear. The answer to being ridden down from every side.</summary>
    Square = 3,
    /// <summary>Shields locked overhead. Near-immune to missiles, feeble in a fight, slow.</summary>
    Testudo = 4,
    /// <summary>Levelled pikes. A wall in front, an open door on the flank.</summary>
    Phalanx = 5,
    /// <summary>Loose order. Halves missile casualties, spreads out, cannot hold a line.</summary>
    Skirmish = 6,
}

[Flags]
public enum FormationMask : byte
{
    None = 0,
    Line = 1 << 0,
    Column = 1 << 1,
    Wedge = 1 << 2,
    Square = 1 << 3,
    Testudo = 1 << 4,
    Phalanx = 1 << 5,
    Skirmish = 1 << 6,

    Standard = Line | Column | Square,
    Foot = Line | Column | Square | Skirmish,
    Legionary = Line | Column | Square | Testudo | Skirmish,
    Hoplite = Line | Column | Square | Phalanx,
    Horse = Line | Column | Wedge,
}

public static class FormationMaskExtensions
{
    public static FormationMask ToMask(this FormationType type) => (FormationMask)(1 << (int)type);
}

/// <summary>
/// How a formation modifies the men standing in it. Everything is a multiplier on, or an
/// addition to, the unit's own stats.
/// </summary>
public readonly struct FormationProfile
{
    /// <summary>Multiplies the base file spacing. Loose orders spread, tight orders pack.</summary>
    public required Fix FileSpacingScale { get; init; }

    public required Fix RankSpacingScale { get; init; }

    /// <summary>Added to attack for every man in the formation.</summary>
    public required int AttackBonus { get; init; }

    /// <summary>Added to defence against attacks from the front.</summary>
    public required int FrontDefenceBonus { get; init; }

    /// <summary>Added to defence against attacks from flank or rear. Usually negative.</summary>
    public required int FlankDefenceBonus { get; init; }

    /// <summary>Multiplies incoming missile hit chance. Testudo is near zero, skirmish is halved.</summary>
    public required Fix MissileVulnerability { get; init; }

    public required Fix SpeedScale { get; init; }

    /// <summary>Multiplies the charge bonus this formation delivers.</summary>
    public required Fix ChargeScale { get; init; }

    /// <summary>Extra reach — pikes engage a rank or two early.</summary>
    public required Fix ExtraReach { get; init; }

    /// <summary>
    /// If true, an enemy charging into the frontal arc gets no charge bonus at all.
    /// This is the whole reason to stand in a phalanx.
    /// </summary>
    public required bool NegatesFrontalCharge { get; init; }

    /// <summary>If true, men face outward from the unit centre rather than all one way.</summary>
    public required bool AllRoundDefence { get; init; }

    /// <summary>Preferred ranks deep when the player has not set a width by hand.</summary>
    public required int PreferredDepth { get; init; }
}

/// <summary>
/// Formation geometry and effects.
///
/// A formation is two things: a function from soldier index to a local offset, and a set
/// of modifiers. The offsets use the convention established in <see cref="FixVec2.Rotate"/> —
/// local +X is forward, local +Y is left — so a slot is placed in the world by rotating
/// its offset by the unit's facing and adding the unit's centre. No trigonometry, and the
/// whole formation pivots correctly for free.
/// </summary>
public static class Formations
{
    public static FormationProfile Profile(FormationType type) => type switch
    {
        FormationType.Line => new FormationProfile
        {
            FileSpacingScale = Fix.One,
            RankSpacingScale = Fix.One,
            AttackBonus = 0,
            FrontDefenceBonus = 0,
            FlankDefenceBonus = 0,
            MissileVulnerability = Fix.One,
            SpeedScale = Fix.One,
            ChargeScale = Fix.One,
            ExtraReach = Fix.Zero,
            NegatesFrontalCharge = false,
            AllRoundDefence = false,
            PreferredDepth = 4,
        },

        FormationType.Column => new FormationProfile
        {
            FileSpacingScale = Fix.Ratio(9, 10),
            RankSpacingScale = Fix.Ratio(9, 10),
            AttackBonus = -2,
            FrontDefenceBonus = -1,
            FlankDefenceBonus = -2,
            MissileVulnerability = Fix.Ratio(115, 100),
            SpeedScale = Fix.Ratio(115, 100),
            ChargeScale = Fix.Ratio(8, 10),
            ExtraReach = Fix.Zero,
            NegatesFrontalCharge = false,
            AllRoundDefence = false,
            PreferredDepth = 12,
        },

        // Concentrates the charge on a point. Everything behind the tip piles in.
        FormationType.Wedge => new FormationProfile
        {
            FileSpacingScale = Fix.Ratio(95, 100),
            RankSpacingScale = Fix.Ratio(95, 100),
            AttackBonus = 1,
            FrontDefenceBonus = 0,
            FlankDefenceBonus = -2,
            MissileVulnerability = Fix.One,
            SpeedScale = Fix.One,
            ChargeScale = Fix.Ratio(140, 100),
            ExtraReach = Fix.Zero,
            NegatesFrontalCharge = false,
            AllRoundDefence = false,
            PreferredDepth = 6,
        },

        // No flanks to turn, but nothing much pointing at the enemy either.
        FormationType.Square => new FormationProfile
        {
            FileSpacingScale = Fix.Ratio(9, 10),
            RankSpacingScale = Fix.Ratio(9, 10),
            AttackBonus = -1,
            FrontDefenceBonus = 1,
            FlankDefenceBonus = 4,
            MissileVulnerability = Fix.Ratio(125, 100),
            SpeedScale = Fix.Ratio(6, 10),
            ChargeScale = Fix.Ratio(5, 10),
            ExtraReach = Fix.Zero,
            NegatesFrontalCharge = false,
            AllRoundDefence = true,
            PreferredDepth = 0,   // computed as a square
        },

        // Shields locked. Arrows bounce off; so does most of your own fighting ability.
        FormationType.Testudo => new FormationProfile
        {
            FileSpacingScale = Fix.Ratio(65, 100),
            RankSpacingScale = Fix.Ratio(7, 10),
            AttackBonus = -4,
            FrontDefenceBonus = 2,
            FlankDefenceBonus = 0,
            MissileVulnerability = Fix.Ratio(15, 100),
            SpeedScale = Fix.Ratio(45, 100),
            ChargeScale = Fix.Zero,
            ExtraReach = Fix.Zero,
            NegatesFrontalCharge = false,
            AllRoundDefence = false,
            PreferredDepth = 6,
        },

        // A wall of points. Do not attack it from the front, and do not let it be flanked.
        FormationType.Phalanx => new FormationProfile
        {
            FileSpacingScale = Fix.Ratio(85, 100),
            RankSpacingScale = Fix.Ratio(9, 10),
            AttackBonus = 2,
            FrontDefenceBonus = 6,
            FlankDefenceBonus = -6,
            MissileVulnerability = Fix.Ratio(11, 10),
            SpeedScale = Fix.Ratio(5, 10),
            ChargeScale = Fix.Ratio(3, 10),
            ExtraReach = Fix.FromInt(2),
            NegatesFrontalCharge = true,
            AllRoundDefence = false,
            PreferredDepth = 8,
        },

        // Spread out. Halves what archery does to you and lets you get away.
        FormationType.Skirmish => new FormationProfile
        {
            FileSpacingScale = Fix.Ratio(22, 10),
            RankSpacingScale = Fix.Ratio(22, 10),
            AttackBonus = -2,
            FrontDefenceBonus = -2,
            FlankDefenceBonus = 0,
            MissileVulnerability = Fix.Ratio(5, 10),
            SpeedScale = Fix.Ratio(11, 10),
            ChargeScale = Fix.Ratio(6, 10),
            ExtraReach = Fix.Zero,
            NegatesFrontalCharge = false,
            AllRoundDefence = false,
            PreferredDepth = 2,
        },

        _ => Profile(FormationType.Line),
    };

    /// <summary>
    /// The natural frontage for a formation holding <paramref name="strength"/> men,
    /// used when the player has not dragged a width out by hand.
    /// </summary>
    public static int DefaultWidth(FormationType type, int strength)
    {
        if (strength <= 0) return 1;

        if (type == FormationType.Square)
        {
            int side = 1;
            while (side * side < strength) side++;
            return side;
        }

        if (type == FormationType.Wedge)
        {
            // A wedge's "width" is its number of ranks; the base widens as it deepens.
            int ranks = 1;
            while (ranks * (ranks + 1) / 2 < strength) ranks++;
            return ranks;
        }

        int depth = Profile(type).PreferredDepth;
        if (depth < 1) depth = 1;

        int width = (strength + depth - 1) / depth;
        return width < 1 ? 1 : width;
    }

    /// <summary>
    /// Local offset for one slot, with +X forward and +Y left. Rotate by the unit's
    /// facing and add its centre to get a world position.
    /// </summary>
    /// <param name="slot">Slot index, 0 to strength−1. Slot 0 is front and centre.</param>
    /// <param name="width">Frontage in files, or ranks for a wedge, or side for a square.</param>
    public static FixVec2 SlotOffset(
        FormationType type, int slot, int width, int strength, Fix fileSpacing, Fix rankSpacing)
    {
        if (width < 1) width = 1;

        FormationProfile profile = Profile(type);
        Fix file = fileSpacing * profile.FileSpacingScale;
        Fix rank = rankSpacing * profile.RankSpacingScale;

        return type switch
        {
            FormationType.Wedge => WedgeOffset(slot, file, rank),
            FormationType.Square => SquareOffset(slot, width, file, rank),
            _ => RectangleOffset(slot, width, strength, file, rank),
        };
    }

    /// <summary>
    /// Rectangular block, filled front rank first. The last rank is centred rather than
    /// left-aligned, so a unit that has taken casualties still looks like a formation
    /// instead of a comb with a missing tooth.
    /// </summary>
    private static FixVec2 RectangleOffset(int slot, int width, int strength, Fix file, Fix rank)
    {
        int rankIndex = slot / width;
        int fileIndex = slot % width;

        int fullRanks = strength / width;
        int remainder = strength - fullRanks * width;
        int rankWidth = rankIndex < fullRanks ? width : remainder;
        if (rankWidth < 1) rankWidth = 1;

        // Centre each rank about the unit's axis: file 0 is on the left.
        Fix lateral = (Fix.Ratio(rankWidth - 1, 2) - Fix.FromInt(fileIndex)) * file;
        Fix depth = -Fix.FromInt(rankIndex) * rank;

        return new FixVec2(depth, lateral);
    }

    /// <summary>
    /// Triangle with the point forward: rank r holds r+1 men. Everything behind the tip
    /// funnels into the hole the tip makes.
    /// </summary>
    private static FixVec2 WedgeOffset(int slot, Fix file, Fix rank)
    {
        // Find which rank this slot falls in: ranks 0..r hold (r+1)(r+2)/2 men in total.
        int rankIndex = 0;
        int consumed = 0;
        while (consumed + rankIndex + 1 <= slot)
        {
            consumed += rankIndex + 1;
            rankIndex++;
        }

        int indexInRank = slot - consumed;
        int rankWidth = rankIndex + 1;

        Fix lateral = (Fix.Ratio(rankWidth - 1, 2) - Fix.FromInt(indexInRank)) * file;
        Fix depth = -Fix.FromInt(rankIndex) * rank;

        return new FixVec2(depth, lateral);
    }

    /// <summary>
    /// A solid block as wide as it is deep, centred on the unit. Combined with
    /// <see cref="FormationProfile.AllRoundDefence"/> the men face outward, so there is
    /// no flank to find.
    /// </summary>
    private static FixVec2 SquareOffset(int slot, int side, Fix file, Fix rank)
    {
        if (side < 1) side = 1;
        int rankIndex = slot / side;
        int fileIndex = slot % side;

        Fix lateral = (Fix.Ratio(side - 1, 2) - Fix.FromInt(fileIndex)) * file;
        Fix depth = (Fix.Ratio(side - 1, 2) - Fix.FromInt(rankIndex)) * rank;

        return new FixVec2(depth, lateral);
    }

    /// <summary>
    /// Half the frontage of a formation in metres — how much ground it covers, which is
    /// what the AI uses to decide whether it can match or overlap an enemy line.
    /// </summary>
    public static Fix HalfFrontage(
        FormationType type, int width, int strength, Fix fileSpacing)
    {
        if (width < 1) width = 1;
        FormationProfile profile = Profile(type);
        Fix file = fileSpacing * profile.FileSpacingScale;

        int files = type switch
        {
            FormationType.Wedge => width,                 // widest rank is the last
            FormationType.Square => width,
            _ => width < strength ? width : strength,
        };
        if (files < 1) files = 1;

        return Fix.Ratio(files - 1, 2) * file;
    }
}
