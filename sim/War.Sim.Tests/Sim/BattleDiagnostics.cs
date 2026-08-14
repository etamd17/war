using War.Sim.Core;
using War.Sim.Sim;
using War.Sim.Units;
using Xunit;
using Xunit.Abstractions;

namespace War.Sim.Tests.Sim;

/// <summary>
/// Not assertions — instrumentation. These print a running account of a battle so the
/// shape of it can actually be inspected: when contact happens, when units break, how
/// long it takes, what the casualty split looks like. A combat model can pass every
/// unit test and still produce battles that are over in nine seconds or never end at all.
/// </summary>
public class BattleDiagnostics
{
    private readonly ITestOutputHelper _out;

    public BattleDiagnostics(ITestOutputHelper output) => _out = output;

    [Fact]
    public void NarrateARomeVersusCarthageBattle()
    {
        var sim = BattleSim.Create(BattleFixtures.RomeVersusCarthage(seed: 4471));
        BattleState state = sim.State;

        _out.WriteLine("=== Rome vs Carthage, seed 4471 ===");
        foreach (Army army in state.Armies)
            _out.WriteLine($"{army.Name}: {army.InitialMen} men in {army.UnitIds.Length} units");
        _out.WriteLine("");

        int lastReport = -1;

        while (!sim.IsOver && state.Tick < SimConstants.TickRate * 60 * 25)
        {
            sim.Tick();

            foreach (BattleEvent e in state.Events)
            {
                if (e.Type == BattleEventType.UnitBroke)
                    _out.WriteLine($"[{Clock(state)}] BROKE: {state.Units[e.A]}");
                else if (e.Type == BattleEventType.UnitRallied)
                    _out.WriteLine($"[{Clock(state)}] RALLIED: {state.Units[e.A]}");
                else if (e.Type == BattleEventType.GeneralKilled)
                    _out.WriteLine($"[{Clock(state)}] GENERAL DOWN: {state.Armies[e.B].Name}");
            }
            state.DrainEvents();

            int minute = state.Tick / (SimConstants.TickRate * 60);
            if (minute != lastReport)
            {
                lastReport = minute;
                _out.WriteLine($"[{Clock(state)}] " +
                    $"Rome {BattleFixtures.EffectiveStrength(state, 0)} " +
                    $"| Carthage {BattleFixtures.EffectiveStrength(state, 1)}");
            }
        }

        _out.WriteLine("");
        _out.WriteLine($"Result: {state.Result}" +
            (state.Victor >= 0 ? $" — {state.Armies[state.Victor].Name}" : "") +
            $" after {Clock(state)}");
        _out.WriteLine("");

        foreach (Army army in state.Armies)
        {
            _out.WriteLine($"--- {army.Name} ---");
            foreach (int unitId in army.UnitIds)
            {
                Unit unit = state.Units[unitId];
                _out.WriteLine(
                    $"  {unit.Type.Name,-26} {unit.Alive,4}/{unit.Strength,-4} " +
                    $"morale {unit.Morale.ToDouble(),5:F1}  fatigue {unit.Fatigue.ToDouble(),4:F2}  " +
                    $"{unit.MoraleState}{(unit.Withdrawn ? " (fled)" : "")}");
            }
        }

        Assert.True(state.Tick > 0);
    }

    [Fact]
    public void ReportMeleeMatchups()
    {
        _out.WriteLine("=== Head-on duels, 60 seconds, flat ground ===");
        _out.WriteLine("");

        (string a, string b)[] matchups =
        [
            ("rome_hastati", "gaul_warband"),
            ("rome_principes", "carthage_libyan_spearmen"),
            ("greece_spartans", "gaul_fanatics"),
            ("rome_equites", "greece_hoplites"),
            ("rome_equites", "greece_peltasts"),
            ("carthage_elephants", "rome_hastati"),
            ("gaul_warband", "gaul_warband"),
        ];

        foreach ((string a, string b) in matchups)
        {
            var (state, left, right) = BattleFixtures.Duel(a, b);
            BattleFixtures.Engage(state, left, right, FixVec2.North, FixVec2.North, Fix.FromInt(30));

            var sim = new BattleSim(state);
            sim.RunSeconds(Fix.FromInt(60));

            _out.WriteLine(
                $"{left.Type.Name,-22} {left.Alive,4}/{left.Strength,-4} {left.MoraleState,-9}" +
                $"  vs  {right.Type.Name,-22} {right.Alive,4}/{right.Strength,-4} {right.MoraleState}");
        }

        Assert.True(true);
    }

    [Fact]
    public void ReportFlankAndRearAttacks()
    {
        _out.WriteLine("=== Defender pinned from the north, second unit hooks in ===");
        _out.WriteLine("=== Only the hook's bearing changes between runs          ===");
        _out.WriteLine("");

        (string label, FixVec2 bearing)[] angles =
        [
            ("front", FixVec2.North),
            ("flank", FixVec2.East),
            ("rear ", -FixVec2.North),
        ];

        foreach ((string label, FixVec2 bearing) in angles)
        {
            var (state, pin, hook, defender) = BattleFixtures.FlankScenario(
                "rome_principes", "carthage_libyan_spearmen", bearing);

            var sim = new BattleSim(state);
            int brokeAt = BattleFixtures.RunUntil(
                sim, () => defender.MoraleState == MoraleState.Routing, maxSeconds: 120);

            string outcome = brokeAt < 0
                ? "held out"
                : $"broke at {brokeAt / (double)SimConstants.TickRate,5:F1}s";

            _out.WriteLine(
                $"hook from {label}: {outcome,-16} defender {defender.Alive,4}/{defender.Strength}  " +
                $"| attackers lost {pin.Strength - pin.Alive + hook.Strength - hook.Alive,3}  " +
                $"(hook engaged {hook.Engaged})");
        }

        Assert.True(true);
    }

