using War.Sim.Campaign;
using War.Sim.Core;
using War.Sim.Units;

namespace War.Watch;

/// <summary>
/// Holds the campaign's fast battle model against the tactical engine.
///
/// Most battles in a campaign happen where the player is not, and are estimated rather
/// than fought — the real thing costs tens of seconds each. That estimate is a promise
/// that the map is telling the truth about the war. If it is wrong, armies the player
/// never sees win battles they would have lost, and the campaign is quietly a different
/// game from the one being played on the field.
///
/// So the promise gets measured. This generates matched pairs of armies, resolves each
/// both ways, and reports where the two disagree — which is the only way to tune the model
/// without guessing, and guessing at this sort of thing has already cost one afternoon.
/// </summary>
public static class CalibrationView
{
    public static int Run(uint seed, int battles)
    {
        Console.WriteLine($"Calibrating the campaign battle model over {battles} fights...");
        Console.WriteLine();

        int agreed = 0, decisive = 0, agreedDecisive = 0;
        var random = new DetRandom(seed, RngStream.Campaign);

        for (int i = 0; i < battles; i++)
        {
            CampaignArmy a = RandomArmy(random, out _);
            CampaignArmy b = RandomArmy(random, out _);

            var province = new Province
            {
                Id = 0, Name = "Test", Position = FixVec2.Zero,
                Landscape = Landscape.Farmland, Wealth = 400,
            };

            BattleReport quick = BattleResolver.Estimate(
                Copy(a), Copy(b), province.Landscape, new DetRandom(seed + (uint)i, RngStream.CampaignBattle));

            BattleReport real = BattleResolver.Fight(Copy(a), Copy(b), province, seed + (uint)i);

            // How lopsided the fight actually was, by the engine's own verdict: one side
            // wrecked and the other barely touched. Those are the fights the model has no
            // excuse for calling wrong — nobody can predict a near-run thing, and the
            // tactical layer is built so that those turn on which flank breaks first.
            double attackerLost = real.AttackerLosses / (double)Math.Max(1, a.Men);
            double defenderLost = real.DefenderLosses / (double)Math.Max(1, b.Men);
            bool oneSided = Math.Max(attackerLost, defenderLost)
                         >= Math.Max(0.05, Math.Min(attackerLost, defenderLost)) * 2;

            bool match = quick.Outcome == real.Outcome;
            if (match) agreed++;
            if (oneSided)
            {
                decisive++;
                if (match) agreedDecisive++;
            }

            Console.WriteLine(
                $"  {(match ? "  " : "><")} {Describe(a),-34} vs {Describe(b),-34} " +
                $"engine {real.Outcome,-12} model {quick.Outcome,-12}" +
                $"{(oneSided ? "one-sided" : "")}");
        }

        Console.WriteLine();
        Console.WriteLine($"  agreed on {agreed} of {battles}");
        Console.WriteLine($"  agreed on {agreedDecisive} of {decisive} one-sided fights");
        return 0;
    }

    private static string Describe(CampaignArmy army) =>
        $"{army.Owner} {army.Regiments.Count} regt {army.Men} men";

    private static CampaignArmy Copy(CampaignArmy army)
    {
        var clone = new CampaignArmy { Id = army.Id, Owner = army.Owner, Province = army.Province };
        foreach (Regiment regiment in army.Regiments)
            clone.Regiments.Add(new Regiment { TypeId = regiment.TypeId, Strength = regiment.Strength });
        return clone;
    }

    /// <summary>
    /// A plausible campaign army: three to eight regiments of one faction, sometimes a
    /// general, sometimes below establishment. Not a fair fight — the point is to sample
    /// the space of fights the campaign actually generates, most of which are uneven.
    /// </summary>
    private static CampaignArmy RandomArmy(DetRandom random, out Faction faction)
    {
        var factions = Enum.GetValues<Faction>();
        faction = factions[random.NextInt(factions.Length)];

        var army = new CampaignArmy { Id = 0, Owner = faction, Province = 0 };

        var pool = Roster.ByFaction(faction)
            .Where(u => u.Class != UnitClass.General)
            .OrderBy(u => u.Id, StringComparer.Ordinal)
            .ToList();

        int regiments = random.NextInt(3, 9);
        for (int i = 0; i < regiments; i++)
        {
            UnitType type = pool[random.NextInt(pool.Count)];
            int strength = type.DefaultStrength * random.NextInt(50, 101) / 100;
            army.Regiments.Add(new Regiment { TypeId = type.Id, Strength = Math.Max(4, strength) });
        }

        if (random.Chance(1, 2))
        {
            UnitType general = Roster.GeneralOf(faction);
            army.Regiments.Add(new Regiment { TypeId = general.Id, Strength = general.DefaultStrength });
        }

        return army;
    }
}
