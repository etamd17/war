using Godot;
using War.Sim.Core;
using War.Sim.Sim;
using War.Sim.Units;
using War.Sim.World;

namespace War.Game;

/// <summary>
/// The only place that converts between the simulation's world and Godot's.
///
/// The simulation is a top-down plan view in fixed point: X east, Y north, elevation a
/// separate lookup. Godot is a 3D scene in floats. Everything crossing that boundary
/// goes through here, in one direction — the game reads simulation state and draws it,
/// and converts player clicks back into orders. No simulation value is ever written
/// from a float computed in the engine, because that is exactly how determinism dies.
///
/// Mapping: sim (x, y) becomes Godot (x, height, y). North is +Z.
/// </summary>
public static class SimBridge
{
    public static Vector2 Plane(FixVec2 p) => new(p.X.ToFloat(), p.Y.ToFloat());

    public static Vector3 World(FixVec2 p, float height) => new(p.X.ToFloat(), height, p.Y.ToFloat());

    /// <summary>Converts a point on the ground plane back into simulation coordinates.</summary>
    public static FixVec2 ToSim(Vector3 world) =>
        new(Fix.FromDouble(world.X), Fix.FromDouble(world.Z));

    /// <summary>A facing vector as a Godot direction on the ground plane.</summary>
    public static Vector3 Direction(FixVec2 facing) => new(facing.X.ToFloat(), 0, facing.Y.ToFloat());

    /// <summary>Builds a basis that points a model's +Z along the given facing.</summary>
    public static Basis Facing(FixVec2 facing)
    {
        Vector3 forward = Direction(facing);
        if (forward.LengthSquared() < 0.0001f) forward = Vector3.Forward;
        forward = forward.Normalized();

        Vector3 right = Vector3.Up.Cross(forward).Normalized();
        if (right.LengthSquared() < 0.0001f) right = Vector3.Right;

        return new Basis(right, Vector3.Up, forward);
    }

    // -------------------------------------------------------------- appearance

    public static Color FactionColour(Faction faction) => faction switch
    {
        Faction.Rome => new Color(0.72f, 0.13f, 0.13f),      // legionary red
        Faction.Carthage => new Color(0.45f, 0.20f, 0.55f),  // Tyrian purple
        Faction.Gaul => new Color(0.20f, 0.48f, 0.24f),      // green
        Faction.Greece => new Color(0.16f, 0.34f, 0.66f),    // blue
        Faction.Egypt => new Color(0.83f, 0.68f, 0.20f),     // gold
        _ => new Color(0.6f, 0.6f, 0.6f),
    };

    /// <summary>Colour used on the unit card and banner to show how a unit is holding up.</summary>
    public static Color MoraleColour(MoraleState state) => state switch
    {
        MoraleState.Steady => new Color(0.35f, 0.75f, 0.35f),
        MoraleState.Wavering => new Color(0.90f, 0.72f, 0.20f),
        MoraleState.Routing => new Color(0.85f, 0.22f, 0.22f),
        MoraleState.Rallying => new Color(0.45f, 0.62f, 0.85f),
        _ => Colors.White,
    };

    public static Color GroundColour(GroundType ground) => ground switch
    {
        GroundType.Grass => new Color(0.36f, 0.45f, 0.24f),
        GroundType.Mud => new Color(0.34f, 0.28f, 0.20f),
        GroundType.Rock => new Color(0.47f, 0.46f, 0.44f),
        GroundType.Sand => new Color(0.68f, 0.61f, 0.42f),
        GroundType.Ford => new Color(0.28f, 0.38f, 0.44f),
        GroundType.Road => new Color(0.52f, 0.47f, 0.38f),
        _ => new Color(0.36f, 0.45f, 0.24f),
    };
}
