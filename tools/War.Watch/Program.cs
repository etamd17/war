using System.Diagnostics;
using System.Text;
using War.Sim.Core;
using War.Sim.Sim;
using War.Sim.Units;
using War.Sim.World;

namespace War.Watch;

/// <summary>
/// Runs a battle in the terminal.
///
///   dotnet run --project tools/War.Watch                 watch it play out
///   dotnet run --project tools/War.Watch -- --seed 77    a different battle
///   dotnet run --project tools/War.Watch -- --fast       resolve it and print the result
///   dotnet run --project tools/War.Watch -- --sweep 25   run 25 battles and tally the outcomes
///
/// The sweep is the one that earns its keep. Ancient battles turn on morale, which is
/// noisy by design, so a single result tells you very little about whether a change
/// helped. Twenty-five of them tell you a great deal, and they take about a minute.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var options = Options.Parse(args);

        Console.OutputEncoding = Encoding.UTF8;

        if (options.Sweep > 0) return Sweep(options);
        return options.Fast ? Resolve(options) : Watch(options);
    }

    // ------------------------------------------------------------------- modes

    private static int Watch(Options options)
    {
        BattleSim sim = Build(options.Seed, options.Swap);
        var renderer = new BattleRenderer(sim.State, options.Width, options.Height, options.Colour);
        var log = new Queue<string>();

        Console.Clear();
        Console.CursorVisible = false;

        var clock = Stopwatch.StartNew();
        int rendered = -1;

        try
        {
            while (!sim.IsOver)
            {
                // Real time, scaled. The simulation is fixed at 30 Hz whatever we do
                // here, so speeding it up changes only how fast we consume those ticks.
                int wanted = (int)(clock.Elapsed.TotalSeconds * SimConstants.TickRate * options.Speed);
                while (sim.State.Tick < wanted && !sim.IsOver)
                {
                    sim.Tick();
                    Record(sim.State, log);
                    sim.State.DrainEvents();
                }

                int frame = sim.State.Tick / 6;
                if (frame != rendered)
                {
                    rendered = frame;
                    Paint(renderer, sim.State, log);
                }

                Thread.Sleep(16);

                if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape) break;
            }

            Paint(renderer, sim.State, log);
        }
        finally
        {
            Console.CursorVisible = true;
        }

        Summary(sim.State, renderer);
        return 0;
    }

    private static int Resolve(Options options)
    {
        BattleSim sim = Build(options.Seed, options.Swap);
        var renderer = new BattleRenderer(sim.State, options.Width, options.Height, options.Colour);

        LogLimit = int.MaxValue;

        var clock = Stopwatch.StartNew();
        var log = new Queue<string>();

        // Phase timings. "How long is a battle" is the wrong question on its own — an
        // eight-minute battle that is seven minutes of walking and one of fighting is
        // not the same game as one that is two and six, and only the split tells you
        // which knob to turn.
        int firstShot = -1, firstContact = -1, lineClash = -1, firstBreak = -1;

        static bool IsLine(Unit u) => u.Type.Class is UnitClass.Infantry or UnitClass.Spear or UnitClass.Pike;

        // How long each unit spent in each formation.
        //
        // Worth its few lines. When the commander was first taught to change formation it
        // took Rome from eight wins in fourteen to one, and three blind five-minute sweeps
        // failed to say which of the five new rules was responsible. A census answers it
        // from a single battle: the formation a unit is standing in for most of the fight
        // is the one deciding the fight.
        var census = new Dictionary<(int, FormationType), int>();

        while (!sim.IsOver && sim.State.Tick < SimConstants.TickRate * 60 * 40)
        {
            sim.Tick();

            foreach (Unit unit in sim.State.Units)
            {
                if (unit.IsOutOfAction) continue;
                var key = (unit.Id, unit.Formation);
                census[key] = census.GetValueOrDefault(key) + 1;
            }

            if (firstShot < 0 && sim.State.MissileCount > 0) firstShot = sim.State.Tick;
            if (firstContact < 0 && sim.State.Units.Any(u => u.InContact)) firstContact = sim.State.Tick;

            // The one that actually matters. Cavalry reaches the enemy in twenty
            // seconds whatever else is true; the battle proper starts when the heavy
            // infantry meets.
            if (lineClash < 0 && sim.State.Units.Any(u => IsLine(u) && u.InContact))
                lineClash = sim.State.Tick;

            if (firstBreak < 0 && sim.State.Units.Any(u => u.MoraleState == MoraleState.Routing))
                firstBreak = sim.State.Tick;

            Record(sim.State, log);
            sim.State.DrainEvents();
        }
        clock.Stop();

        string Stamp(int tick) => tick < 0
            ? "  never"
            : $"{tick / SimConstants.TickRate / 60:D2}:{tick / SimConstants.TickRate % 60:D2}";

        Console.WriteLine();
        Console.WriteLine($"  missiles {Stamp(firstShot)}   any contact {Stamp(firstContact)}   " +
                          $"LINES CLASH {Stamp(lineClash)}   first break {Stamp(firstBreak)}   " +
                          $"ended {Stamp(sim.State.Tick)}");

        Console.WriteLine(renderer.Draw());
        foreach (string line in log) Console.WriteLine(line);
        Console.WriteLine();

        Console.WriteLine("  time in formation");
        foreach (Unit unit in sim.State.Units)
        {
            var spent = census
                .Where(entry => entry.Key.Item1 == unit.Id && entry.Value > SimConstants.TickRate)
                .OrderByDescending(entry => entry.Value)
                .Select(entry => $"{entry.Key.Item2.ToString().ToLowerInvariant()} " +
                                 $"{entry.Value / SimConstants.TickRate}s")
                .ToList();

            if (spent.Count == 0) continue;
            Console.WriteLine($"    {sim.State.Armies[unit.ArmyId].Name,-9} {unit.Type.Name,-22} " +
                              string.Join("  ", spent));
        }
        Console.WriteLine();

        Summary(sim.State, renderer);
        Console.WriteLine($"  simulated {sim.State.Tick} ticks in {clock.Elapsed.TotalSeconds:F2}s " +
                          $"({sim.State.Tick / clock.Elapsed.TotalSeconds:F0} ticks/sec, " +
                          $"{sim.State.SoldierCount} soldiers)");
        return 0;
    }

    private static int Sweep(Options options)
    {
        var wins = new int[2];
        int draws = 0;
        double totalMinutes = 0;

        // Surviving fraction per side, averaged over the sweep.
        //
        // Who won is one bit per battle, and one bit is a terrible instrument: fourteen
        // battles put an error bar of roughly plus or minus seventeen points on the win
        // rate, which is wider than most balance changes are. Six sweeps were spent
        // reading noise as signal on this metric alone.
        //
        // How much of each army was left standing is continuous, moves smoothly with the
        // thing being tuned, and separates "narrowly, on the last unit" from "swept from
        // the field" — which the win column cannot do at any sample size.
        var survivingFraction = new double[2];

        BattleSim probe = Build(options.Seed, options.Swap);
        string firstName = probe.State.Armies[0].Name;
        string secondName = probe.State.Armies[1].Name;

        Console.WriteLine($"Running {options.Sweep} battles from seed {options.Seed}" +
                          (options.Swap ? " (sides swapped)" : "") + "...");
        Console.WriteLine();

        var clock = Stopwatch.StartNew();

        for (uint i = 0; i < options.Sweep; i++)
        {
            BattleSim sim = Build(options.Seed + i, options.Swap);
            sim.Run(SimConstants.TickRate * 60 * 40);
            sim.State.DrainEvents();

            double minutes = sim.State.Tick / (double)SimConstants.TickRate / 60;
            totalMinutes += minutes;

            if (sim.State.Result == BattleResult.ArmyVictory) wins[sim.State.Victor]++;
            else draws++;

            string winner = sim.State.Victor >= 0 ? sim.State.Armies[sim.State.Victor].Name : "draw";
            int survivors = 0;
            var standing = new int[2];
            var fielded = new int[2];

            foreach (Army army in sim.State.Armies)
                foreach (int unitId in army.UnitIds)
                {
                    Unit unit = sim.State.Units[unitId];
                    fielded[army.Id] += unit.Strength;
                    if (!unit.IsEffective) continue;
                    survivors += unit.Alive;
                    standing[army.Id] += unit.Alive;
                }

            for (int side = 0; side < 2; side++)
                survivingFraction[side] += fielded[side] == 0 ? 0 : standing[side] / (double)fielded[side];

            Console.WriteLine($"  seed {options.Seed + i,-6} {winner,-10} {minutes,5:F1} min   " +
                              $"{survivors,5} still standing");
        }

        clock.Stop();
        Console.WriteLine();
        Console.WriteLine($"  {firstName} {wins[0]}   {secondName} {wins[1]}   draws {draws}");
        Console.WriteLine($"  still standing at the end: {firstName} " +
                          $"{survivingFraction[0] / options.Sweep * 100:F1}%   {secondName} " +
                          $"{survivingFraction[1] / options.Sweep * 100:F1}%");
        Console.WriteLine($"  average battle {totalMinutes / options.Sweep:F1} minutes, " +
                          $"swept in {clock.Elapsed.TotalSeconds:F1}s");
        return 0;
    }

    // ----------------------------------------------------------------- helpers

    private static void Paint(BattleRenderer renderer, BattleState state, Queue<string> log)
    {
        var sb = new StringBuilder();
        sb.Append("[H");           // home the cursor rather than clearing, so it does not flicker
        sb.AppendLine(renderer.Status());
        sb.Append(renderer.Draw());

        foreach (string line in log) sb.AppendLine(line.PadRight(Math.Max(renderer.Status().Length, 60)));
        for (int i = log.Count; i < 6; i++) sb.AppendLine(new string(' ', 60));

        Console.Write(sb.ToString());
    }

    /// <summary>Lines of battle log kept. The live view shows a tail; --fast keeps everything.</summary>
    private static int LogLimit = 6;

    private static void Record(BattleState state, Queue<string> log)
    {
        foreach (BattleEvent e in state.Events)
        {
            string? line = e.Type switch
            {
                BattleEventType.UnitBroke =>
                    $"  {Clock(state)}  BROKE: {War.Sim.Sim.Systems.MoraleSystem.Explain(state, state.Units[e.A])}",
                BattleEventType.UnitRallied =>
                    $"  {Clock(state)}  {state.Units[e.A].Type.Name} rallies",
                BattleEventType.GeneralKilled =>
                    $"  {Clock(state)}  the {state.Armies[e.B].Name} general is down",
                BattleEventType.UnitDestroyed =>
                    $"  {Clock(state)}  {state.Units[e.A].Type.Name} quits the field",
                _ => null,
            };

            if (line == null) continue;
            log.Enqueue(line);

            // The live view has room for six lines; a resolved battle wants the lot.
            // Trimming to six here once hid the very event being investigated.
            while (log.Count > LogLimit) log.Dequeue();
        }
    }

    private static string Clock(BattleState state)
    {
        int seconds = state.Tick / SimConstants.TickRate;
        return $"{seconds / 60:D2}:{seconds % 60:D2}";
    }

    private static void Summary(BattleState state, BattleRenderer renderer)
    {
        Console.WriteLine();
        Console.WriteLine(state.Result switch
        {
            BattleResult.ArmyVictory => $"  {state.Armies[state.Victor].Name} holds the field after {Clock(state)}.",
            BattleResult.Draw => $"  Neither side could break the other. {Clock(state)}.",
            _ => $"  Still fighting at {Clock(state)}.",
        });
        Console.WriteLine();

        foreach (Army army in state.Armies)
        {
            Console.WriteLine($"  {army.Name} — {renderer.Standing(army.Id)} of {army.InitialMen} still in the line");
            foreach (string line in renderer.Roster(army.Id)) Console.WriteLine(line);
            Console.WriteLine();
        }
    }

    private static BattleSim Build(uint seed, bool swapSides = false)
    {
        Terrain terrain = TerrainGenerator.Generate(new BattlefieldSettings
        {
            Seed = seed,
            Hilliness = Fix.Ratio(11, 10),
            ForestCoverage = Fix.Ratio(16, 100),
            CentralRidge = true,
        });

        ArmyBlueprint rome = Matchups.Rome();
        ArmyBlueprint carthage = Matchups.Carthage();

        // Swapping which army is listed first is how you tell a roster imbalance from a
        // positional one. Soldier ids are allocated army by army and every loop walks
        // them in order, so army 0 strikes marginally earlier within a tick; if the same
        // side keeps winning after a swap, that ordering is a fairness bug rather than a
        // design choice.
        return BattleSim.Create(new BattleSetup
        {
            Terrain = terrain,
            Seed = seed,
            // Wide enough that there is a real approach to think during, and room to
            // reposition before the lines meet.
            Separation = Fix.FromInt(380),
            Armies = swapSides ? [carthage, rome] : [rome, carthage],
        });
    }

    private sealed class Options
    {
        public uint Seed { get; private set; } = 4471;
        public int Width { get; private set; } = 110;
        public int Height { get; private set; } = 34;
        public int Speed { get; private set; } = 3;
        public bool Fast { get; private set; }
        public int Sweep { get; private set; }
        public bool Colour { get; private set; } = true;
        public bool Swap { get; private set; }

        public static Options Parse(string[] args)
        {
            var options = new Options();

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--seed" when i + 1 < args.Length:
                        options.Seed = uint.Parse(args[++i]);
                        break;
                    case "--speed" when i + 1 < args.Length:
                        options.Speed = int.Parse(args[++i]);
                        break;
                    case "--sweep" when i + 1 < args.Length:
                        options.Sweep = int.Parse(args[++i]);
                        break;
                    case "--width" when i + 1 < args.Length:
                        options.Width = int.Parse(args[++i]);
                        break;
                    case "--height" when i + 1 < args.Length:
                        options.Height = int.Parse(args[++i]);
                        break;
                    case "--fast":
                        options.Fast = true;
                        break;
                    case "--swap":
                        options.Swap = true;
                        break;
                    case "--no-colour":
                    case "--no-color":
                        options.Colour = false;
                        break;
                }
            }

            return options;
        }
    }
}
