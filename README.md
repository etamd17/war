# WAR

A Rome: Total War–style game. Build a civilization, then fight its battles yourself —
real-time tactics on the field, at the scale the genre demands: individual soldiers,
formations that matter, and armies that break and run rather than politely dying to the
last man.

Built with **Godot 4.5 (.NET)**.

> **Status: milestone 1 in progress** — the battle engine. See [DESIGN.md](DESIGN.md)
> for the full design and roadmap.

---

## Layout

```
sim/War.Sim/         The battle simulation. Pure C#, zero Godot references,
                     fully deterministic, runnable headless.
sim/War.Sim.Tests/   xUnit suite covering the sim.
game/                Godot project — rendering, input, UI. Contains no game rules.
```

The split is deliberate. The simulation knows nothing about the engine, which means the
combat model can be tested, replayed, and batch-run for balance sweeps without Godot
being involved at all. The Godot layer is a viewer and a controller: it draws sim state
and feeds player orders in, and it never advances the simulation itself.

## Determinism

Battles are reproducible from `(seed, army lists, terrain, command log)`. Getting there
required banning floating point from the simulation entirely — IEEE 754 permits results
to differ across compilers and architectures, which would break replays and any future
lockstep multiplayer.

Instead the sim runs on `Fix`, a Q16.16 fixed-point type, with integer implementations
of square root and trigonometry, an explicitly seeded xorshift generator, and a fixed
30 Hz timestep. The renderer interpolates between ticks for smooth visuals.

## Building

Requirements:

- **.NET 8 SDK** — Godot 4.5's .NET build targets `net8.0`
- **Godot 4.5, .NET variant** — from [godotengine.org/download](https://godotengine.org/download);
  the plain build will not run C#

Run the simulation test suite (no Godot required):

```bash
dotnet test War.slnx
```

Open `game/project.godot` in Godot to play.
