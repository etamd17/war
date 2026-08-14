using Godot;
using War.Sim.Core;
using War.Sim.World;

namespace War.Game;

/// <summary>
/// Draws the battlefield, and answers the one question the rest of the presentation
/// layer asks constantly: how high is the ground under this point?
///
/// The simulation's terrain is fixed point and sampled through <see cref="Fix"/>
/// arithmetic. Asking it for elevation once per soldier per frame — a few thousand
/// times at sixty frames a second — would put fixed-point maths on the render path for
/// no benefit, since nothing the renderer computes may flow back into the simulation.
/// So the heights are copied into a plain float grid once at load and sampled from
/// there. Same numbers, none of the cost, and no risk of a float leaking backwards.
/// </summary>
public sealed partial class TerrainView : Node3D
{
    private float[] _heights = [];
    private int _resolution;
    private float _cellSize;
    private float _worldSize;

    public float WorldSize => _worldSize;

    public void Build(Terrain terrain)
    {
        _resolution = terrain.Resolution;
        _cellSize = terrain.CellSize.ToFloat();
        _worldSize = terrain.Size.ToFloat();
        _heights = new float[_resolution * _resolution];

        for (int y = 0; y < _resolution; y++)
            for (int x = 0; x < _resolution; x++)
                _heights[y * _resolution + x] = terrain.GetHeight(x, y).ToFloat();

        AddChild(BuildGround(terrain));
        AddChild(BuildTrees(terrain));
    }

    // ------------------------------------------------------------------ ground

    private MeshInstance3D BuildGround(Terrain terrain)
    {
        int res = _resolution;
        var vertices = new Vector3[res * res];
        var normals = new Vector3[res * res];
        var colours = new Color[res * res];

        for (int y = 0; y < res; y++)
        {
            for (int x = 0; x < res; x++)
            {
                int i = y * res + x;
                float wx = x * _cellSize;
                float wz = y * _cellSize;

                vertices[i] = new Vector3(wx, _heights[i], wz);

                // Normals from central differences on the height grid. Cheap, and it
                // gives the hillsides the shading that makes the ground readable at all.
                float dx = SampleGrid(x + 1, y) - SampleGrid(x - 1, y);
                float dz = SampleGrid(x, y + 1) - SampleGrid(x, y - 1);
                normals[i] = new Vector3(-dx, 2f * _cellSize, -dz).Normalized();

                var at = new FixVec2(Fix.FromDouble(wx), Fix.FromDouble(wz));
                Color colour = SimBridge.GroundColour(terrain.GroundAt(at));

                // Woodland floor is darker, which reads as canopy shadow from above and
                // makes forested ground obvious before you have flown the camera into it.
                float forest = terrain.GetForest(x, y) / 255f;
                colours[i] = colour.Lerp(new Color(0.14f, 0.22f, 0.13f), forest * 0.75f);
            }
        }

        var indices = new int[(res - 1) * (res - 1) * 6];
        int cursor = 0;
        for (int y = 0; y < res - 1; y++)
        {
            for (int x = 0; x < res - 1; x++)
            {
                int a = y * res + x;
                int b = a + 1;
                int c = a + res;
                int d = c + 1;

                indices[cursor++] = a;
                indices[cursor++] = c;
                indices[cursor++] = b;
                indices[cursor++] = b;
                indices[cursor++] = c;
                indices[cursor++] = d;
            }
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Color] = colours;
        arrays[(int)Mesh.ArrayType.Index] = indices;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        return new MeshInstance3D
        {
            Name = "Ground",
            Mesh = mesh,
            MaterialOverride = MeshBuilder.Material(),
        };
    }

    private float SampleGrid(int x, int y)
    {
        x = Mathf.Clamp(x, 0, _resolution - 1);
        y = Mathf.Clamp(y, 0, _resolution - 1);
        return _heights[y * _resolution + x];
    }

    // ------------------------------------------------------------------- trees

