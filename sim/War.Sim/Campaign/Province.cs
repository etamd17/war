using War.Sim.Core;
using War.Sim.Units;
using War.Sim.World;

namespace War.Sim.Campaign;

/// <summary>How a province pays, and what its ground looks like when a battle happens on it.</summary>
public enum Landscape : byte
{
    /// <summary>Open farmland. Good money, and nowhere to hide an army.</summary>
    Farmland = 0,

    /// <summary>Broken upland. Poor, and the defender picks the hill.</summary>
    Hills = 1,

    /// <summary>Forest and bog. Poor, and cavalry is worth very little in it.</summary>
    Forest = 2,

    /// <summary>Desert and steppe. Poorest, wide open, brutal on infantry.</summary>
    Desert = 3,

    /// <summary>A port and the trade that comes with it. Rich out of proportion to its size.</summary>
    Coastal = 4,
}

/// <summary>
/// One province.
///
/// A campaign map is mostly a graph with money on it, and the parts that matter are which
/// provinces touch which — that is the whole of campaign manoeuvre — and what each one is
/// worth, which is the whole of campaign economy.
///
/// <see cref="Landscape"/> does double duty. It sets the income, and it decides the ground
/// a battle is fought on when two armies meet here: a fight in Celtica generates forest,
/// a fight in Thebais generates desert. That connection is the point of having a campaign
/// at all — where you choose to give battle is a decision made turns before the battle.
/// </summary>
public sealed class Province
{
    public required int Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Position on the map, for drawing and for the AI's sense of distance.</summary>
    public required FixVec2 Position { get; init; }

    public required Landscape Landscape { get; init; }

    /// <summary>Base income per turn before any development.</summary>
    public required int Wealth { get; init; }

    /// <summary>Provinces reachable in one move. Sea crossings count; this is the Mediterranean.</summary>
    public int[] Neighbours { get; set; } = [];

    /// <summary>Who holds it. Null means nobody — independent, and fair game for everyone.</summary>
    public Faction? Owner { get; set; }

    /// <summary>
    /// Turns of occupation still owed before the province pays its holder properly.
    ///
    /// A province taken this turn is a province in revolt, not an asset. Without this,
    /// a blitz across four provinces funds the next four, and the campaign becomes a
    /// race that the first mover always wins.
    /// </summary>
    public int Unrest { get; set; }

    /// <summary>What this province actually pays its owner this turn.</summary>
    public int Income => Unrest > 0 ? Wealth / 4 : Wealth;

    /// <summary>
    /// Men of the local levy still under arms.
    ///
    /// Every province defends itself a little, and without that the campaign is musical
    /// chairs: with nothing but field armies on the map, any province not physically stood
    /// in falls to whoever is adjacent, so provinces changed hands fourteen times in a
    /// single turn and no border existed anywhere for more than one move.
    ///
    /// The levy is poor troops and few of them — it will not stop an army, and it is not
    /// meant to. It is meant to make taking ground cost a turn and a real force, which is
    /// what turns a map of wandering stacks into a map with fronts on it.
    /// </summary>
    public int Militia { get; set; }

    /// <summary>Men the province can raise and keep. Richer ground supports more of them.</summary>
    public int MilitiaCap => Wealth / 7;

    /// <summary>Turns an enemy has been sitting outside, and who is doing the sitting.</summary>
    public int Siege { get; set; }
    public Faction? Besieger { get; set; }

    /// <summary>
    /// Turns of investment before a province falls.
    ///
    /// This is the campaign's clock. Without it an army takes a province every turn it
    /// moves, so a single full stack sweeps the map at one province per turn and the whole
    /// Mediterranean was decided in twenty-four — the winner simply being whoever had the
    /// most adjacency to weakness at the start.
    ///
    /// Richer ground holds out longer, because rich ground means walls. The point is not
    /// the delay itself but what the delay creates: time for the owner to march a relief
    /// army, which is the only thing that makes holding a border a decision rather than an
    /// accounting identity.
    /// </summary>
    public int SiegeLength => 2 + Wealth / 220;

    /// <summary>Called whenever nobody hostile is standing here, so a lifted siege resets.</summary>
    public void LiftSiege()
    {
        Siege = 0;
        Besieger = null;
    }

    /// <summary>
    /// Rebuilds the levy a little each turn.
    ///
    /// Not while the province is in unrest — a province taken last turn does not raise men
    /// for the power that just took it — and emphatically not while it is under siege.
    ///
    /// That second condition was missing, and the consequence was total. The levy has to
    /// reach zero before a province can fall, and it was topping itself back up at the end
    /// of every turn including the turns it was being besieged, so it converged to a
    /// comfortable number and stopped there. Not one province changed hands in a hundred
    /// and fifty turns. Every power ended holding exactly what it started with, the map
    /// looked stable and considered, and the log was an unbroken column of sieges being
    /// laid against towns that could not be taken by anyone, ever.
    /// </summary>
    public void RaiseLevy()
    {
        if (Unrest > 0 || Besieger != null) return;
        Militia = Math.Min(MilitiaCap, Militia + Math.Max(2, MilitiaCap / 5));
    }

    /// <summary>
    /// The battlefield this province generates.
    ///
    /// Seeded from the province and the turn, so the same fight on the same ground is
    /// reproducible — and so a battle in the hills is genuinely hilly rather than
    /// nominally so.
    /// </summary>
    public BattlefieldSettings Battlefield(uint seed) => Landscape switch
    {
        Landscape.Hills => new BattlefieldSettings
        {
            Seed = seed,
            Hilliness = Fix.Ratio(18, 10),
            ForestCoverage = Fix.Ratio(10, 100),
            CentralRidge = true,
        },
        Landscape.Forest => new BattlefieldSettings
        {
            Seed = seed,
            Hilliness = Fix.Ratio(7, 10),
            ForestCoverage = Fix.Ratio(42, 100),
            CentralRidge = false,
        },
        Landscape.Desert => new BattlefieldSettings
        {
            Seed = seed,
            Hilliness = Fix.Ratio(4, 10),
            ForestCoverage = Fix.Zero,
            CentralRidge = false,
        },
        Landscape.Coastal => new BattlefieldSettings
        {
            Seed = seed,
            Hilliness = Fix.Ratio(6, 10),
            ForestCoverage = Fix.Ratio(12, 100),
            CentralRidge = false,
        },
        _ => new BattlefieldSettings
        {
            Seed = seed,
            Hilliness = Fix.Ratio(9, 10),
            ForestCoverage = Fix.Ratio(14, 100),
            CentralRidge = true,
        },
    };

    public override string ToString() => $"{Name} ({Owner?.ToString() ?? "independent"})";
}
