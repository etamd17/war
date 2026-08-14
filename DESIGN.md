# WAR — Design Document

A Rome: Total War–style game: build a civilization, then fight its battles yourself in
real time on the field.

Status: **Milestone 1 in progress** — the battle engine.

---

## 1. Decisions

These were settled up front and everything below follows from them.

| Decision | Choice | Why |
|---|---|---|
| Engine | Godot 4.5 (.NET / C#) | Real 3D engine; C# is ~5–10× faster than GDScript in the per-soldier loop, which is where all the cost is |
| First milestone | Battle engine | The hard part, and the part that makes or breaks the genre |
| Scale | ~120 men/unit, up to 20 units/side, ~2400 individually simulated soldiers | RTW scale. Anything less doesn't feel right |
| Setting | Historical, c. 270–146 BC | Rome, Carthage, Gauls, Greeks, Egypt. Real rosters already encode the counters |
| Art | Procedural low-poly placeholders | Zero asset dependency; swappable later |
| Determinism | Yes, from day one | Replays, reproducible bugs, batch balance testing, multiplayer later without a rewrite |
| Enemy AI | Basic tactical commander | Holds a line, screens, flanks, targets archers, keeps a reserve |
| Non-negotiable systems | Morale/routing, formations/facing, terrain/fatigue, combined-arms counters | All four, in the core |

Target hardware (the dev machine, and the baseline we tune against): Ryzen 7 3700X
8c/16t, RTX 4070 SUPER, 32 GB. Budget: **2400 soldiers at 60 fps**, sim tick under
5 ms single-threaded before we reach for the thread pool.

---

## 2. Architecture

The single most important structural decision: **the simulation has zero Godot
dependencies.**

```
F:\war
├── DESIGN.md
├── War.slnx
├── sim/
│   ├── War.Sim/            pure C# class library — NO Godot references
│   └── War.Sim.Tests/      xUnit — determinism, combat math, morale, formations
├── game/                   Godot 4.5 .NET project — rendering, input, UI only
└── tools/
    └── War.Watch/          terminal renderer and balance sweeper, no engine needed
```

`War.Sim` is a headless, deterministic battle simulation that can run in a console, in
a test harness, or a thousand times in a loop for balance sweeps. `game/` is a *viewer
and controller* for it: it reads sim state and draws it, and it pushes player orders in.
Nothing in `game/` may contain combat rules.

This buys three things:

1. **Verifiability without the engine.** The whole combat model is testable with
   `dotnet test` — no Godot install required to prove the math is right.
2. **Determinism is enforceable.** Godot's frame timing, physics, and RNG can't leak
   into the sim, because the sim can't see them.
3. **Replays and multiplayer are cheap later.** A battle is a seed plus an ordered list
   of commands. That's it.

### Framework targets

`War.Sim` multi-targets `net8.0;net10.0`. Godot 4.5 .NET expects `net8.0`; the test
project runs on `net10.0`, which is the SDK actually installed. Godot itself needs the
**.NET 8 SDK** installed to build the game project.

---

## 3. Determinism

Determinism is not "seed the RNG and hope." Floating point is allowed to vary across
compilers, architectures, and optimization levels, so the sim uses **fixed-point math**
throughout.

- `Fix` — Q16.16 signed fixed point on `int`, with `long` intermediates for multiply and
  divide. Range ±32768, precision ~1.5e-5. The battlefield is ~1000 m across, so this is
  a comfortable fit.
- `FixVec2` — 2D vector; all soldier positions, velocities, and combat geometry.
- `FixMath` — `Sqrt` (Newton's method on integers), `Sin`/`Cos`/`Atan2` (lookup tables
  with linear interpolation). No `System.Math` anywhere in the sim.
- `DetRandom` — xorshift128, explicitly seeded, one stream per subsystem so adding a new
  call site in one system doesn't shift another system's rolls.

Rules the sim obeys:

- **Fixed timestep.** 30 ticks/sec, always. Never `delta`. The renderer interpolates
  between the last two sim states for smooth 60+ fps visuals.
- **Deterministic iteration order.** No `Dictionary` iteration, no unordered parallelism
  affecting results. Arrays indexed by id.
- **No wall-clock, no `DateTime`, no unseeded `Random`.**
- **`FixVec2Sum` for any average over more than a handful of positions.** Q16.16
  saturates at 32768, so summing 140 soldier positions to find a unit's centre wraps
  negative and puts the unit hundreds of metres off the map. Nothing throws — the unit
  stops being targeted and silently drops out of the battle. Accumulate in 64 bits.

A battle is therefore reproducible from `(seed, army lists, terrain params, command log)`.

---

## 4. The simulation

### 4.1 Data layout

Soldiers are stored in flat arrays indexed by id, in a struct-of-arrays–friendly layout,
so the hot loops are cache-coherent. 2400 soldiers × ~64 B is ~150 KB — the entire army
fits in L2.

```
Soldier   position, facing, health, state, fatigue, attackCooldown,
          targetSoldier, formationSlot, unitId
Unit      typeId, factionId, soldier range, order, formation, morale,
          moraleState, ammo, chargeTimer, cohesion
Army      units, general, faction, reinforcement timer
```

Soldier states: `Forming`, `Marching`, `Charging`, `Fighting`, `Firing`, `Wavering`,
`Routing`, `Rallying`, `Dead`.

### 4.2 Tick order

Order matters and is fixed:

1. **Spatial hash rebuild** — uniform grid, ~4 m cells. Every neighbour query in the
   frame goes through this.
2. **Commander AI** (every 15 ticks, staggered per army) — issues unit orders.
3. **Unit orders → soldier goals** — formation slot targets, melee targets, flee vectors.
4. **Movement** — seek goal, separation from neighbours (soft collision, not physics),
   terrain slope and ground-type speed modifiers, formation cohesion.
5. **Charge resolution** — impact detection, charge bonus applied, decays over 5 s.
6. **Melee** — paired attacker/defender resolution for soldiers in contact.
7. **Missiles** — volley timing, ballistic flight, impact, friendly-fire check.
8. **Fatigue** — accrual from running/fighting/uphill/armour, recovery when idle.
9. **Morale** — per unit, recomputed from the full modifier set; hysteresis; break/rally.
10. **Victory check** — army rout threshold, general death, timer.

### 4.3 Combat resolution

Every strike is one roll. Both sides are a sum of legible modifiers, so balance changes
are readable rather than magic.

```
offense  = AttackSkill
         + ChargeBonus            (decays over 5 s after impact)
         + BonusVs(targetClass)   (spear vs cavalry, etc.)
         + FlankBonus             (+4 flank, +8 rear)
         − SlopePenalty           (up to ±4, from the gradient underfoot)
         + FormationBonus
         − FatiguePenalty         (up to −6 when exhausted)

defense  = DefenceSkill
         + Shield                 (only vs attacks from front or left)
         + Armour
         + FormationBonus         (front or flank, depending where the blow lands)
         − FatiguePenalty

hitChance = clamp(0.04 + 0.012 × (offense − defense + 7), 0.008, 0.35)
```

Two calibration details that are not cosmetic:

- **The `+7` offset.** Defence sums three stats and offense is essentially one, so a raw
  subtraction sits permanently on the floor and every modifier on either side becomes
  academic. The offset recentres a normal head-on matchup on the base chance.
- **The base chance is low and the tempo is slow.** Most blows in a real melee were
  blocked, parried, or turned. Tuned so about fifty men in contact kill roughly one man
  a second between them, which puts a decisive clash at a minute or two and lets the
  player react to it. Four times that rate destroyed a 120-man unit in ten seconds.

A single `MeleeTempo` constant scales every attack interval, so the pace of the whole
battle is one knob and the roster keeps its relative timings.

**Facing is real, and formations are flanked as a body.** The attack arc is measured
against the *formation's* facing, not each soldier's. Measuring it per man looks more
faithful and destroys the mechanic: soldiers turn to face whoever is hitting them, so
the penalty cancels within a second of contact and a rear attack ends up no better than
a frontal one. A body of men drawn up one way cannot all turn — and a unit already in
melee wheels at 8% of its rate, so pinning it holds the flank open. Formations that
genuinely face outward (square) and men who have already broken are flanked individually,
because there is no formation left to flank.

**Reach is pairwise and measured surface to surface** — `own reach + both collision
radii`. Centre-to-centre reach means an elephant, at 1.5 m radius against an
infantryman's 0.4 m, is held 1.9 m away by separation and can never land a blow at all.

Units whose whole point is going *through* people — elephants, chariots — strike several
men per swing via `AttacksPerStrike`.

### 4.4 Morale — the system that actually decides battles

Units almost never fight to the last man. They break. Morale is recomputed per unit per
tick from:

| Modifier | Effect |
|---|---|
| Casualties taken | Non-linear; falls off a cliff past ~40% losses |
| Nearby friendly units routing | −, scaled by proximity and how many |
| Attacked in flank or rear | −− , the single biggest swing |
| General alive and nearby | + (aura radius), and −− the moment he dies |
| Winning locally | + when killing faster than dying |
| Fatigue | − when exhausted |
| Locally outnumbered | − by ratio within a radius |
| High ground | + |
| Unit discipline / experience | + , per unit type |

Thresholds with hysteresis so units don't flicker: `< 25` → **Wavering** (slower, worse
in combat, may still hold), `< 10` → **Broken** → routs away from the nearest threat.
A routing unit that gets clear of pursuit and recovers above 30 will **Rally** — and
routers who reach the map edge are gone for good.

