using System.Diagnostics;
using War.Sim.Core;
using War.Sim.Sim;
using War.Sim.Sim.Systems;
using War.Sim.Units;
using Xunit;
using Xunit.Abstractions;

namespace War.Sim.Tests.Sim;

/// <summary>
/// What the battle engine has to get right, stated as behaviour rather than as
/// implementation. Every one of these corresponds to a decision a player is supposed to
/// be able to make: go round the flank, take the hill, hold a reserve, put spears in
/// front of cavalry, shoot the unarmoured troops rather than the armoured ones.
/// </summary>
public class BattleTests
{
    private readonly ITestOutputHelper _out;

    public BattleTests(ITestOutputHelper output) => _out = output;

    // ------------------------------------------------------------- determinism

    [Fact]
    public void SameSeedProducesABitIdenticalBattle()
    {
        var a = BattleSim.Create(BattleFixtures.RomeVersusCarthage(seed: 31337));
        var b = BattleSim.Create(BattleFixtures.RomeVersusCarthage(seed: 31337));

        for (int i = 0; i < 1200; i++)
        {
            a.Tick();
            b.Tick();
            a.State.DrainEvents();
            b.State.DrainEvents();

            if (i % 200 == 0)
                Assert.Equal(BattleFixtures.Fingerprint(a.State), BattleFixtures.Fingerprint(b.State));
        }

        Assert.Equal(BattleFixtures.Fingerprint(a.State), BattleFixtures.Fingerprint(b.State));
        Assert.Equal(a.State.Result, b.State.Result);
        Assert.Equal(a.State.Victor, b.State.Victor);
    }

    [Fact]
    public void DifferentSeedsProduceDifferentBattles()
    {
        var a = BattleSim.Create(BattleFixtures.RomeVersusCarthage(seed: 1));
        var b = BattleSim.Create(BattleFixtures.RomeVersusCarthage(seed: 2));

        a.Run(900);
        b.Run(900);

        Assert.NotEqual(BattleFixtures.Fingerprint(a.State), BattleFixtures.Fingerprint(b.State));
    }

    [Fact]
    public void ABattleActuallyResolves()
    {
        // A combat model that never reaches a decision is as broken as one that decides
        // in nine seconds. Both have shipped in real games.
        for (uint seed = 1; seed <= 3; seed++)
        {
            var sim = BattleSim.Create(BattleFixtures.RomeVersusCarthage(seed: seed));
            sim.Run(SimConstants.TickRate * 60 * 20);

            Assert.NotEqual(BattleResult.InProgress, sim.State.Result);

            double minutes = sim.State.Tick / (double)SimConstants.TickRate / 60;
            _out.WriteLine($"seed {seed}: {sim.State.Result} " +
                $"{(sim.State.Victor >= 0 ? sim.State.Armies[sim.State.Victor].Name : "-")} in {minutes:F1} min");

            Assert.InRange(minutes, 0.5, 20);
        }
    }

    // ------------------------------------------------------------- manoeuvre

    [Fact]
    public void AttackingTheRearBeatsAttackingTheFront()
    {
        var (frontState, frontPin, frontHook, frontTarget) = BattleFixtures.FlankScenario(
            "rome_principes", "carthage_libyan_spearmen", FixVec2.North);
        var (rearState, rearPin, rearHook, rearTarget) = BattleFixtures.FlankScenario(
            "rome_principes", "carthage_libyan_spearmen", -FixVec2.North);

        int frontBreak = BattleFixtures.RunUntil(
            new BattleSim(frontState), () => frontTarget.MoraleState == MoraleState.Routing);
        int rearBreak = BattleFixtures.RunUntil(
            new BattleSim(rearState), () => rearTarget.MoraleState == MoraleState.Routing);

        int frontCost = frontPin.Strength - frontPin.Alive + frontHook.Strength - frontHook.Alive;
        int rearCost = rearPin.Strength - rearPin.Alive + rearHook.Strength - rearHook.Alive;

        _out.WriteLine($"front: broke at tick {frontBreak}, cost {frontCost} men");
        _out.WriteLine($"rear:  broke at tick {rearBreak}, cost {rearCost} men");

        Assert.True(rearBreak > 0 && rearBreak < frontBreak,
            "a rear attack should break the defender sooner than a frontal one");
        Assert.True(rearCost < frontCost,
            "a rear attack should cost the attacker fewer men");
    }

