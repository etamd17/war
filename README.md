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

You need **Godot 4.7, the build labelled .NET** (the plain build will not run C#), from
[godotengine.org/download](https://godotengine.org/download). Open `game/project.godot`
and press play — Godot builds the C# on first open.

Any recent .NET SDK will do. The game targets `net8.0` because that is what GodotSharp
itself is built against, but a newer SDK compiles it perfectly well; this was developed
with only the .NET 10 SDK installed.

If you upgrade Godot, the version in the first line of `game/War.Game.csproj` has to
match the engine exactly.

### Verifying it without opening the editor

Godot can run the game headless, which makes the presentation layer testable the same way
the simulation is:

```bash
godot --path game --headless --quit-after 600
```

It prints one line naming the armies and the soldier count. That line appearing means the
battle was built, the terrain meshed, every army instanced and the HUD assembled without
throwing — no window, nobody watching, and suitable for CI.

The game will also photograph itself and exit:

```bash
godot --path game --resolution 1600x900 --quit-after 1200 -- --shot=out.png --shot-frame=900 --speed=4
```

That one is worth having. A renderer that merely starts proves very little — the
interesting failures are geometry inside out, a camera aimed at nothing, a HUD off the
edge of the screen. None of those throw and all of them are obvious in one picture. The
terrain being drawn inside out and invisible was found exactly this way, on a build that
was otherwise running perfectly and reporting no errors at all.

### Performance

About **155 fps** at 1340 soldiers on a Ryzen 7 3700X and RTX 4070 SUPER, at 1600×900,
running the editor's Debug build of the C#. That is CPU-bound: the same scene headless,
drawing nothing at all, runs at 145 fps, so rendering is close to free.

Getting there took two fixes worth knowing about, because neither is visible as draw
calls or triangles — the scene submits 213 draw calls and 850k primitives, which a modern
GPU does not notice:

- **`MultiMesh.CustomAabb`.** Every `SetInstanceTransform` marks the bounding box dirty
  and Godot recomputes it across all instances, so writing n transforms costs O(n²) per
  unit per frame. Pinning the AABB took it from 36 to 87 fps.
- **Bulk `MultiMesh.Buffer` writes.** Setting each transform and colour individually
  means two marshalled calls per soldier per frame. Writing the raw float buffer once per
  unit took it from 87 to 155 fps.

The speed controls above 2× are limited by the simulation rather than the renderer, and
the editor runs a Debug build of it; an exported release build is several times faster.

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

A battle opens in **deployment**: the clock is stopped, the armies are drawn up, and a
line of stakes marks how far forward you may come. Draw your line, then press Enter.

| | |
|---|---|
| **Enter** | Begin the battle (deployment only) |
| **Right click and drag, while deploying** | Place a unit outright. Several selected units share the line you draw, so a whole wing goes down in one gesture |
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
- **Fresh troops are steadier than bled ones.** Contagion, fear and being outnumbered are
  all softened for a unit that is still intact, so panic has to start with troops who
  have actually been fought. Without that rule an army routed *while at full strength*:
  one cavalry unit panicking near the elephants spread −10, −20, −30 down the line and
  dissolved five hundred men in twenty-five seconds, having lost almost none of them.

Battles run about six and a half minutes on average, with a spread from four to
seventeen. `SimConstants.Lethality` is the single knob for that — it scales every hit
chance, melee and missile alike, so battle length moves without any ratio in the model
changing. Lower is longer.
