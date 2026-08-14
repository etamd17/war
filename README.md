# WAR

A Rome: Total War–style game. Build a civilization, then fight its battles yourself —
real-time tactics on the field, at the scale the genre demands: individual soldiers,
formations that matter, and armies that break and run rather than politely dying to the
last man.

Built with **Godot 4.5 (.NET)**.

> **Status: milestone 1** — the battle engine. A complete Rome versus Carthage field
> battle runs end to end. See [DESIGN.md](DESIGN.md) for the full design and roadmap.

---

## Running it

You need two things installed:

- **.NET 8 SDK** — Godot 4.5's .NET build targets `net8.0`
- **Godot 4.5, .NET variant** — from [godotengine.org/download](https://godotengine.org/download).
  The plain build will not run C#.

Then open `game/project.godot` in Godot and press play.

## Watching a battle without Godot

The simulation is entirely independent of the engine, which means you can run a whole
battle in a terminal right now — it renders the field, the ridge line, the woods, both
armies and their dead:

```bash
dotnet run -c Release --project tools/War.Watch
```

Resolve one immediately and print the result:

```bash
dotnet run -c Release --project tools/War.Watch -- --fast
```

Run twenty battles and tally who won:

```bash
dotnet run -c Release --project tools/War.Watch -- --sweep 20
```

The sweep is the one that earns its keep. Ancient battles turn on morale, which is noisy
by design, so a single result says very little about whether a balance change helped and
twenty say a great deal. It was a sweep that showed the Roman army list winning nine
battles in twelve — and a second sweep with the sides swapped that proved the cause was
the roster rather than the simulation's tick order.

The test suite likewise needs no engine:

```bash
dotnet test War.slnx
```

And the Godot layer can be typechecked without opening Godot, since its SDK comes from
NuGet:

```bash
dotnet build game/War.Game.csproj
```

## Controls

| | |
|---|---|
| Left click / drag | Select a unit, or drag a box over several |
| Right click | Move there |
| **Right click and drag** | Draw the front rank: the line you draw sets frontage and facing |
| Right click an enemy | Attack |
| Shift + right click | Move at a run |
| Shift + click | Add to selection |
| `F` | Cycle formation (line, column, wedge, square, testudo, phalanx, skirmish) |
| `[` / `]` | Narrow / widen the line |
| `R` | Toggle walk and run |
| `T` | Toggle fire at will |
| `Space` | Pause |
| WASD / arrows / screen edge | Pan |
| Mouse wheel | Zoom — the camera flattens toward eye level as you descend |
| Middle drag, `Q` / `E` | Orbit |

Drag-to-draw is the control worth learning. Widening a line stops you being overlapped
and enveloped; deepening it holds ground longer and pushes harder. That is a decision
you make by drawing it, not by picking from a menu.

---

## Layout

```
sim/War.Sim/         The battle simulation. Pure C#, zero Godot references,
                     fully deterministic, runnable headless.
sim/War.Sim.Tests/   xUnit suite covering the sim.
game/                Godot project — rendering, input, UI. Contains no game rules.
```

The split is deliberate and strictly one-way: the game knows about the simulation, and
the simulation has never heard of Godot. Combat can therefore be tested, replayed, and
batch-run for balance sweeps without the engine being involved at all — and the entire
combat model was in fact developed and debugged before Godot was installed on this
machine. The Godot layer is a viewer and a controller: it draws simulation state and
feeds player orders in, and it never advances the simulation itself.

## Determinism

Battles are reproducible from `(seed, army lists, terrain, command log)`. Getting there
required banning floating point from the simulation entirely — IEEE 754 permits results
to differ across compilers and architectures, which would break replays and any future
lockstep multiplayer.

Instead the sim runs on `Fix`, a Q16.16 fixed-point type, with integer implementations of
square root and trigonometry, an explicitly seeded xorshift generator per subsystem, and
a fixed 30 Hz timestep. The renderer interpolates between ticks for smooth visuals at any
frame rate.

That choice has teeth. Q16.16 saturates at 32768, so summing 140 soldier positions to
find a unit's centre overflows and silently teleports the unit off the map — a bug that
threw nothing, logged nothing, and simply removed a unit from the battle. Anything that
averages more than a handful of positions goes through `FixVec2Sum`, which accumulates
in 64 bits.

## What the battle model actually does

- **Morale decides battles, not casualties.** Units break from flanking, losses, nearby
  routs, exhaustion, local odds, fear, and their general going down — then rout, and may
  rally if they get clear. Almost every casualty comes after one side breaks.
- **Facing is real, and formations are flanked as a body.** The attack arc is measured
  against the formation's facing, not each man's, and a unit already in melee wheels at
  8% of its turn rate. Pin a unit and its flank stays open; leave it idle and it will
  turn to meet you.
- **Terrain is not scenery.** Height is scored from the slope underfoot, so the crest is
  worth taking. Woods hide units, break formations, and stop arrows. Mud and fords
  exhaust an army before it arrives.
- **Combined arms.** Spears wreck cavalry frontally and lose to swords; cavalry destroys
  archers and anything caught in the flank; slings pierce armour where arrows do not;
  a phalanx negates a frontal charge and has no answer to its own flank; elephants
  trample several men per swing and rout catastrophically.
- **Fatigue makes reserves worth holding.** A fresh unit beats an exhausted identical one
  roughly two to one.