    [Fact]
    public void ReportMissileEffectiveness()
    {
        _out.WriteLine("=== 30 seconds of shooting at 80 metres ===");
        _out.WriteLine("");

        (string shooter, string target, FormationType formation)[] cases =
        [
            ("rome_archers", "rome_hastati", FormationType.Line),
            ("rome_archers", "rome_hastati", FormationType.Testudo),
            ("rome_archers", "gaul_warband", FormationType.Line),
            ("carthage_balearic_slingers", "rome_principes", FormationType.Line),
            ("rome_archers", "rome_velites", FormationType.Skirmish),
        ];

        foreach ((string shooter, string target, FormationType formation) in cases)
        {
            var (state, shooterUnit, targetUnit) =
                BattleFixtures.Duel(shooter, target, rightFormation: formation);

            FixVec2 centre = new(state.Terrain.Size / 2, state.Terrain.Size / 2);
            state.Reposition(targetUnit, centre, -FixVec2.North);
            state.Reposition(shooterUnit, centre - FixVec2.North * Fix.FromInt(80), FixVec2.North);

            shooterUnit.Order = UnitOrder.Hold();
            targetUnit.Order = UnitOrder.Hold();
            state.RebuildSpatialIndices();

            int ammoBefore = TotalAmmo(state, shooterUnit);

            var sim = new BattleSim(state);
            sim.RunSeconds(Fix.FromInt(30));

            _out.WriteLine(
                $"{shooterUnit.Type.Name,-26} -> {targetUnit.Type.Name,-16} ({formation,-8}) " +
                $"shot {ammoBefore - TotalAmmo(state, shooterUnit),4}  " +
                $"killed {targetUnit.Strength - targetUnit.Alive,3} of {targetUnit.Strength}");
        }

        Assert.True(true);
    }

    [Fact]
    public void ReportTerrainAndFatigueEffects()
    {
        _out.WriteLine("=== High ground and exhaustion, 60 seconds ===");
        _out.WriteLine("");

        // Identical units on a slope, set up the way high ground is actually used: the
        // uphill unit holds the crest and the downhill unit has to come up to it. Having
        // both charge would be a worse test — on a uniform slope they simply meet in the
        // middle at the same elevation, and neither has the high ground at all.
        var (hill, uphill, downhill) = BattleFixtures.Duel(
            "rome_principes", "rome_principes", terrain: BattleFixtures.Hillside(rise: 4));

        FixVec2 centre = new(hill.Terrain.Size / 2, hill.Terrain.Size / 2);
        hill.Reposition(downhill, centre, FixVec2.East);
        hill.Reposition(uphill, centre + FixVec2.East * Fix.FromInt(40), -FixVec2.East);
        uphill.Order = UnitOrder.Hold();
        downhill.Order = UnitOrder.Attack(uphill.Id);
        hill.RebuildSpatialIndices();

        var hillSim = new BattleSim(hill);
        for (int step = 0; step < 6; step++)
        {
            hillSim.RunSeconds(Fix.FromInt(10));
            _out.WriteLine(
                $"   t={hill.Tick / SimConstants.TickRate,2}s  " +
                $"high {uphill.Alive,4} at x={uphill.Centre.X.ToDouble(),6:F1} h={hill.Terrain.HeightAt(uphill.Centre).ToDouble(),5:F1} " +
                $"eng {uphill.Engaged,3} fat {uphill.Fatigue.ToDouble():F2} {uphill.MoraleState,-8} | " +
                $"low {downhill.Alive,4} at x={downhill.Centre.X.ToDouble(),6:F1} h={hill.Terrain.HeightAt(downhill.Centre).ToDouble(),5:F1} " +
                $"eng {downhill.Engaged,3} fat {downhill.Fatigue.ToDouble():F2} {downhill.MoraleState}");
        }

        _out.WriteLine($"on the high ground: {uphill.Alive,4}/{uphill.Strength}   " +
                       $"on the low ground: {downhill.Alive,4}/{downhill.Strength}");

        // Same fight, but one side has been run into the ground first.
        var (flat, fresh, tired) = BattleFixtures.Duel("rome_principes", "rome_principes");

        FixVec2 mid = new(flat.Terrain.Size / 2, flat.Terrain.Size / 2);
        flat.Reposition(tired, mid, FixVec2.North);
        flat.Reposition(fresh, mid + FixVec2.North * Fix.FromInt(28), -FixVec2.North);

        for (int s = tired.FirstSoldier; s < tired.EndSoldier; s++)
            flat.Fatigue[s] = Fix.Ratio(95, 100);

        fresh.Order = UnitOrder.Attack(tired.Id);
        tired.Order = UnitOrder.Attack(fresh.Id);
        flat.RebuildSpatialIndices();

        new BattleSim(flat).RunSeconds(Fix.FromInt(60));

        _out.WriteLine($"fresh:              {fresh.Alive,4}/{fresh.Strength}   " +
                       $"exhausted:         {tired.Alive,4}/{tired.Strength}");

        Assert.True(true);
    }

    private static int TotalAmmo(BattleState state, Unit unit)
    {
        int total = 0;
        for (int s = unit.FirstSoldier; s < unit.EndSoldier; s++)
            if (state.State[s] != SoldierState.Dead) total += state.Ammo[s];
        return total;
    }

    private static string Clock(BattleState state)
    {
        int seconds = state.Tick / SimConstants.TickRate;
        return $"{seconds / 60:D2}:{seconds % 60:D2}";
    }
}
