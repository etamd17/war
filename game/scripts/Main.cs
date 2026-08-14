using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using War.Sim.Core;
using War.Sim.Sim;
using War.Sim.Units;
using War.Sim.World;

namespace War.Game;

/// <summary>
/// The game.
///
/// Builds the battle, advances it on a fixed clock, draws it, and turns mouse clicks
/// into orders. That is the whole of this layer's job: it holds no rules. Every question
/// about what happens when two units meet is answered inside War.Sim, which has never
/// heard of Godot.
///
/// The simulation runs at exactly 30 Hz regardless of frame rate. Rendering interpolates
/// between the last two ticks, so the men move smoothly at whatever the monitor does
/// while the underlying battle stays reproducible tick for tick.
/// </summary>
public sealed partial class Main : Node3D
{
    private const int PlayerArmy = 0;

    private BattleSim _sim = null!;
    private TerrainView _terrain = null!;
    private ArmyView _armies = null!;
    private RtsCamera _camera = null!;
    private Hud _hud = null!;

    private readonly HashSet<int> _selected = new();

    private double _accumulator;
    private int _speed = 1;
    private bool _paused;

    private Vector2? _dragFrom;
    private Vector3? _orderFrom;
    private MeshInstance3D _deploymentMarker = null!;

    private static double StepSeconds => 1.0 / SimConstants.TickRate;

    private bool _reported;
    private int _frames;
    private string? _shotPath;
    private int _shotFrame = 240;

    public override void _Ready()
    {
        BuildBattle();
        BuildWorld();
        BuildHud();

        // Also the headless smoke test: `godot --headless --quit-after 600` runs the
        // real _Ready and _Process, so this line appearing means the battle was built,
        // the terrain meshed, every army instanced and the HUD assembled without
        // throwing — which is verifiable in CI, with no window and nobody watching.
        GD.Print($"WAR — {_sim.State.Armies[0].Name} vs {_sim.State.Armies[1].Name}, " +
                 $"{_sim.State.SoldierCount} soldiers in {_sim.State.Units.Length} units");

        ReadScreenshotArgs();
    }

