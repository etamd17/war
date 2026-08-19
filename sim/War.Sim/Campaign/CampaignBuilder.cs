using War.Sim.Units;

namespace War.Sim.Campaign;

public sealed class CampaignSetup
{
    public uint Seed { get; init; } = 1;

    /// <summary>Which power the player commands, or null for a campaign that runs itself.</summary>
    public Faction? Player { get; init; }

    /// <summary>Starting coin per province held. Enough to be dangerous, not enough to be safe.</summary>
    public int TreasuryPerProvince { get; init; } = 240;
}

/// <summary>
/// Sets the board up.
///
/// Every power starts with one field army in its best province and enough money to be
/// dangerous but not enough to be safe. The armies are drawn from each faction's own
/// roster in the same shape the AI recruits to, so nobody opens with something they could
/// not have built — which matters, because the first thing a balance problem does is hide
/// inside a hand-written starting position.
/// </summary>
public static class CampaignBuilder
{
    public static CampaignState Build(CampaignSetup setup)
    {
        List<Province> provinces = CampaignMap.Create();

        var powers = new Dictionary<Faction, CampaignPower>();
        foreach (Faction faction in Enum.GetValues<Faction>())
        {
            // Coin in hand scales with ground held, because ground held is what has to be
            // garrisoned. A flat purse quietly punished whoever started widest: Carthage
            // opens with seven provinces across three coastlines and one army to cover
            // them, and with everyone starting equally rich it led in none of twelve
            // campaigns while compact powers in defensible corners led in eleven.
            int held = provinces.Count(p => p.Owner == faction);

            powers[faction] = new CampaignPower
            {
                Faction = faction,
                Name = NameOf(faction),
                Treasury = setup.TreasuryPerProvince * Math.Max(1, held),
                IsPlayer = setup.Player == faction,
            };
        }

        var state = new CampaignState
        {
            Provinces = provinces,
            Powers = powers,
            Seed = setup.Seed,
        };

        foreach (Faction faction in Enum.GetValues<Faction>())
        {
            Province? home = state.Held(faction).OrderByDescending(p => p.Wealth).FirstOrDefault();
            if (home == null) continue;

            state.Armies.Add(StartingArmy(state, faction, home.Id));
        }

        // Everyone starts fully levied, so the opening turns are about independent ground
        // and not about whoever happens to be adjacent to the weakest neighbour.
        foreach (Province province in provinces) province.Militia = province.MilitiaCap;

        state.Record("the powers of the Mediterranean take the field");
        return state;
    }

    private static string NameOf(Faction faction) => faction switch
    {
        Faction.Rome => "Rome",
        Faction.Carthage => "Carthage",
        Faction.Gaul => "the Gallic tribes",
        Faction.Greece => "the Greek cities",
        _ => "Egypt",
    };

    /// <summary>
    /// A small combined-arms force: four of the line, one to shoot with, one of horse, and
    /// the general. Roughly a third of what the map will support once the money is coming
    /// in, so the opening turns are about taking independent ground rather than fighting.
    /// </summary>
    private static CampaignArmy StartingArmy(CampaignState state, Faction faction, int provinceId)
    {
        var army = new CampaignArmy
        {
            Id = state.NextArmyId++,
            Owner = faction,
            Province = provinceId,
        };

        void Enlist(UnitType? type, int count = 1)
        {
            if (type == null) return;
            for (int i = 0; i < count; i++)
                army.Regiments.Add(new Regiment { TypeId = type.Id, Strength = type.DefaultStrength });
        }

        UnitType? Cheapest(params UnitClass[] classes) => Roster.ByFaction(faction)
            .Where(u => classes.Contains(u.Class))
            .OrderBy(u => u.Cost)
            .ThenBy(u => u.Id, StringComparer.Ordinal)
            .FirstOrDefault();

        Enlist(Cheapest(UnitClass.Infantry, UnitClass.Spear, UnitClass.Pike), 4);
        Enlist(Cheapest(UnitClass.Missile));
        Enlist(Cheapest(UnitClass.Cavalry, UnitClass.MissileCavalry, UnitClass.Chariot));
        Enlist(Roster.GeneralOf(faction));

        return army;
    }
}