Pursuing routers is cheap kills, and chasing too far with your cavalry is how you lose
the battle. That trade-off is deliberate.

### 4.5 Terrain

A heightmap grid over the battlefield, plus a forest mask and a ground-type layer.

- **Elevation** — scored from the **slope underfoot**, not the height difference between
  the two men. Two soldiers close enough to fight are about a metre and a half apart; on
  a normal hillside that is twenty centimetres of height, so scoring elevation gives
  almost exactly zero and terrain stops mattering. What decides the exchange is that one
  man is on the slope below the other. High ground also gives a morale bonus and extends
  missile range, both of which do use true elevation.
- **Forest** — blocks line of sight (units go unspotted until close), degrades formation
  cohesion, slows movement, and cancels the cavalry charge bonus. Ambushes work.
- **Ground type** — mud slows and tires; rock is fast; fords slow river crossings to a
  crawl and make crossing under fire disastrous.
- **Fatigue** is driven by all of the above plus armour weight. Exhausted units swing
  slower, hit softer, defend worse, and break sooner. Reserves matter.

### 4.6 Formations

Line, Column, Wedge, Square, Testudo, Phalanx. Each is a slot-generation function over
(width, depth, spacing) plus behaviour flags.

- **Phalanx** — massive frontal bonus, pikes engage at range, nearly helpless on the flank.
- **Testudo** — huge missile protection, poor melee, slow.
- **Wedge** — cavalry; concentrates the charge for breakthrough.
- **Square** — no flanks; the answer to being surrounded by cavalry.