    [Fact]
    public void APinnedUnitWheelsFarSlowerThanAnIdleOne()
    {
        // The rule that makes flanking worth anything. Stated comparatively rather than
        // as an absolute angle, because the number that matters is the difference: an
        // engaged formation cannot turn to meet you, an idle one can and quite correctly
        // will.
        Fix TurnedAfter(bool pinned)
        {
            var (state, pin, _, defender) = BattleFixtures.FlankScenario(
                "rome_principes", "carthage_libyan_spearmen", FixVec2.East, hookDistance: 200);

            // Same scenario either way; the only difference is whether anything is
            // holding the defender's front.
            if (!pinned) state.Reposition(pin, new FixVec2(Fix.FromInt(40), Fix.FromInt(40)), FixVec2.North);
            pin.Order = pinned ? UnitOrder.Attack(defender.Id) : UnitOrder.Hold();

            // Let contact be established *before* asking for the turn. Ordering the turn
            // up front measures nothing: the defender completes a ninety-degree wheel in
            // well under a second while the pin is still walking toward it.
            var sim = new BattleSim(state);
            sim.RunSeconds(Fix.FromInt(10));

            FixVec2 start = defender.Facing;
            defender.Order = UnitOrder.MoveTo(defender.Anchor, defender.Facing.Right);

            sim.RunSeconds(Fix.FromInt(3));
            return FixVec2.Dot(start, defender.Facing);
        }

        Fix whilePinned = TurnedAfter(pinned: true);
        Fix whileIdle = TurnedAfter(pinned: false);

        _out.WriteLine($"after 6s: pinned kept dot {whilePinned}, idle kept dot {whileIdle}");
        Assert.True(whilePinned > whileIdle,
            "a unit locked in melee should wheel far less than one standing free");
    }

    [Fact]
    public void HighGroundWinsAnOtherwiseEvenFight()
    {
        var (state, uphill, downhill) = BattleFixtures.Duel(
            "rome_principes", "rome_principes", terrain: BattleFixtures.Hillside(rise: 4));

        FixVec2 centre = new(state.Terrain.Size / 2, state.Terrain.Size / 2);
        state.Reposition(downhill, centre, FixVec2.East);
        state.Reposition(uphill, centre + FixVec2.East * Fix.FromInt(40), -FixVec2.East);
        uphill.Order = UnitOrder.Hold();
        downhill.Order = UnitOrder.Attack(uphill.Id);
        state.RebuildSpatialIndices();

        new BattleSim(state).RunSeconds(Fix.FromInt(60));

        _out.WriteLine($"uphill {uphill.Alive}/{uphill.Strength}, downhill {downhill.Alive}/{downhill.Strength}");
        Assert.True(uphill.Alive > downhill.Alive * 2,
            "identical units, and the one holding the hill should win comfortably");
    }

    [Fact]
    public void ExhaustedTroopsLoseToFreshOnes()
    {
        var (state, fresh, tired) = BattleFixtures.Duel("rome_principes", "rome_principes");

        FixVec2 centre = new(state.Terrain.Size / 2, state.Terrain.Size / 2);
        state.Reposition(tired, centre, FixVec2.North);
        state.Reposition(fresh, centre + FixVec2.North * Fix.FromInt(28), -FixVec2.North);

        for (int s = tired.FirstSoldier; s < tired.EndSoldier; s++)
            state.Fatigue[s] = Fix.Ratio(95, 100);

        fresh.Order = UnitOrder.Attack(tired.Id);
        tired.Order = UnitOrder.Attack(fresh.Id);
        state.RebuildSpatialIndices();

        new BattleSim(state).RunSeconds(Fix.FromInt(60));

        _out.WriteLine($"fresh {fresh.Alive}/{fresh.Strength}, exhausted {tired.Alive}/{tired.Strength}");
        Assert.True(fresh.Alive > tired.Alive * 2, "reserves should be worth committing");
    }

    // -------------------------------------------------------- combined arms

    [Fact]
    public void SpearsDestroyCavalryFromTheFront()
    {
        var (state, horse, spears) = BattleFixtures.Duel("rome_equites", "greece_hoplites");
        BattleFixtures.Engage(state, horse, spears, FixVec2.North, FixVec2.North, Fix.FromInt(40));

        new BattleSim(state).RunSeconds(Fix.FromInt(60));

        _out.WriteLine($"cavalry {horse.Alive}/{horse.Strength}, hoplites {spears.Alive}/{spears.Strength}");
        Assert.True(spears.StrengthFraction > Fix.Ratio(85, 100),
            "a spear wall should barely notice a frontal cavalry charge");
        Assert.True(horse.StrengthFraction < Fix.Half,
            "and the cavalry should be wrecked by it");
    }

