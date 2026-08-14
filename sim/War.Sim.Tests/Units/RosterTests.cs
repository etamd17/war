using War.Sim.Core;
using War.Sim.Units;
using Xunit;

namespace War.Sim.Tests.Units;

/// <summary>
/// These are design invariants, not implementation details. A stat table is easy to
/// break silently — one mistyped field and spearmen quietly stop countering cavalry,
/// and nobody notices until the game feels wrong for reasons nobody can name.
/// </summary>
public class RosterTests
{
    [Fact]
    public void EveryFactionHasAFullArmy()
    {
        foreach (Faction faction in Enum.GetValues<Faction>())
        {
            var units = Roster.ByFaction(faction).ToList();
            Assert.True(units.Count >= 7, $"{faction} has only {units.Count} unit types");

            Assert.Contains(units, u => u.Class == UnitClass.General);
            Assert.Contains(units, u => u.Class is UnitClass.Infantry or UnitClass.Spear);
            Assert.Contains(units, u => u.Class == UnitClass.Missile);
            Assert.Contains(units, u => u.Class is UnitClass.Cavalry or UnitClass.MissileCavalry
                or UnitClass.Chariot or UnitClass.Elephant);
        }
    }

    [Fact]
    public void UnitIdsAreUniqueAndResolvable()
    {
        var ids = Roster.All.Select(u => u.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());

        foreach (UnitType type in Roster.All)
            Assert.Same(type, Roster.Get(type.Id));
    }

    [Fact]
    public void EveryUnitCanActuallyStandInItsDefaultFormation()
    {
        foreach (UnitType type in Roster.All)
            Assert.True(type.CanUse(type.DefaultFormation),
                $"{type.Name} defaults to {type.DefaultFormation}, which it is not allowed to form");
    }

    [Fact]
    public void SpearsAndPikesCounterMounts()
    {
        // The single most important line in the counter web. If this breaks, cavalry
        // becomes an autowin button.
        foreach (UnitType type in Roster.All.Where(u => u.Class is UnitClass.Spear or UnitClass.Pike))
            Assert.True(type.BonusVsMounted >= 5,
                $"{type.Name} is a spear unit with only +{type.BonusVsMounted} against mounts");
    }

    [Fact]
    public void CavalryChargesHarderThanInfantry()
    {
        Fix worstCavalryCharge = Fix.FromInt(Roster.All
            .Where(u => u.Class is UnitClass.Cavalry or UnitClass.Chariot)
            .Min(u => u.Charge));

        // Naked Fanatics are the deliberate exception — a charge of 12 is the whole
        // point of them — so compare against the ordinary foot.
        Fix bestOrdinaryFootCharge = Fix.FromInt(Roster.All
            .Where(u => u.Class is UnitClass.Infantry or UnitClass.Spear && !u.CausesFear)
            .Max(u => u.Charge));

        Assert.True(worstCavalryCharge >= bestOrdinaryFootCharge,
            "even poor cavalry should hit harder on the charge than good infantry");
    }

    [Fact]
    public void MountedUnitsAreFasterAndTurnWorseThanFoot()
    {
        foreach (UnitType mounted in Roster.All.Where(u => u.IsMounted && u.Class != UnitClass.Elephant))
        {
            Assert.True(mounted.RunSpeed > Fix.FromInt(5), $"{mounted.Name} is slow for a mount");
            Assert.True(mounted.TurnRate < Fix.FromInt(2), $"{mounted.Name} pivots like infantry");
            Assert.True(mounted.Mass > Fix.Two, $"{mounted.Name} weighs too little to shove a man aside");
        }
    }

    [Fact]
    public void MissileStatsAreInternallyConsistent()
    {
        foreach (UnitType type in Roster.All)
        {
            if (type.Missile == MissileType.None)
            {
                Assert.Equal(0, type.Ammunition);
                Assert.False(type.HasMissiles);
                continue;
            }

            Assert.True(type.Ammunition > 0, $"{type.Name} carries a {type.Missile} and no ammunition");
            Assert.True(type.MissileRange > Fix.Zero, $"{type.Name} has no missile range");
            Assert.True(type.MissileAttack > 0, $"{type.Name} does no missile damage");
            Assert.True(type.HasMissiles);
        }
    }

    [Fact]
    public void ThrownWeaponsAreShortRangedAndBowsAreNot()
    {
        foreach (UnitType type in Roster.All.Where(u => u.Missile is MissileType.Javelin or MissileType.Pilum))
            Assert.True(type.MissileRange < Fix.FromInt(40),
                $"{type.Name} throws a {type.Missile} {type.MissileRange} metres");

        foreach (UnitType type in Roster.All.Where(u => u.Missile is MissileType.Bow or MissileType.Sling))
            Assert.True(type.MissileRange > Fix.FromInt(100),
                $"{type.Name} only shoots {type.MissileRange} metres");

        // The pilum is a single volley before contact, not a missile duel.
        foreach (UnitType type in Roster.All.Where(u => u.Missile == MissileType.Pilum))
            Assert.True(type.Ammunition <= 2, $"{type.Name} carries {type.Ammunition} pila");
    }

