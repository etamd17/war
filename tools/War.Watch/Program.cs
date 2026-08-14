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

        var clock = Stopwatch.StartNew();
        var log = new Queue<string>();

        while (!sim.IsOver && sim.State.Tick < SimConstants.TickRate * 60 * 40)
        {
            sim.Tick();
            Record(sim.State, log);
            sim.State.DrainEvents();
        }
        clock.Stop();

        Console.WriteLine(renderer.Draw());
        foreach (string line in log) Console.WriteLine(line);
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
            foreach (Army army in sim.State.Armies)
                foreach (int unitId in army.UnitIds)
                    if (sim.State.Units[unitId].IsEffective) survivors += sim.State.Units[unitId].Alive;

            Console.WriteLine($"  seed {options.Seed + i,-6} {winner,-10} {minutes,5:F1} min   " +
                              $"{survivors,5} still standing");
        }

        clock.Stop();
        Console.WriteLine();
        Console.WriteLine($"  {firstName} {wins[0]}   {secondName} {wins[1]}   draws {draws}");
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

    private static void Record(BattleState state, Queue<string> log)
    {
        foreach (BattleEvent e in state.Events)
        {
            string? line = e.Type switch
            {
                BattleEventType.UnitBroke =>
                    $"  {Clock(state)}  {state.Units[e.A].Type.Name} breaks and runs",
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
            while (log.Count > 6) log.Dequeue();
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

        var rome = new ArmyBlueprint
        {
            Faction = Faction.Rome,
            Name = "Rome",
            Units =
            [
                new UnitBlueprint { TypeId = "rome_velites" },
                new UnitBlueprint { TypeId = "rome_hastati" },
                new UnitBlueprint { TypeId = "rome_hastati" },
                new UnitBlueprint { TypeId = "rome_principes" },
                new UnitBlueprint { TypeId = "rome_principes" },
                new UnitBlueprint { TypeId = "rome_triarii" },
                new UnitBlueprint { TypeId = "rome_equites" },
                new UnitBlueprint { TypeId = "rome_general" },
            ],
        };

        var carthage = new ArmyBlueprint
        {
            Faction = Faction.Carthage,
            Name = "Carthage",
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

        // Swapping which army is listed first is how you tell a roster imbalance from a
        // positional one. Soldier ids are allocated army by army and every loop walks
        // them in order, so army 0 strikes marginally earlier within a tick; if the same
        // side keeps winning after a swap, that ordering is a fairness bug rather than a
        // design choice.
        return BattleSim.Create(new BattleSetup
        {
            Terrain = terrain,
            Seed = seed,
            Separation = Fix.FromInt(320),
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