    [Fact]
    public void CavalryDestroysSkirmishersCaughtInTheOpen()
    {
        var (state, horse, skirmishers) = BattleFixtures.Duel("rome_equites", "greece_peltasts");
        BattleFixtures.Engage(state, horse, skirmishers, FixVec2.North, FixVec2.North, Fix.FromInt(40));

        new BattleSim(state).RunSeconds(Fix.FromInt(60));

        _out.WriteLine($"cavalry {horse.Alive}/{horse.Strength}, peltasts {skirmishers.Alive}/{skirmishers.Strength}");
        Assert.True(horse.Alive > skirmishers.Alive,
            "skirmishers caught by cavalry in the open should lose badly");
    }

    [Fact]
    public void APhalanxNegatesAFrontalCharge()
    {
        // Tested at the rule rather than through a battle outcome. Cavalry that charges
        // hoplites head-on is annihilated in either formation without landing a blow, so
        // "the phalanx took fewer casualties" compares zero against zero and asserts
        // nothing at all. The claim being made here is specific — a charge bonus is
        // worth nothing against levelled pikes from the front — so measure exactly that.
        var (state, horse, spears) = BattleFixtures.Duel(
            "rome_equites", "greece_hoplites", rightFormation: FormationType.Line);

        // Engage places the attacker due north of a north-facing defender, so this is a
        // frontal attack by construction.
        BattleFixtures.Engage(state, horse, spears, FixVec2.North, FixVec2.North, Fix.FromInt(6));

        int rider = horse.FirstSoldier;
        int hoplite = spears.FirstSoldier;

        Fix Chance() => MeleeSystem.HitChance(state, rider, hoplite, horse, spears);

        state.ChargeTicks[rider] = 0;
        Fix restingVsLine = Chance();

        state.ChargeTicks[rider] = SimConstants.Ticks(SimConstants.ChargeDecaySeconds);
        Fix chargingVsLine = Chance();

        spears.Formation = FormationType.Phalanx;
        Fix chargingVsPhalanx = Chance();

        state.ChargeTicks[rider] = 0;
        Fix restingVsPhalanx = Chance();

        _out.WriteLine($"vs line:    resting {restingVsLine}, charging {chargingVsLine}");
        _out.WriteLine($"vs phalanx: resting {restingVsPhalanx}, charging {chargingVsPhalanx}");

        Assert.True(chargingVsLine > restingVsLine,
            "a charge should be worth something against an ordinary line");
        Assert.Equal(restingVsPhalanx, chargingVsPhalanx);
        Assert.True(restingVsPhalanx < restingVsLine,
            "and a phalanx should be harder to hurt even standing still");
    }

    [Fact]
    public void ElephantsTrampleRatherThanDuel()
    {
        var (state, elephants, legionaries) = BattleFixtures.Duel("carthage_elephants", "rome_hastati");
        BattleFixtures.Engage(state, elephants, legionaries, FixVec2.North, FixVec2.North, Fix.FromInt(40));

        // Measured while the elephants are still fighting. Twelve animals against a
        // hundred and twenty legionaries are hopelessly outnumbered and will break
        // before long — correctly — and a longer window ends up measuring how fast they
        // rout rather than how hard they hit.
        var sim = new BattleSim(state);
        int killed = 0;
        while (!sim.IsOver && elephants.MoraleState is MoraleState.Steady or MoraleState.Wavering
               && state.Tick < SimConstants.TickRate * 60)
        {
            sim.Tick();
            killed = legionaries.Strength - legionaries.Alive;
        }
        _out.WriteLine($"{elephants.Strength} elephants killed {killed} legionaries");

        // Twelve animals should be worth considerably more than twelve men. This is a
        // deliberately loose floor — the exact number moves with every combat tuning
        // pass, and pinning it precisely would make the test a tripwire rather than a
        // statement about elephants.
        Assert.True(killed > elephants.Strength,
            $"{elephants.Strength} elephants managed only {killed} kills — trampling is not working");
    }

    // ------------------------------------------------------------- missiles

