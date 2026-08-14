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

    private static double StepSeconds => 1.0 / SimConstants.TickRate;

    public override void _Ready()
    {
        BuildBattle();
        BuildWorld();
        BuildHud();
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
            Separation = Fix.FromInt(320),
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
                AmbientLightSource = Godot.Environment.AmbientSource.Sky,
                AmbientLightEnergy = 0.65f,
                FogEnabled = true,
                FogDensity = 0.0012f,
                FogLightColor = new Color(0.66f, 0.71f, 0.76f),
            },
        });
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
        _hud.Map.Clicked += world => _camera.LookAtGround(new Vector3(world.X, 0, world.Y));
    }

    // -------------------------------------------------------------------- loop

    public override void _Process(double delta)
    {
        StepSimulation(delta);

        float alpha = (float)Mathf.Clamp(_accumulator / StepSeconds, 0, 1);
        _armies.Update(_sim.State, alpha, _selected);
        _hud.Refresh(_selected);
    }

    private void StepSimulation(double delta)
    {
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

        Vector3 along = end - start;
        along.Y = 0;
        float length = along.Length();

        bool run = Input.IsKeyPressed(Key.Shift);

        if (length < 5f)
        {
            foreach (Unit unit in Selected())
            {
                FixVec2 destination = SimBridge.ToSim(start);
                unit.Order = UnitOrder.MoveTo(destination, unit.AnchorFacing, run);
            }
            return;
        }

        Vector3 direction = along / length;
        Vector3 centre = start + along * 0.5f;

        foreach (Unit unit in Selected())
        {
            // Perpendicular to the drawn line, pointing away from where the unit is now.
            var normal = new Vector3(direction.Z, 0, -direction.X);
            Vector2 here = SimBridge.Plane(unit.Centre);
            Vector3 toUnit = new Vector3(here.X, 0, here.Y) - centre;
            if (normal.Dot(toUnit) > 0) normal = -normal;

            unit.Width = Mathf.Clamp(
                Mathf.RoundToInt(length / Mathf.Max(unit.Type.FileSpacing.ToFloat(), 0.2f)),
                2, Math.Max(2, unit.Alive));
            unit.SlotsBuiltFor = -1;

            unit.Order = UnitOrder.MoveTo(
                SimBridge.ToSim(centre),
                new FixVec2(Fix.FromDouble(normal.X), Fix.FromDouble(normal.Z)).Normalized,
                run);
        }
    }

    private Vector3? GroundUnder(Vector2 screenPosition)
    {
        Camera3D camera = _camera.Camera;
        return _terrain.Raycast(
            camera.ProjectRayOrigin(screenPosition),
            camera.ProjectRayNormal(screenPosition));
    }
}
