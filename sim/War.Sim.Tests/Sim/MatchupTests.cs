using War.Sim.Sim;
using Xunit;

namespace War.Sim.Tests.Sim;

/// <summary>
/// Holds the standard matchup's two army lists to each other.
///
/// This test exists because its absence cost a working afternoon. Carthage was fielding
/// 5110 roster points against Rome's 4210 and had been for as long as the matchup had
/// existed. Nothing reported it: a points gap does not surface as a warning or a failing
/// assertion, it surfaces as the losing army's commander appearing to be badly written —
/// which is indistinguishable from a commander that IS badly written.
///
/// So the gap was blamed on the commander. Six sweeps of fourteen battles each went into
/// tuning formation doctrine, target selection and skirmisher behaviour against a
/// scoreboard that could not move, because none of it was the problem. Adding the two
/// columns up took a minute and closed a thirty-point survival gap to four.
/// </summary>
public class MatchupTests
{
    /// <summary>
    /// How far apart the two lists may be, in points.
    ///
    /// Armies are built from whole units, so they will never come out equal — the current
    /// pair differ by twenty. A few per cent is the granularity of the roster; anything
    /// wider is a thumb on the scale.
    /// </summary>
    private const double Tolerance = 0.05;

    [Fact]
    public void TheStandardMatchupIsFoughtByArmiesOfEqualValue()
    {
        int rome = Matchups.Cost(Matchups.Rome());
        int carthage = Matchups.Cost(Matchups.Carthage());

        double gap = System.Math.Abs(rome - carthage) / (double)System.Math.Max(rome, carthage);

        Assert.True(
            gap <= Tolerance,
            $"Rome fields {rome} points against Carthage's {carthage} — {gap:P1} apart. " +
            "Every balance measurement taken against this matchup is measuring the gap " +
            "rather than whatever is being tuned. Fix the lists before tuning anything.");
    }

    [Fact]
    public void BothArmiesBringAGeneral()
    {
        // The morale model hangs an aura off the general and a large penalty off losing
        // him. An army without one is playing a different game from an army with one.
        Assert.Contains(Matchups.Rome().Units, u => u.TypeId.EndsWith("_general"));
        Assert.Contains(Matchups.Carthage().Units, u => u.TypeId.EndsWith("_general"));
    }
}