    [Fact]
    public void ArmourMattersAgainstArrowsAndSlingsPierceIt()
    {
        int Killed(string shooter, string target)
        {
            var (state, shooterUnit, targetUnit) = BattleFixtures.Duel(shooter, target);

            FixVec2 centre = new(state.Terrain.Size / 2, state.Terrain.Size / 2);
            state.Reposition(targetUnit, centre, -FixVec2.North);
            state.Reposition(shooterUnit, centre - FixVec2.North * Fix.FromInt(80), FixVec2.North);
            shooterUnit.Order = UnitOrder.Hold();
            targetUnit.Order = UnitOrder.Hold();
            state.RebuildSpatialIndices();

            new BattleSim(state).RunSeconds(Fix.FromInt(30));
            return targetUnit.Strength - targetUnit.Alive;
        }

        int arrowsIntoMail = Killed("rome_archers", "rome_principes");
        int arrowsIntoFlesh = Killed("rome_archers", "gaul_warband");
        int slingsIntoMail = Killed("carthage_balearic_slingers", "rome_principes");

        _out.WriteLine($"arrows vs mail {arrowsIntoMail}, arrows vs unarmoured {arrowsIntoFlesh}, " +
                       $"slings vs mail {slingsIntoMail}");

        Assert.True(arrowsIntoFlesh > arrowsIntoMail * 2, "armour should turn arrows");
        Assert.True(slingsIntoMail > arrowsIntoMail, "lead shot should get through mail where arrows do not");
    }

    [Fact]
    public void TestudoAndLooseOrderBothBlunkMissileFire()
    {
        int Killed(FormationType formation, string target)
        {
            var (state, archers, targetUnit) = BattleFixtures.Duel(
                "rome_archers", target, rightFormation: formation);

            FixVec2 centre = new(state.Terrain.Size / 2, state.Terrain.Size / 2);
            state.Reposition(targetUnit, centre, -FixVec2.North);
            state.Reposition(archers, centre - FixVec2.North * Fix.FromInt(80), FixVec2.North);
            archers.Order = UnitOrder.Hold();
            targetUnit.Order = UnitOrder.Hold();
            state.RebuildSpatialIndices();

            new BattleSim(state).RunSeconds(Fix.FromInt(30));
            return targetUnit.Strength - targetUnit.Alive;
        }

        int inLine = Killed(FormationType.Line, "rome_hastati");
        int inTestudo = Killed(FormationType.Testudo, "rome_hastati");

        _out.WriteLine($"line took {inLine}, testudo took {inTestudo}");
        Assert.True(inTestudo * 2 < inLine, "locked shields should shrug most of a volley off");
    }

    // --------------------------------------------------------------- morale

    [Fact]
    public void UnitsBreakInsteadOfFightingToTheLastMan()
    {
        // The single most important behaviour in the genre. Almost every casualty in an
        // ancient battle came after one side broke.
        var sim = BattleSim.Create(BattleFixtures.RomeVersusCarthage(seed: 77));
        sim.Run(SimConstants.TickRate * 60 * 30);

        var broken = sim.State.Units
            .Where(u => u.MoraleState == MoraleState.Routing || u.Withdrawn)
            .ToList();

        Assert.NotEmpty(broken);

        foreach (Unit unit in sim.State.Units)
        {
            // Nothing should have been annihilated while still notionally steady.
            if (unit.MoraleState == MoraleState.Steady)
                Assert.True(unit.Alive > 0, $"{unit.Type.Name} was wiped out without ever losing heart");
        }

        _out.WriteLine($"{broken.Count} of {sim.State.Units.Length} units broke or fled");
    }

    [Fact]
    public void ARoutingUnitLeftAloneCanRally()
    {
        var (state, _, victim) = BattleFixtures.Duel("rome_principes", "carthage_libyan_spearmen");

        // Break it outright, then leave it entirely alone. Setting morale to zero is not
        // enough on its own: with nothing attacking it, morale climbs straight back to
        // its target long before the break threshold is confirmed, and the unit never
        // routs at all.
        FixVec2 centre = new(state.Terrain.Size / 2, state.Terrain.Size / 2);
        state.Reposition(victim, centre, FixVec2.North);
        victim.Order = UnitOrder.Hold();
        MoraleSystem.Break(state, victim);
        Assert.Equal(MoraleState.Routing, victim.MoraleState);

        var sim = new BattleSim(state);
        int rallied = BattleFixtures.RunUntil(
            sim, () => victim.MoraleState == MoraleState.Rallying, maxSeconds: 120);

        _out.WriteLine(rallied > 0
            ? $"rallied after {rallied / SimConstants.TickRate}s"
            : $"never rallied (state {victim.MoraleState})");

        Assert.True(rallied > 0, "a broken unit that gets clear should be able to re-form");
    }