    /// <summary>
    /// Lets the game photograph itself and exit:
    ///   godot --path game -- --shot=out.png --shot-frame=600
    ///
    /// Worth the twenty lines. A build that merely starts proves very little about a
    /// renderer — the interesting failures are a camera pointing at nothing, geometry
    /// inside out, a HUD off the edge of the screen. None of those throw, and all of
    /// them are obvious in one picture.
    /// </summary>
    private void ReadScreenshotArgs()
    {
        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            if (argument.StartsWith("--shot=", StringComparison.Ordinal))
                _shotPath = argument["--shot=".Length..];
            else if (argument.StartsWith("--shot-frame=", StringComparison.Ordinal) &&
                     int.TryParse(argument["--shot-frame=".Length..], out int frame))
                _shotFrame = frame;
            else if (argument.StartsWith("--speed=", StringComparison.Ordinal) &&
                     int.TryParse(argument["--speed=".Length..], out int speed))
                _speed = Mathf.Clamp(speed, 1, 20);
        }
    }

    private void CaptureIfAsked()
    {
        if (_shotPath == null || ++_frames < _shotFrame) return;

        Image image = GetViewport().GetTexture().GetImage();
        Error error = image.SavePng(_shotPath);

        GD.Print(error == Error.Ok
            ? $"WAR — wrote {_shotPath} at tick {_sim.State.Tick}"
            : $"WAR — screenshot failed: {error}");

        GD.Print($"WAR — render: {Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)} draw calls, " +
                 $"{Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame):N0} primitives, " +
                 $"{Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame)} objects");

        GetTree().Quit();
    }

    // ------------------------------------------------------------------- setup

    private void BuildBattle()
    {
        // A fixed seed for now. The campaign layer will supply real army lists and a
        // battlefield chosen from the province the fight happens in.
        const uint seed = 4471;

        Terrain terrain = TerrainGenerator.Generate(new BattlefieldSettings
        {
            Seed = seed,
            Hilliness = Fix.Ratio(11, 10),
            ForestCoverage = Fix.Ratio(16, 100),
            CentralRidge = true,
        });

        _sim = BattleSim.Create(new BattleSetup
        {
            Terrain = terrain,
            Seed = seed,
            Separation = Fix.FromInt(380),
            DeploymentPhase = true,
            Armies =
            [
                new ArmyBlueprint
                {
                    Faction = Faction.Rome,
                    Name = "Rome",
                    IsPlayer = true,
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
                },
                new ArmyBlueprint
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
                },
            ],
        });
    }

    private void BuildWorld()
    {
        _terrain = new TerrainView { Name = "Terrain" };
        AddChild(_terrain);
        _terrain.Build(_sim.State.Terrain);

        _armies = new ArmyView { Name = "Armies" };
        AddChild(_armies);
        _armies.Build(_sim.State, _terrain);

        Vector2 start = SimBridge.Plane(_sim.State.Armies[PlayerArmy].DeploymentCentre);
        _camera = new RtsCamera { Name = "Camera" };
        AddChild(_camera);
        _camera.Setup(_terrain, new Vector3(start.X, 0, start.Y), yaw: Mathf.Pi);

        _deploymentMarker = BuildDeploymentMarker();
        AddChild(_deploymentMarker);

        AddChild(new DirectionalLight3D
        {
            Name = "Sun",
            Rotation = new Vector3(-Mathf.Pi * 0.34f, Mathf.Pi * 0.22f, 0),
            LightEnergy = 1.05f,
            ShadowEnabled = true,
            DirectionalShadowMaxDistance = 320,
        });

        var sky = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.34f, 0.50f, 0.72f),
            SkyHorizonColor = new Color(0.71f, 0.76f, 0.78f),
            GroundHorizonColor = new Color(0.55f, 0.53f, 0.46f),
            GroundBottomColor = new Color(0.35f, 0.34f, 0.30f),
        };

        AddChild(new WorldEnvironment
        {
            Name = "Environment",
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky,
                Sky = new Sky { SkyMaterial = sky },
                // Ambient kept low so the sun actually models the ground. At 0.65 the
                // hillsides were lit from every direction at once and the ridge — the
                // thing the whole map is arranged around — read as flat paint.
                AmbientLightSource = Godot.Environment.AmbientSource.Sky,
                AmbientLightEnergy = 0.35f,

                // Enough haze for aerial perspective and no more. The first value tried
                // here was four times this and turned the entire field into pale fog
                // with an army somewhere in it.
                FogEnabled = true,
                FogDensity = 0.0003f,
                FogLightColor = new Color(0.70f, 0.75f, 0.80f),
            },
        });
    }

    /// <summary>
    /// A line of stakes along the forward edge of the player's deployment zone. Without
    /// something to see, "you may draw up anywhere behind here" is an instruction with no
    /// referent, and the first thing a player does is drag a unit somewhere it silently
    /// refuses to go.
    /// </summary>
    private MeshInstance3D BuildDeploymentMarker()
    {
        DeploymentZone zone = _sim.State.Armies[PlayerArmy].Zone;
        var colour = new Color(1f, 0.92f, 0.55f);

        // The forward edge is whichever side of the box faces the enemy.
        float forwardZ = _sim.State.Armies[PlayerArmy].AdvanceDirection.Y > Fix.Zero
            ? zone.Max.Y.ToFloat()
            : zone.Min.Y.ToFloat();

        float from = zone.Min.X.ToFloat();
        float to = zone.Max.X.ToFloat();

        var builder = new MeshBuilder();
        for (float x = from + 6; x < to - 6; x += 14)
        {
            float ground = _terrain.HeightAt(x, forwardZ);
            builder.AddBox(new Vector3(x, ground + 1.1f, forwardZ), new Vector3(0.25f, 2.2f, 0.25f), colour);
            builder.AddBox(new Vector3(x, ground + 2.3f, forwardZ), new Vector3(1.4f, 0.18f, 0.18f), colour);
        }

        return new MeshInstance3D
        {
            Name = "DeploymentLine",
            Mesh = builder.Build(),
            MaterialOverride = MeshBuilder.Material(unshaded: true),
        };
    }

    private void BuildHud()
    {
        _hud = new Hud { Name = "Hud" };
        AddChild(_hud);
        _hud.Build(_sim.State, PlayerArmy, _camera);

        _hud.UnitCardClicked += (unit, additive) =>
        {
            if (!additive) _selected.Clear();
            if (!_selected.Add(unit.Id) && additive) _selected.Remove(unit.Id);
        };

        _hud.SpeedChosen += speed =>
        {
            _speed = speed;
            _paused = false;
        };

        _hud.PauseToggled += () => _paused = !_paused;
        _hud.BeginRequested += BeginBattle;
        _hud.Map.Clicked += world => _camera.LookAtGround(new Vector3(world.X, 0, world.Y));
    }

    // -------------------------------------------------------------------- loop

    public override void _Process(double delta)
    {
        StepSimulation(delta);

        float alpha = (float)Mathf.Clamp(_accumulator / StepSeconds, 0, 1);
        _armies.Update(_sim.State, alpha, _selected);
        _hud.Refresh(_selected);

        if (_sim.IsOver && !_reported)
        {
            _reported = true;
            int seconds = _sim.State.Tick / SimConstants.TickRate;
            string victor = _sim.State.Victor >= 0
                ? _sim.State.Armies[_sim.State.Victor].Name
                : "nobody";
            GD.Print($"WAR — {victor} holds the field after {seconds / 60:D2}:{seconds % 60:D2}");
        }

        CaptureIfAsked();
    }

    private void StepSimulation(double delta)
    {
        // Nothing moves until the commander says so.
        if (_sim.IsDeploying)
        {
            _accumulator = 0;
            return;
        }

        if (_paused || _sim.IsOver)
        {
            // Keep the interpolation parked on the last tick rather than drifting past it.
            _accumulator = Math.Min(_accumulator, StepSeconds);
            return;
        }

        _accumulator += delta * _speed;

        // Cap the catch-up. If a frame takes far too long — a stall, a breakpoint, a
        // dragged window — running every missed tick makes the next frame slower still
        // and the game never recovers. Dropping the backlog costs a moment of simulated
        // time and keeps the loop stable.
        int budget = 12;
        while (_accumulator >= StepSeconds && budget-- > 0)
        {
            _sim.Tick();
            _accumulator -= StepSeconds;
        }
        if (_accumulator > StepSeconds) _accumulator = StepSeconds;

        // The simulation writes events for the presentation layer and never reads them
        // back. Nothing consumes them yet — sound and particles are the next users — but
        // they have to be drained or the list grows for the whole battle.
        _sim.State.DrainEvents();
    }

    // ------------------------------------------------------------------- input

    public override void _UnhandledInput(InputEvent @event)
    {
        _camera.HandleInput(@event);

        switch (@event)
        {
            case InputEventKey { Pressed: true, Echo: false } key:
                HandleKey(key);
                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.Left } left:
                if (left.Pressed) _dragFrom = left.Position;
                else FinishSelection(left);
                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.Right } right:
                if (right.Pressed) _orderFrom = GroundUnder(right.Position);
                else FinishOrder(right);
                break;

            case InputEventMouseMotion motion when _dragFrom is { } from:
                _hud.SetDragRect(RectBetween(from, motion.Position));
                break;
        }
    }

    private void HandleKey(InputEventKey key)
    {
        switch (key.Keycode)
        {
            case Key.Space:
                _paused = !_paused;
                break;

            case Key.F:
                CycleFormation();
                break;

            case Key.R:
                foreach (Unit unit in Selected())
                    unit.Order = unit.Order with { Run = !unit.Order.Run };
                break;

            case Key.T:
                foreach (Unit unit in Selected()) unit.FireAtWill = !unit.FireAtWill;
                break;

            case Key.Bracketleft:
                AdjustWidth(-4);
                break;

            case Key.Bracketright:
                AdjustWidth(+4);
                break;

            case Key.Enter:
            case Key.KpEnter:
                BeginBattle();
                break;

            case Key.Escape:
                _selected.Clear();
                break;
        }
    }

    private IEnumerable<Unit> Selected() =>
        _selected.Select(id => _sim.State.Units[id]).Where(u => !u.IsOutOfAction);

    private void CycleFormation()
    {
        FormationType[] all = Enum.GetValues<FormationType>();

        foreach (Unit unit in Selected())
        {
            int start = Array.IndexOf(all, unit.Formation);
            for (int i = 1; i <= all.Length; i++)
            {
                FormationType next = all[(start + i) % all.Length];
                if (!unit.Type.CanUse(next)) continue;

                unit.Formation = next;
                unit.Width = 0;                 // let the new formation pick its own depth
                unit.SlotsBuiltFor = -1;        // and re-form the men into it
                break;
            }
        }
    }

    private void AdjustWidth(int delta)
    {
        foreach (Unit unit in Selected())
        {
            int width = unit.EffectiveWidth + delta;
            unit.Width = Mathf.Clamp(width, 2, Math.Max(2, unit.Alive));
            unit.SlotsBuiltFor = -1;
        }
    }

    // --------------------------------------------------------------- selection

    private void FinishSelection(InputEventMouseButton release)
    {
        Vector2 from = _dragFrom ?? release.Position;
        _dragFrom = null;
        _hud.SetDragRect(null);

        if (!release.ShiftPressed) _selected.Clear();

        Rect2 rect = RectBetween(from, release.Position);

        if (rect.Size.Length() < 6)
        {
            // A click rather than a drag: take whatever unit is under the cursor.
            if (GroundUnder(release.Position) is { } point &&
                _armies.UnitNear(_sim.State, point, 30f) is { } unit &&
                unit.ArmyId == PlayerArmy)
            {
                _selected.Add(unit.Id);
            }
            return;
        }

        foreach (Unit unit in _armies.UnitsInRectangle(_camera.Camera, rect, PlayerArmy))
            _selected.Add(unit.Id);
    }

    private static Rect2 RectBetween(Vector2 a, Vector2 b) =>
        new(new Vector2(Mathf.Min(a.X, b.X), Mathf.Min(a.Y, b.Y)), (b - a).Abs());

    // ------------------------------------------------------------------ orders

    /// <summary>
    /// Right-click to move; right-click and drag to lay the unit out along the line you
    /// draw. The drag defines the front rank: its length sets the frontage, and the unit
    /// faces the side away from where it currently stands. It is the one control that
    /// makes formation a tactical decision rather than a menu setting — you widen a line
    /// to avoid being overlapped and deepen it to hold, by drawing it.
    /// </summary>
    private void FinishOrder(InputEventMouseButton release)
    {
        Vector3? from = _orderFrom;
        _orderFrom = null;

        if (from is not { } start) return;
        if (GroundUnder(release.Position) is not { } end) return;
        if (_selected.Count == 0) return;

        // Right-clicking an enemy is an attack order, whatever the drag did.
        Unit? enemy = _armies.UnitNear(_sim.State, end, 26f);
        if (enemy != null && enemy.ArmyId != PlayerArmy)
        {
            foreach (Unit unit in Selected()) unit.Order = UnitOrder.Attack(enemy.Id);
            return;
        }

        List<Unit> chosen = Selected().ToList();
        if (chosen.Count == 0) return;

        Vector3 along = end - start;
        along.Y = 0;
        float length = along.Length();

        bool run = Input.IsKeyPressed(Key.Shift);

        if (length < 5f)
        {
            foreach (Unit unit in chosen)
            {
                FixVec2 destination = SimBridge.ToSim(start);

                // While deploying, the same gesture places the unit outright rather than
                // ordering it to walk there. Nothing is moving yet, so there is nothing
                // to order.
                if (_sim.IsDeploying) _sim.State.Deploy(unit, destination, unit.AnchorFacing);
                else unit.Order = UnitOrder.MoveTo(destination, unit.AnchorFacing, run);
            }
            return;
        }

        Vector3 direction = along / length;
        Vector3 centre = start + along * 0.5f;

        // Perpendicular to the drawn line, pointing away from the selection.
        var normal = new Vector3(direction.Z, 0, -direction.X);
        Vector2 anchorOfFirst = SimBridge.Plane(chosen[0].Centre);
        if (normal.Dot(new Vector3(anchorOfFirst.X, 0, anchorOfFirst.Y) - centre) > 0) normal = -normal;

        FixVec2 facing = new FixVec2(Fix.FromDouble(normal.X), Fix.FromDouble(normal.Z)).Normalized;

        List<Unit> units = chosen;

        // One unit fills the line you drew. Several share it, in the order they already
        // stand, so a whole wing can be laid out in one gesture.
        float share = length / units.Count;

        for (int i = 0; i < units.Count; i++)
        {
            Unit unit = units[i];
            float span = units.Count == 1 ? length : share;

            unit.Width = Mathf.Clamp(
                Mathf.RoundToInt(span / Mathf.Max(unit.Type.FileSpacing.ToFloat(), 0.2f)),
                2, Math.Max(2, unit.Alive));
            unit.SlotsBuiltFor = -1;

            Vector3 at = units.Count == 1
                ? centre
                : start + direction * (share * (i + 0.5f));

            if (_sim.IsDeploying) _sim.State.Deploy(unit, SimBridge.ToSim(at), facing);
            else unit.Order = UnitOrder.MoveTo(SimBridge.ToSim(at), facing, run);
        }
    }

    private void BeginBattle()
    {
        if (!_sim.IsDeploying) return;

        _sim.BeginBattle();
        _deploymentMarker.Visible = false;
        GD.Print($"WAR — battle joined");
    }

    private Vector3? GroundUnder(Vector2 screenPosition)
    {
        Camera3D camera = _camera.Camera;
        return _terrain.Raycast(
            camera.ProjectRayOrigin(screenPosition),
            camera.ProjectRayNormal(screenPosition));
    }
}