    [Fact]
    public void SlingsPierceArmourAndBowsDoNot()
    {
        // This is why Balearic Slingers are the answer to a Roman line: mail does not
        // help against a lead shot.
        foreach (UnitType type in Roster.All.Where(u => u.Missile == MissileType.Sling))
            Assert.True(type.ArmourPiercing > Fix.Zero, $"{type.Name} slings should pierce armour");

        foreach (UnitType type in Roster.All.Where(u => u.Missile == MissileType.Bow))
            Assert.Equal(Fix.Zero, type.ArmourPiercing);
    }

    [Fact]
    public void ElephantsAndChariotsCauseFear()
    {
        foreach (UnitType type in Roster.All.Where(u => u.Class is UnitClass.Elephant or UnitClass.Chariot))
            Assert.True(type.CausesFear, $"{type.Name} should terrify the men in front of it");

        // And elephants are not afraid of anything, including other elephants.
        foreach (UnitType type in Roster.All.Where(u => u.Class == UnitClass.Elephant))
        {
            Assert.True(type.ImmuneToFear);
            Assert.True(type.Hitpoints > 1, "an elephant should not die to one spear thrust");
        }
    }

    [Fact]
    public void GeneralsAreAmongTheSteadiestTroopsTheyCommand()
    {
        // Deliberately "among the steadiest" rather than "the steadiest". Spartan
        // Hoplites and Naked Fanatics both out-morale their own general, and that is
        // the point of both units — not breaking is the whole Spartan identity, and
        // the Fanatics are fearless precisely because they are about to die. A general
        // who topped every chart would flatten that.
        foreach (Faction faction in Enum.GetValues<Faction>())
        {
            UnitType general = Roster.GeneralOf(faction);
            int best = Roster.ByFaction(faction).Max(u => u.Morale);

            Assert.True(general.Morale >= 15, $"the {faction} general is not steady enough to steady anyone");
            Assert.True(general.Morale >= best - 2,
                $"the {faction} general trails the army's best morale by {best - general.Morale}");
            Assert.True(general.DefaultStrength <= 30, "a bodyguard is a handful of men, not a regiment");
        }
    }

    [Fact]
    public void ArmourAndSpeedTradeOffAgainstEachOther()
    {
        // Heavy foot should not also be the fastest foot. Catches a mistyped speed.
        var foot = Roster.All.Where(u => !u.IsMounted && u.Class != UnitClass.Missile).ToList();

        UnitType heaviest = foot.OrderByDescending(u => u.Armour).First();
        UnitType fastest = foot.OrderByDescending(u => u.RunSpeed).First();

        Assert.NotSame(heaviest, fastest);
        Assert.True(heaviest.RunSpeed < fastest.RunSpeed);
    }

    [Fact]
    public void TurnStepIsAUsableRotation()
    {
        // Cached once per type and handed straight to TurnTowards, so it must be a
        // unit vector or facings would slowly grow or shrink.
        foreach (UnitType type in Roster.All)
        {
            double length = type.TurnStepPerTick.Magnitude.ToDouble();
            Assert.True(Math.Abs(length - 1.0) < 0.01, $"{type.Name} turn step has length {length}");

            // And it must be a small forward-ish step, not a wild swing per tick.
            Assert.True(type.TurnStepPerTick.X > Fix.Ratio(9, 10),
                $"{type.Name} turns more than 25 degrees in a single tick");
        }
    }

    [Fact]
    public void CostsTrackCapability()
    {
        // A rough sanity check that the roster is not accidentally offering elite units
        // at levy prices, which would make the campaign trivial later.
        UnitType spartans = Roster.Get("greece_spartans");
        UnitType militia = Roster.Get("greece_militia_hoplites");

        Assert.True(spartans.Cost > militia.Cost * 2);
        Assert.True(spartans.Attack > militia.Attack);
        Assert.True(spartans.Morale > militia.Morale);
    }

    [Fact]
    public void EveryUnitHasSaneCoreStats()
    {
        foreach (UnitType type in Roster.All)
        {
            Assert.InRange(type.Attack, 3, 20);
            Assert.InRange(type.DefenceSkill, 2, 15);
            Assert.InRange(type.Shield, 0, 8);
            Assert.InRange(type.Armour, 0, 12);
            Assert.InRange(type.Morale, 4, 18);
            Assert.InRange(type.Discipline, 0, 10);
            Assert.InRange(type.DefaultStrength, 8, 200);
            Assert.True(type.RunSpeed > type.WalkSpeed, $"{type.Name} runs no faster than it walks");
            Assert.True(type.Radius > Fix.Zero);
            Assert.True(type.Mass > Fix.Zero);
            Assert.True(type.AttackInterval > Fix.Zero);
            Assert.False(string.IsNullOrWhiteSpace(type.Description), $"{type.Name} has no description");
        }
    }
}