    [Fact]
    public void RoutersThatLeaveTheFieldAreGoneForGood()
    {
        var sim = BattleSim.Create(BattleFixtures.RomeVersusCarthage(seed: 4471));
        sim.Run(SimConstants.TickRate * 60 * 30);

        // At least somebody should have quit the field entirely over a whole battle,
        // and anything that did must stop being simulated.
        foreach (Unit unit in sim.State.Units.Where(u => u.Withdrawn))
        {
            Assert.False(unit.IsEffective);
            Assert.Equal(0, unit.Engaged);
        }
    }

    [Fact]
    public void LosingTheGeneralShakesTheWholeArmy()
    {
        var sim = BattleSim.Create(BattleFixtures.RomeVersusCarthage(seed: 9));
        sim.Run(SimConstants.TickRate * 20);

        Army army = sim.State.Armies[0];
        Unit line = sim.State.Units[army.UnitIds[1]];
        Fix before = line.Morale;

        // Kill the bodyguard outright and let morale settle.
        Unit general = sim.State.Units[army.GeneralUnit];
        for (int s = general.FirstSoldier; s < general.EndSoldier; s++)
            sim.State.KillSoldier(s, -1);
        army.GeneralDead = true;

        sim.Run(SimConstants.TickRate * 10);

        _out.WriteLine($"morale {before.ToDouble():F1} -> {line.Morale.ToDouble():F1}");
        Assert.True(line.Morale < before, "the army should feel its commander going down");
    }

    // ------------------------------------------------------------------ scale

    [Fact]
    public void FullScaleBattlesRunAtTargetSize()
    {
        var sim = BattleSim.Create(BattleFixtures.GrandBattle(seed: 5));

        _out.WriteLine($"{sim.State.SoldierCount} soldiers in {sim.State.Units.Length} units");
        Assert.True(sim.State.SoldierCount >= 2000,
            $"only {sim.State.SoldierCount} soldiers — the scale target is around 2400");

        // Warm up, then time a stretch of the busiest part of the battle.
        sim.Run(SimConstants.TickRate * 15);

        var clock = Stopwatch.StartNew();
        sim.Run(SimConstants.TickRate * 20);
        clock.Stop();

        double msPerTick = clock.Elapsed.TotalMilliseconds / (SimConstants.TickRate * 20);
        _out.WriteLine($"{msPerTick:F2} ms per tick (Debug build, single-threaded, under test-suite load)");

        // Deliberately a very loose tripwire rather than a real performance bound.
        //
        // xUnit runs test classes in parallel, so this measurement shares the machine
        // with a dozen other battles and is worth roughly nothing as an absolute number
        // — a tight threshold here fails at random, and a test that fails at random gets
        // ignored, which is worse than not having it. The figure is printed because it
        // is useful to see; the assertion only catches something having gone
        // catastrophically wrong, like an accidental O(n²) sweep over every soldier pair.
        //
        // For a real measurement run: dotnet run -c Release --project tools/War.Watch -- --fast
        Assert.True(msPerTick < 150, $"a tick took {msPerTick:F1} ms — something is very wrong");
    }

    [Fact]
    public void NoSoldierEverLeavesTheWorldExceptByRouting()
    {
        var sim = BattleSim.Create(BattleFixtures.RomeVersusCarthage(seed: 12));

        for (int i = 0; i < 1500 && !sim.IsOver; i++)
        {
            sim.Tick();
            sim.State.DrainEvents();

            if (i % 100 != 0) continue;

            for (int s = 0; s < sim.State.SoldierCount; s++)
            {
                if (sim.State.State[s] is SoldierState.Dead or SoldierState.Routing) continue;

                FixVec2 at = sim.State.Position[s];
                Assert.True(sim.State.Terrain.InBounds(at), $"soldier {s} left the battlefield at {at}");
            }
        }
    }

    [Fact]
    public void UnitCentresStayWithTheirMen()
    {
        // Regression guard for the Q16.16 overflow that put a 140-man unit six hundred
        // metres off the map with no error of any kind.
        var sim = BattleSim.Create(BattleFixtures.GrandBattle(seed: 3));
        sim.Run(SimConstants.TickRate * 30);

        foreach (Unit unit in sim.State.Units)
        {
            if (unit.IsOutOfAction || unit.Alive == 0) continue;

            int sample = -1;
            for (int s = unit.FirstSoldier; s < unit.EndSoldier; s++)
                if (sim.State.State[s] != SoldierState.Dead) { sample = s; break; }

            Assert.True(sample >= 0);
            Fix gap = FixVec2.Distance(unit.Centre, sim.State.Position[sample]);
            Assert.True(gap < Fix.FromInt(80),
                $"{unit.Type.Name} centre is {gap} from one of its own men");
        }
    }
}
