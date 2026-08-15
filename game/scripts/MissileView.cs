using Godot;
using War.Sim.Sim;
using War.Sim.Units;

namespace War.Game;

/// <summary>
/// Draws what is in the air.
///
/// The simulation already tracks every arrow, sling stone, javelin and pilum as an object
/// with an origin, a mark and a flight time — it must, because a shot is resolved against
/// whoever is standing at the impact point when it arrives rather than against the man it
/// was aimed at. So there is nothing to invent here: the shots exist, and this puts them
/// on screen.
///
/// Volleys are the most legible thing on an ancient battlefield. Seeing the arc leave your
/// archers and fall on a unit tells you at a glance what is shooting what, whether it is
/// landing, and when the ammunition runs out — all of which the simulation was already
/// modelling invisibly.
///
/// One MultiMesh for the lot, same as an army: a heavy exchange puts about eighty shots in
/// the air, and eighty draw calls for eighty darts would cost more than the three thousand
/// men they are being thrown at.
/// </summary>
public sealed partial class MissileView : Node3D
{
    /// <summary>Shots drawable at once. Heaviest observed volley is about eighty.</summary>
    private const int Pool = 256;

    private readonly float[] _buffer = new float[Pool * InstanceBuffer.Stride];
    private MultiMesh _multiMesh = null!;
    private TerrainView _terrain = null!;

    public void Build(TerrainView terrain)
    {
        _terrain = terrain;

        _multiMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
        };
        _multiMesh.Mesh = Dart();
        _multiMesh.InstanceCount = Pool;

        // Map-sized bounds, for the same reason as the armies: shots move every frame, and
        // letting Godot rederive the box from the instances each time is the expensive way
        // to arrive at "somewhere on the battlefield".
        float span = terrain.WorldSize;
        _multiMesh.CustomAabb = new Aabb(
            new Vector3(-64, -64, -64),
            new Vector3(span + 128, 512, span + 128));

        AddChild(new MultiMeshInstance3D
        {
            Name = "Shots",
            Multimesh = _multiMesh,
            MaterialOverride = MeshBuilder.Material(),
        });

        // Park every slot out of sight. Unused instances keep whatever was last written to
        // them, so without this the pool would show a cloud of darts at the origin.
        for (int i = 0; i < Pool; i++) Park(i);
        _multiMesh.Buffer = _buffer;
    }

    /// <summary>
    /// A dart pointing down its own local -Z, scaled per shot type.
    ///
    /// Drawn far longer and thicker than life: a real arrow is a few centimetres across
    /// and would be a single flickering pixel from the battle camera. Legibility beats
    /// scale accuracy here — the point is to read the volley, not to count the fletching.
    /// </summary>
    private static ArrayMesh Dart()
    {
        var builder = new MeshBuilder();
        var shaft = new Color(0.86f, 0.82f, 0.70f);
        var head = new Color(0.55f, 0.57f, 0.60f);

        builder.AddBox(new Vector3(0, 0, 0.12f), new Vector3(0.09f, 0.09f, 0.5f), shaft);
        builder.AddBox(new Vector3(0, 0, -0.42f), new Vector3(0.13f, 0.13f, 0.1f), head);

        return builder.Build();
    }

    /// <summary>How high a shot rises, as a fraction of how far it travels.</summary>
    private static float ArcOf(MissileType type) => type switch
    {
        MissileType.Bow => 0.13f,        // lobbed over the line in front
        MissileType.Sling => 0.09f,
        _ => 0.05f,                      // javelins and pila go flat and hard
    };

    /// <summary>Length in metres, drawn.</summary>
    private static float LengthOf(MissileType type) => type switch
    {
        MissileType.Sling => 1.2f,
        MissileType.Javelin => 4.0f,
        MissileType.Pilum => 4.6f,
        _ => 3.0f,
    };

    private static Color ColourOf(MissileType type) => type switch
    {
        MissileType.Sling => new Color(0.62f, 0.62f, 0.66f),
        MissileType.Javelin => new Color(0.80f, 0.72f, 0.52f),
        MissileType.Pilum => new Color(0.86f, 0.80f, 0.62f),
        _ => new Color(0.90f, 0.86f, 0.72f),
    };

    public void Update(BattleState state, float alpha)
    {
        int drawn = 0;

        for (int i = 0; i < state.MissileCount && drawn < Pool; i++)
        {
            ref Missile missile = ref state.Missiles[i];

            float flight = missile.FlightTicks.ToFloat();
            if (flight <= 0) continue;

            // Interpolated within the tick as well as between them, so a shot in the air
            // moves smoothly rather than stepping thirty times a second.
            float t = (missile.ElapsedTicks.ToFloat() + alpha) / flight;
            if (t < 0 || t > 1) continue;

            Vector2 from = SimBridge.Plane(missile.Origin);
            Vector2 to = SimBridge.Plane(missile.Target);
            float range = from.DistanceTo(to);
            if (range < 0.01f) continue;

            Vector2 flat = from.Lerp(to, t);
            float arc = range * ArcOf(missile.Type);

            // The ground rises and falls under the shot, so the path is the terrain
            // between the two ends with a parabola laid over it. Shooting downhill really
            // does carry further, and it is visible.
            float ground = Mathf.Lerp(
                _terrain.HeightAt(from.X, from.Y),
                _terrain.HeightAt(to.X, to.Y), t);

            float height = ground + 1.5f + 4f * arc * t * (1f - t);

            // Aim the dart along its own velocity. Horizontal is constant; vertical is the
            // slope of the parabola, which flips sign at the top of the arc — so shots
            // nose over and come down point first.
            float climb = 4f * arc * (1f - 2f * t);
            var heading = new Vector3(to.X - from.X, 0, to.Y - from.Y);

            Vector3 forward = (heading.Normalized() * range + new Vector3(0, climb, 0)).Normalized();
            Vector3 right = Vector3.Up.Cross(forward);
            if (right.LengthSquared() < 0.0001f) right = Vector3.Right;
            right = right.Normalized();

            var basis = new Basis(right, forward.Cross(right).Normalized(), forward)
                .Scaled(new Vector3(1, 1, LengthOf(missile.Type)));

            InstanceBuffer.Write(
                _buffer, drawn++, basis,
                new Vector3(flat.X, height, flat.Y),
                ColourOf(missile.Type));
        }

        // Only the slots that were live last frame need clearing; the rest are still
        // parked from whenever they last landed.
        for (int i = drawn; i < LastDrawn; i++) Park(i);

        LastDrawn = drawn;
        _multiMesh.Buffer = _buffer;
    }

    /// <summary>Shots drawn last frame, so clearing only touches slots that were used.</summary>
    public int LastDrawn { get; private set; }

    /// <summary>
    /// Hides one slot. Scaled to nothing rather than moved away: a zero basis collapses
    /// the triangles to a point, which costs no pixels and cannot show up as a speck on
    /// the horizon.
    /// </summary>
    private void Park(int slot) =>
        InstanceBuffer.Write(_buffer, slot, new Basis(Vector3.Zero, Vector3.Zero, Vector3.Zero),
            Vector3.Zero, new Color(0, 0, 0, 0));
}
