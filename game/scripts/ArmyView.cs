using System.Collections.Generic;
using Godot;
using War.Sim.Core;
using War.Sim.Sim;

namespace War.Game;

/// <summary>
/// Draws the armies.
///
/// One <see cref="MultiMesh"/> per unit, one instance per soldier — around forty draw
/// calls for three and a half thousand men. Transforms come from the simulation's last
/// two ticks blended together, so the men move smoothly at whatever the monitor runs at
/// while the simulation stays locked to its 30 Hz.
///
/// Corpses stay on the field. They are written once, when the man falls, and then left
/// alone — so the cost of drawing an army goes <em>down</em> as the battle wears on
/// rather than up, and the ground fills with the shape of what happened.
/// </summary>
public sealed partial class ArmyView : Node3D
{
    private sealed class UnitView
    {
        public required Unit Unit;
        public required MultiMeshInstance3D Soldiers;
        public required MeshInstance3D Banner;
        public required MeshInstance3D Pennant;
        public required StandardMaterial3D PennantMaterial;
        public required MeshInstance3D Ring;

        /// <summary>Soldiers already laid out as corpses, so they are never touched again.</summary>
        public required bool[] Settled;

        public Vector3 BannerAt;
    }

    private static readonly Color Living = Colors.White;
    private static readonly Color Fallen = new(0.42f, 0.36f, 0.34f);

    private readonly List<UnitView> _views = new();
    private TerrainView _terrain = null!;