    /// <summary>
    /// Trees as a single MultiMesh. Forest matters mechanically — it hides units, breaks
    /// formations and stops arrows — so it has to be visible from the battle camera at a
    /// glance, and it has to cost nothing to draw.
    /// </summary>
    private MultiMeshInstance3D BuildTrees(Terrain terrain)
    {
        var trunk = new Color(0.28f, 0.20f, 0.13f);
        var canopy = new Color(0.16f, 0.30f, 0.15f);

        ArrayMesh treeMesh = new MeshBuilder()
            .AddBox(new Vector3(0, 1.6f, 0), new Vector3(0.5f, 3.2f, 0.5f), trunk)
            .AddCone(new Vector3(0, 2.6f, 0), 2.4f, 5.2f, canopy)
            .AddCone(new Vector3(0, 4.6f, 0), 1.6f, 3.4f, canopy)
            .Build();

        var placements = new System.Collections.Generic.List<Transform3D>();

        // One candidate per grid cell, accepted in proportion to forest density. The
        // jitter is derived from the cell index so the wood looks the same every run.
        for (int y = 0; y < _resolution; y++)
        {
            for (int x = 0; x < _resolution; x++)
            {
                float density = terrain.GetForest(x, y) / 255f;
                if (density < 0.15f) continue;

                uint hash = (uint)(x * 73856093 ^ y * 19349663);
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    hash = hash * 1664525u + 1013904223u;
                    if ((hash >> 8 & 0xFF) / 255f > density) continue;

                    float jx = ((hash >> 16 & 0xFF) / 255f - 0.5f) * _cellSize;
                    float jz = ((hash >> 24 & 0xFF) / 255f - 0.5f) * _cellSize;

                    float wx = x * _cellSize + jx;
                    float wz = y * _cellSize + jz;
                    float scale = 0.75f + (hash >> 4 & 0x3F) / 63f * 0.6f;

                    var basis = new Basis(Vector3.Up, (hash & 0xFF) / 255f * Mathf.Tau).Scaled(Vector3.One * scale);
                    placements.Add(new Transform3D(basis, new Vector3(wx, HeightAt(wx, wz), wz)));
                }
            }
        }

        var multiMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = treeMesh,
            InstanceCount = placements.Count,
        };

        for (int i = 0; i < placements.Count; i++)
            multiMesh.SetInstanceTransform(i, placements[i]);

        return new MultiMeshInstance3D
        {
            Name = "Trees",
            Multimesh = multiMesh,
            MaterialOverride = MeshBuilder.Material(),
        };
    }

    // ---------------------------------------------------------------- sampling

    /// <summary>Ground elevation at a world position, bilinearly interpolated.</summary>
    public float HeightAt(float x, float z)
    {
        if (_heights.Length == 0) return 0;

        float gx = Mathf.Clamp(x / _cellSize, 0, _resolution - 1.001f);
        float gz = Mathf.Clamp(z / _cellSize, 0, _resolution - 1.001f);

        int x0 = (int)gx;
        int z0 = (int)gz;
        float tx = gx - x0;
        float tz = gz - z0;

        float h00 = SampleGrid(x0, z0);
        float h10 = SampleGrid(x0 + 1, z0);
        float h01 = SampleGrid(x0, z0 + 1);
        float h11 = SampleGrid(x0 + 1, z0 + 1);

        return Mathf.Lerp(Mathf.Lerp(h00, h10, tx), Mathf.Lerp(h01, h11, tx), tz);
    }

    /// <summary>
    /// Where a ray meets the ground, or null if it never does. Used for every click:
    /// marching the ray in short steps and refining the crossing is both simpler and
    /// more robust on a heightmap than building collision geometry for it.
    /// </summary>
    public Vector3? Raycast(Vector3 origin, Vector3 direction, float maxDistance = 4000f)
    {
        const float step = 2.0f;
        float travelled = 0;
        Vector3 previous = origin;
        float previousGap = origin.Y - HeightAt(origin.X, origin.Z);

        while (travelled < maxDistance)
        {
            travelled += step;
            Vector3 point = origin + direction * travelled;
            float gap = point.Y - HeightAt(point.X, point.Z);

            if (gap <= 0 && previousGap > 0)
            {
                // Bisect the crossing a few times; two metres of step is far more
                // precision than a mouse click needs after this.
                Vector3 lo = previous, hi = point;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 mid = (lo + hi) * 0.5f;
                    if (mid.Y - HeightAt(mid.X, mid.Z) > 0) lo = mid;
                    else hi = mid;
                }
                return (lo + hi) * 0.5f;
            }

            previous = point;
            previousGap = gap;
        }

        return null;
    }
}
