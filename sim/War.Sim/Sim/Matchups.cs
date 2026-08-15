using War.Sim.Units;

namespace War.Sim.Sim;

/// <summary>
/// The canonical army lists.
///
/// These lived inline in two places — the game and the terminal watcher — which meant the
/// thing being balanced and the thing being played were only the same army by hand. They
/// are here so they cannot drift, and so <see cref="Cost"/> can be checked against them.
///
/// The lists are matched on the roster's own points. That sounds obvious and was not being
/// done: Carthage fielded 5110 points against Rome's 4210, a fifth of an army, and every
/// measurement taken against that matchup was measuring the gap rather than whatever was
/// being tuned. Several rounds of commander tuning were spent trying to make a general
/// overcome it before anyone added the two columns up.
/// </summary>
public static class Matchups
{
    /// <summary>
    /// Rome against Carthage on open ground — the standard test and the game's skirmish.
    ///
    /// A manipular legion: skirmishers out front, three lines of foot behind, horse on both
    /// wings. Against it, a Carthaginian army built the other way round — a small, superb
    /// core of Sacred Band, cheap Iberian and Libyan foot to hold the line, and the
    /// expensive arms that actually win it out on the flanks.
    ///
    /// They come to 5090 and 5110, which is as close as whole units get.
    /// </summary>
    public static ArmyBlueprint Rome(bool isPlayer = false) => new()
    {
        Faction = Faction.Rome,
        Name = "Rome",
        IsPlayer = isPlayer,
        Units =
        [
            new UnitBlueprint { TypeId = "rome_velites" },
            new UnitBlueprint { TypeId = "rome_hastati" },
            new UnitBlueprint { TypeId = "rome_hastati" },
            new UnitBlueprint { TypeId = "rome_hastati" },
            new UnitBlueprint { TypeId = "rome_principes" },
            new UnitBlueprint { TypeId = "rome_principes" },
            new UnitBlueprint { TypeId = "rome_triarii" },

            // Horse on both wings, which is how a legion was actually drawn up and also
            // the only tool Rome has for getting round the side of a spear line. With one
            // wing it had no answer to a formed front at all.
            new UnitBlueprint { TypeId = "rome_equites" },
            new UnitBlueprint { TypeId = "rome_equites" },
            new UnitBlueprint { TypeId = "rome_general" },
        ],
    };

    public static ArmyBlueprint Carthage(bool isPlayer = false) => new()
    {
        Faction = Faction.Carthage,
        Name = "Carthage",
        IsPlayer = isPlayer,
        Units =
        [
            new UnitBlueprint { TypeId = "carthage_balearic_slingers" },
            new UnitBlueprint { TypeId = "carthage_libyan_spearmen" },
            new UnitBlueprint { TypeId = "carthage_libyan_spearmen" },
            new UnitBlueprint { TypeId = "carthage_sacred_band" },
            new UnitBlueprint { TypeId = "carthage_iberian" },
            new UnitBlueprint { TypeId = "carthage_elephants" },
            new UnitBlueprint { TypeId = "carthage_sacred_band_cavalry" },
            new UnitBlueprint { TypeId = "carthage_general" },
        ],
    };

    /// <summary>
    /// What an army list is worth, in roster points.
    ///
    /// Exists so a test can hold the two lists to each other. A points gap does not show up
    /// as anything recognisable in a battle — it shows up as the losing side's commander
    /// looking stupid, which is indistinguishable from a commander that is stupid.
    /// </summary>
    public static int Cost(ArmyBlueprint army)
    {
        int total = 0;

        foreach (UnitBlueprint unit in army.Units)
        {
            UnitType type = Roster.Get(unit.TypeId);
            int strength = unit.Strength > 0 ? unit.Strength : type.DefaultStrength;

            // Priced per man, so a half-strength unit costs half. Nothing does that today,
            // but a campaign fields under-strength units constantly.
            total += type.Cost * strength / type.DefaultStrength;
        }

        return total;
    }
}