Width and depth are player-adjustable by dragging, RTW-style. Thin lines cover ground
and avoid envelopment; deep lines hold longer and push harder.

A unit's **anchor is the middle of its front rank**, not its centre of mass. Placing a
unit places the line you can see, and a unit ordered to a spot arrives with its front
rank on that spot. Men seek slots relative to the anchor rather than to the unit's own
centre — if the formation chased its centre of mass, casualties on one flank would drag
the whole line sideways.

### 4.7 Combined arms

The counter web the rosters are built to express:

- Spears/pikes shred cavalry frontally, lose to swords
- Cavalry destroys archers and anything caught in the flank, dies on a spear wall
- Archers/slingers wither dense infantry, are useless against a testudo, melt in melee
- Elephants and chariots panic horses and shatter lines, but rout catastrophically and
  trample their own side when they do
- Skirmishers screen, bait, and retire through gaps

### 4.8 Commander AI

Runs on a 0.5 s cadence, staggered between armies. Behaviour for milestone 1:

- Form and hold a battle line matched to the player's frontage
- Screen with skirmishers, then retire them behind the line
- Concentrate missiles on the most valuable exposed target — archers first
- Send cavalry wide to flank exposed or already-engaged units
- Keep a reserve and commit it at a local break
- Pull back badly mauled units, rally routers behind the line
- Refuse a flank when outmatched rather than charging in

Faction-specific doctrine (Roman line rotation, Gallic mass charge, Carthaginian
envelopment) is designed for but deferred past milestone 1.

---

## 5. Presentation (Godot layer)

- **Soldiers** — one `MultiMeshInstance3D` per (faction, unit type). One instance per
  soldier, transforms written each frame from interpolated sim state. Procedural low-poly
  meshes: body, helmet, shield quad, weapon, faction colour. Corpses stay on the field.
- **Terrain** — mesh generated from the sim's heightmap with vertex colours for ground
  type; trees as a `MultiMesh` of cone + trunk.
- **Banners** — a pole and faction-coloured quad at each unit's centre, which is how you
  read the battle at a distance.
- **Camera** — RTS: WASD/edge pan, wheel zoom from tabletop overview down to ground
  level, middle-drag orbit.
- **UI** — unit cards along the bottom (strength, morale, ammo, fatigue), minimap,
  formation and stance buttons, pause and 1×/2×/3× speed.
- **Controls** — drag-select, right-click move, **right-click-drag to set facing and
  width**, group hotkeys, alt-click attack-move.

The renderer interpolates between sim ticks; it never advances state.

---

## 6. Roadmap

- **M1 — Battle engine.** One map, Rome vs Carthage, ~8 units a side, full morale /
  formation / terrain / counters, tactical AI, victory conditions. *(current)*
- **M2 — Battle depth.** Full five-faction roster, siege-free varied maps, weather,
  night, reinforcements, replays.
- **M3 — Campaign.** Turn-based map: provinces, cities, buildings, economy, recruitment,
  army movement, agents, diplomacy; battles hand off to M1's engine.
- **M4 — Sieges.** Walls, towers, rams, ladders, street fighting.