    public void Build(BattleState state, TerrainView terrain)
    {
        _terrain = terrain;

        StandardMaterial3D material = MeshBuilder.Material();

        foreach (Unit unit in state.Units)
        {
            var multiMesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = true,
            };
            // Mesh and instance count must be assigned after the format flags above,
            // or the buffer is allocated for the wrong stride.
            multiMesh.Mesh = SoldierModels.For(unit.Type);
            multiMesh.InstanceCount = unit.Strength;

            var soldiers = new MultiMeshInstance3D
            {
                Name = $"Unit{unit.Id}_{unit.Type.Id}",
                Multimesh = multiMesh,
                MaterialOverride = material,
            };
            AddChild(soldiers);

            Color faction = SimBridge.FactionColour(unit.Faction);

            var banner = new MeshInstance3D
            {
                Name = $"Banner{unit.Id}",
                Mesh = SoldierModels.Banner(faction),
                MaterialOverride = material,
            };
            AddChild(banner);

            // A small flag above the standard, recoloured every frame to show how the
            // unit is holding up. It gets its own solid-colour material because the
            // shared one draws vertex colours, which are baked and cannot change.
            var pennantMaterial = new StandardMaterial3D
            {
                AlbedoColor = SimBridge.MoraleColour(MoraleState.Steady),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            };

            var pennant = new MeshInstance3D
            {
                Name = $"Pennant{unit.Id}",
                Mesh = new BoxMesh { Size = new Vector3(0.7f, 0.3f, 0.08f) },
                MaterialOverride = pennantMaterial,
                Position = new Vector3(0.4f, 3.9f, 0),
            };
            banner.AddChild(pennant);

            var ring = new MeshInstance3D
            {
                Name = $"Ring{unit.Id}",
                Mesh = SelectionRing(),
                MaterialOverride = MeshBuilder.Material(unshaded: true),
                Visible = false,
            };
            AddChild(ring);

            // Seed the standard's position from the unit's actual centre. Left at zero it
            // would sit at the map origin for the first frame, and rectangle selection —
            // which tests against this point — would miss every unit on that frame.
            Vector2 centre = SimBridge.Plane(unit.Centre);

            _views.Add(new UnitView
            {
                Unit = unit,
                Soldiers = soldiers,
                Banner = banner,
                Pennant = pennant,
                PennantMaterial = pennantMaterial,
                Ring = ring,
                Settled = new bool[unit.Strength],
                BannerAt = new Vector3(centre.X, terrain.HeightAt(centre.X, centre.Y), centre.Y),
            });
        }
    }

    /// <summary>
    /// Writes one frame. <paramref name="alpha"/> is how far the render clock has run
    /// past the last simulation tick, in the range [0, 1].
    /// </summary>
    public void Update(BattleState state, float alpha, HashSet<int> selected)
    {
        foreach (UnitView view in _views)
        {
            Unit unit = view.Unit;

            if (unit.Withdrawn)
            {
                view.Banner.Visible = false;
                view.Ring.Visible = false;
                continue;
            }

            UpdateSoldiers(state, view, alpha);
            UpdateBanner(state, view, selected.Contains(unit.Id));
        }
    }

    private void UpdateSoldiers(BattleState state, UnitView view, float alpha)
    {
        Unit unit = view.Unit;
        MultiMesh mesh = view.Soldiers.Multimesh;

        for (int s = unit.FirstSoldier; s < unit.EndSoldier; s++)
        {
            int slot = s - unit.FirstSoldier;
            if (view.Settled[slot]) continue;

            bool dead = state.State[s] == SoldierState.Dead;

            Vector2 previous = SimBridge.Plane(state.PreviousPosition[s]);
            Vector2 current = SimBridge.Plane(state.Position[s]);
            Vector2 at = dead ? current : previous.Lerp(current, alpha);

            float ground = _terrain.HeightAt(at.X, at.Y);
            Basis facing = SimBridge.Facing(state.Facing[s]);

            if (dead)
            {
                // Tip the model onto its face and leave it there for good.
                Basis fallen = facing * new Basis(Vector3.Right, Mathf.Pi * 0.5f);
                mesh.SetInstanceTransform(slot, new Transform3D(fallen, new Vector3(at.X, ground + 0.08f, at.Y)));
                mesh.SetInstanceColor(slot, Fallen);
                view.Settled[slot] = true;
                continue;
            }

            mesh.SetInstanceTransform(slot, new Transform3D(facing, new Vector3(at.X, ground, at.Y)));
            mesh.SetInstanceColor(slot, Living);
        }
    }

    private void UpdateBanner(BattleState state, UnitView view, bool isSelected)
    {
        Unit unit = view.Unit;

        if (unit.Alive == 0)
        {
            view.Banner.Visible = false;
            view.Ring.Visible = false;
            return;
        }

        Vector2 centre = SimBridge.Plane(unit.Centre);
        var target = new Vector3(centre.X, _terrain.HeightAt(centre.X, centre.Y), centre.Y);

        // The unit centre only moves once per simulation tick and jumps when men die.
        // Easing the standard toward it keeps the banner from twitching.
        view.BannerAt = view.BannerAt.Lerp(target, 0.15f);

        view.Banner.Visible = true;
        view.Banner.Position = view.BannerAt;

        // The pennant shows how the unit is holding up, so the state of the whole line is
        // legible from the battle camera without opening a single unit card.
        view.PennantMaterial.AlbedoColor = SimBridge.MoraleColour(unit.MoraleState);

        view.Ring.Visible = isSelected;
        if (!isSelected) return;

        float radius = Mathf.Max(unit.HalfFrontage.ToFloat(), 3f) + 1.5f;
        view.Ring.Position = view.BannerAt + new Vector3(0, 0.15f, 0);
        view.Ring.Scale = new Vector3(radius, 1, radius);
    }

    /// <summary>
    /// A flat ring of short segments in the ground plane. Built by hand rather than from
    /// a TorusMesh so its orientation is unambiguous and it sits flush on the ground.
    /// </summary>
    private static ArrayMesh SelectionRing()
    {
        var builder = new MeshBuilder();
        var colour = new Color(1f, 0.94f, 0.55f);
        const int segments = 40;

        for (int i = 0; i < segments; i++)
        {
            float angle = i / (float)segments * Mathf.Tau;
            var at = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
            builder.AddRotatedBox(at, new Vector3(0.16f, 0.05f, 0.10f), -angle, colour);
        }

        return builder.Build();
    }

    /// <summary>The unit whose men are nearest a world point, for click selection.</summary>
    public Unit? UnitNear(BattleState state, Vector3 point, float radius)
    {
        Unit? best = null;
        float bestDistance = radius * radius;

        foreach (UnitView view in _views)
        {
            Unit unit = view.Unit;
            if (unit.IsOutOfAction || unit.Alive == 0) continue;

            Vector2 centre = SimBridge.Plane(unit.Centre);
            var flat = new Vector2(point.X, point.Z);

            // Measured against the unit's footprint, not a fixed radius, so a wide line
            // is as clickable as a compact block.
            float reach = Mathf.Max(unit.HalfFrontage.ToFloat(), unit.HalfDepth.ToFloat()) + 4f;
            float distance = flat.DistanceSquaredTo(centre) - reach * reach;

            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = unit;
        }

        return best;
    }

    /// <summary>Every unit of an army whose centre falls inside a screen-space rectangle.</summary>
    public IEnumerable<Unit> UnitsInRectangle(Camera3D camera, Rect2 screenRect, int armyId)
    {
        foreach (UnitView view in _views)
        {
            Unit unit = view.Unit;
            if (unit.ArmyId != armyId || unit.IsOutOfAction || unit.Alive == 0) continue;
            if (camera.IsPositionBehind(view.BannerAt)) continue;

            Vector2 onScreen = camera.UnprojectPosition(view.BannerAt);
            if (screenRect.HasPoint(onScreen)) yield return unit;
        }
    }
}
