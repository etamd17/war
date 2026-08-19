using War.Sim.Core;
using War.Sim.Units;

namespace War.Sim.Campaign;

/// <summary>
/// One unit in a campaign army: what it is and how many are left.
///
/// The strength survives battles, so a legion that won at Bruttium arrives at Sicilia at
/// eighty-three men and fights the next battle at eighty-three men. That continuity is
/// most of what makes a campaign feel different from a series of skirmishes — a veteran
/// unit is a resource you spend, not a counter you re-place each match.
/// </summary>
public sealed class Regiment
{
    public required string TypeId { get; init; }
    public required int Strength { get; set; }

    public UnitType Type => Roster.Get(TypeId);

    /// <summary>Full-strength establishment, for reporting how badly mauled this is.</summary>
    public int Establishment => Type.DefaultStrength;

    public bool IsDestroyed => Strength <= 0;

    public override string ToString() => $"{Type.Name} {Strength}/{Establishment}";
}

/// <summary>A body of troops standing in a province.</summary>
public sealed class CampaignArmy
{
    public required int Id { get; init; }
    public required Faction Owner { get; init; }

    /// <summary>Province this army occupies.</summary>
    public required int Province { get; set; }

    public List<Regiment> Regiments { get; } = new();

    /// <summary>Province ordered to move to this turn, or null to stand.</summary>
    public int? Destination { get; set; }

    public int Men
    {
        get
        {
            int total = 0;
            foreach (Regiment regiment in Regiments) total += regiment.Strength;
            return total;
        }
    }

    public bool IsDestroyed => Men <= 0;

    public bool HasGeneral
    {
        get
        {
            foreach (Regiment regiment in Regiments)
                if (regiment.Type.Class == UnitClass.General && regiment.Strength > 0) return true;
            return false;
        }
    }

    /// <summary>Removes anything that has been wiped out. Called after every battle.</summary>
    public void BuryTheDead() => Regiments.RemoveAll(r => r.IsDestroyed);

    public override string ToString() => $"{Owner} army #{Id} ({Men} men)";
}

/// <summary>A power on the map.</summary>
public sealed class CampaignPower
{
    public required Faction Faction { get; init; }
    public required string Name { get; init; }
    public int Treasury { get; set; }
    public bool IsPlayer { get; init; }

    /// <summary>Set once a power holds nothing and fields nothing. It stops being simulated.</summary>
    public bool Destroyed { get; set; }
}

/// <summary>
/// The whole campaign.
///
/// Deliberately the same shape as <see cref="Sim.BattleState"/>: plain data, no engine
/// types, one random source with named streams, and every decision derived from what is
/// in here. A campaign is therefore reproducible from its seed, replayable, and testable
/// without a window — the same three properties that made the battle layer worth building
/// this way, and for the same reasons.
/// </summary>
public sealed class CampaignState
{
    /// <summary>Turns are seasons. Four to a year, which is what the chronicle prints.</summary>
    public int Turn { get; set; }

    public required List<Province> Provinces { get; init; }
    public required Dictionary<Faction, CampaignPower> Powers { get; init; }

    public List<CampaignArmy> Armies { get; } = new();

    public required uint Seed { get; init; }

    /// <summary>
    /// What happened, in order, in plain words.
    ///
    /// A campaign that only reports its end state is untestable and unwatchable. This is
    /// the equivalent of the battle log and earns its keep the same way — the first thing
    /// wanted when a faction behaves oddly is a list of what it actually did.
    /// </summary>
    public List<string> Chronicle { get; } = new();

    public int NextArmyId { get; set; }

    public Province this[int provinceId] => Provinces[provinceId];

    public CampaignPower Power(Faction faction) => Powers[faction];

    /// <summary>The year, counting from the founding of the city. Four turns to a year.</summary>
    public string Date
    {
        get
        {
            int year = 265 - Turn / 4;
            string season = (Turn % 4) switch
            {
                0 => "spring", 1 => "summer", 2 => "autumn", _ => "winter",
            };
            return $"{season} {year} BC";
        }
    }

    public void Record(string line) => Chronicle.Add($"[{Date}] {line}");

    /// <summary>A fresh random source for one subsystem of one turn.</summary>
    public DetRandom Random(RngStream stream, int salt = 0) =>
        new(Seed + (uint)(Turn * 7919) + (uint)(salt * 104729), stream);

    public IEnumerable<CampaignArmy> ArmiesIn(int provinceId)
    {
        foreach (CampaignArmy army in Armies)
            if (army.Province == provinceId && !army.IsDestroyed) yield return army;
    }

    public IEnumerable<Province> Held(Faction faction)
    {
        foreach (Province province in Provinces)
            if (province.Owner == faction) yield return province;
    }

    public int ProvinceCount(Faction faction)
    {
        int count = 0;
        foreach (Province province in Provinces)
            if (province.Owner == faction) count++;
        return count;
    }
}
