using System.Text;
using War.Sim.Campaign;
using War.Sim.Units;

namespace War.Watch;

/// <summary>
/// Runs a campaign in the terminal and draws the map as it goes.
///
/// Same reason the battle watcher exists: a layer you cannot see is a layer you cannot
/// debug. A campaign that only reports who won at turn eighty is untestable in practice —
/// what is wanted, every single time something looks wrong, is the map on the turn it
/// started looking wrong and the list of what each power actually did.
/// </summary>
public static class CampaignView
{
    private const int Width = 74;
    private const int Height = 26;

    public static int Run(uint seed, int turns, bool colour)
    {
        CampaignState state = CampaignBuilder.Build(new CampaignSetup { Seed = seed });

        Console.WriteLine($"The Mediterranean, {state.Date} — {turns} turns");
        Console.WriteLine();

        int lastChronicle = 0;
        Faction? victor = null;

        for (int i = 0; i < turns; i++)
        {
            CampaignSim.EndTurn(state);

            victor = CampaignSim.Victor(state);
            if (victor != null) break;

            lastChronicle = state.Chronicle.Count;
        }

        Console.WriteLine(Draw(state, colour));
        Console.WriteLine();
        Console.WriteLine(Standings(state, colour));
        Console.WriteLine();
        Console.WriteLine(Veterans(state, colour));
        Console.WriteLine();

        Console.WriteLine("  the last of it");
        foreach (string line in state.Chronicle.Skip(Math.Max(0, lastChronicle - 14)).TakeLast(18))
            Console.WriteLine($"    {line}");

        Console.WriteLine();
        Console.WriteLine(victor is { } winner
            ? $"  {state.Power(winner).Name} rules the Mediterranean, {state.Date}."
            : $"  No power has mastered the sea by {state.Date}.");

        return 0;
    }

    /// <summary>
    /// The map, drawn by dropping each province onto a character grid at its own position.
    ///
    /// Crude, and good enough to see the shape of a war: Rome's block of Italy going red
    /// across Sicily, or Gaul spilling south. A province is its initial in its owner's
    /// colour, and an army standing on it makes that initial upper case — so a border with
    /// a fleet of capitals along it is a front.
    /// </summary>
    private static string Draw(CampaignState state, bool colour)
    {
        var grid = new char[Height, Width];
        var owner = new Faction?[Height, Width];
        var occupied = new bool[Height, Width];

        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++) grid[y, x] = ' ';

        foreach (Province province in state.Provinces)
        {
            // Map coordinates run 0-64 west to east and 25-80 south to north; the grid is
            // upside down relative to that, as terminals count rows downward.
            int x = Math.Clamp(province.Position.X.RoundToInt * Width / 66, 0, Width - 1);
            int y = Math.Clamp((80 - province.Position.Y.RoundToInt) * Height / 56, 0, Height - 1);

            // Nudge along the row rather than overwrite: two provinces rounding to the
            // same cell would otherwise silently hide one of them, and a province that is
            // not on the map is a province nobody notices is broken.
            while (grid[y, x] != ' ' && x < Width - 1) x++;

            bool hasArmy = state.ArmiesIn(province.Id).Any();
            char initial = province.Name[0];

            grid[y, x] = hasArmy ? char.ToUpperInvariant(initial) : char.ToLowerInvariant(initial);
            owner[y, x] = province.Owner;
            occupied[y, x] = hasArmy;
        }

        var sb = new StringBuilder();
        for (int y = 0; y < Height; y++)
        {
            sb.Append("  ");
            for (int x = 0; x < Width; x++)
            {
                char c = grid[y, x];
                if (c == ' ') { sb.Append(' '); continue; }
                sb.Append(colour ? Paint(c.ToString(), owner[y, x]) : c);
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static string Standings(CampaignState state, bool colour)
    {
        var sb = new StringBuilder();
        sb.AppendLine("  power            provinces   armies      men   treasury");

        foreach (CampaignPower power in state.Powers.Values
                     .OrderByDescending(p => state.ProvinceCount(p.Faction)))
        {
            int armies = 0, men = 0;
            foreach (CampaignArmy army in state.Armies)
            {
                if (army.Owner != power.Faction) continue;
                armies++;
                men += army.Men;
            }

            string name = power.Destroyed ? $"{power.Name} (finished)" : power.Name;
            string row = $"  {name,-18}{state.ProvinceCount(power.Faction),6}{armies,9}{men,9}{power.Treasury,11}";
            sb.AppendLine(colour ? Paint(row, power.Faction) : row);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The most blooded regiments still in the field.
    ///
    /// A campaign is supposed to produce these — a unit that has been kept alive across a
    /// war and is measurably better for it. If this list is empty at turn a hundred, either
    /// nobody is fighting or nobody is surviving, and both are worth knowing.
    /// </summary>
    private static string Veterans(CampaignState state, bool colour)
    {
        // Ranked by chevrons and then by how many men are left, NOT by raw blooding.
        //
        // Sorting by blooding looked reasonable and was a trap: refitting a regiment
        // dilutes its blooding, so the most-blooded list can only ever contain units that
        // never stopped to refit. It showed nothing but nine-chevron husks at eight men
        // apiece and made a healthy campaign look like a broken one — the diagnostic was
        // selecting for the exact pathology it was supposed to detect.
        var best = state.Armies
            .SelectMany(a => a.Regiments.Select(r => (Army: a, Regiment: r)))
            .Where(x => x.Regiment.Experience > 0)
            .OrderByDescending(x => x.Regiment.Experience)
            .ThenByDescending(x => x.Regiment.Strength)
            .ThenBy(x => x.Regiment.TypeId, StringComparer.Ordinal)
            .Take(6)
            .ToList();

        var sb = new StringBuilder();

        // The unbiased number: how much of its establishment the whole map still has under
        // arms. If armies are decaying into husks, this is where it shows.
        int men = 0, establishment = 0;
        foreach (CampaignArmy army in state.Armies)
            foreach (Regiment regiment in army.Regiments)
            {
                men += regiment.Strength;
                establishment += regiment.Establishment;
            }

        sb.AppendLine($"  armies are at {(establishment == 0 ? 0 : men * 100 / establishment)}% " +
                      "of establishment");
        sb.AppendLine();

        if (best.Count == 0) return sb.Append("  no veteran regiments in the field").ToString();

        sb.AppendLine("  veterans");

        foreach ((CampaignArmy army, Regiment regiment) in best)
        {
            string row = $"    {army.Owner,-9} {regiment.Type.Name,-24} " +
                         $"{regiment.Strength,4}/{regiment.Establishment,-4} " +
                         new string('>', regiment.Experience);
            sb.AppendLine(colour ? Paint(row, army.Owner) : row);
        }

        return sb.ToString().TrimEnd();
    }

    private static string Paint(string text, Faction? faction)
    {
        (int r, int g, int b) = faction switch
        {
            Faction.Rome => (210, 70, 60),
            Faction.Carthage => (150, 90, 200),
            Faction.Gaul => (90, 170, 90),
            Faction.Greece => (80, 150, 220),
            Faction.Egypt => (215, 180, 70),
            _ => (120, 120, 120),
        };

        return $"[38;2;{r};{g};{b}m{text}[0m";
    }
}
