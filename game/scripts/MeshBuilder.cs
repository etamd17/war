using System.Collections.Generic;
using Godot;

namespace War.Game;

/// <summary>
/// Writes MultiMesh instance data as raw floats.
///
/// Godot stores each instance as a 3×4 row-major transform followed by a colour, and
/// handing it the whole buffer in one assignment costs a single marshalled call instead
/// of two per instance. On the armies that was the difference between 87 and 155 fps;
/// on missiles it matters because a volley puts several hundred objects in the air at
/// once and they all move every frame.
/// </summary>
public static class InstanceBuffer
{
    /// <summary>Twelve floats of transform plus four of colour.</summary>
    public const int Stride = 16;

    public static void Write(float[] buffer, int slot, Basis basis, Vector3 origin, Color colour)
    {
        int i = slot * Stride;

        buffer[i + 0] = basis.X.X; buffer[i + 1] = basis.Y.X; buffer[i + 2] = basis.Z.X; buffer[i + 3] = origin.X;
        buffer[i + 4] = basis.X.Y; buffer[i + 5] = basis.Y.Y; buffer[i + 6] = basis.Z.Y; buffer[i + 7] = origin.Y;
        buffer[i + 8] = basis.X.Z; buffer[i + 9] = basis.Y.Z; buffer[i + 10] = basis.Z.Z; buffer[i + 11] = origin.Z;

        buffer[i + 12] = colour.R;
        buffer[i + 13] = colour.G;
        buffer[i + 14] = colour.B;
        buffer[i + 15] = colour.A;
    }
}

/// <summary>
/// Assembles a mesh out of coloured boxes.
///
/// Every model in the game is built from this at load time — soldiers, horses,
/// elephants, trees, banners. Boxes are enough: at battle-camera distance what you read
/// is silhouette, colour, and motion, not geometry, and a unit's shape (shield forward,
/// spear up, rider on horse) survives being made of rectangles perfectly well.
///
/// The real reason to do it this way is that it removes the art pipeline from the
/// critical path entirely. There is nothing to import, nothing to keep in sync, and no
/// binary assets in the repository — and when real models do arrive they replace one
/// function rather than the whole renderer.
/// </summary>
public sealed class MeshBuilder
{
    private readonly List<Vector3> _vertices = new();
    private readonly List<Vector3> _normals = new();
    private readonly List<Color> _colours = new();
    private readonly List<int> _indices = new();

    private static readonly Vector3[] FaceNormals =
    [
        new(0, 0, 1), new(0, 0, -1),
        new(1, 0, 0), new(-1, 0, 0),
        new(0, 1, 0), new(0, -1, 0),
    ];

    /// <summary>Corner offsets per face, in units of the half-extent, wound counter-clockwise.</summary>
    private static readonly Vector3[][] FaceCorners =
    [
        [new(-1, -1, 1), new(1, -1, 1), new(1, 1, 1), new(-1, 1, 1)],       // +Z
        [new(1, -1, -1), new(-1, -1, -1), new(-1, 1, -1), new(1, 1, -1)],   // -Z
        [new(1, -1, 1), new(1, -1, -1), new(1, 1, -1), new(1, 1, 1)],       // +X
        [new(-1, -1, -1), new(-1, -1, 1), new(-1, 1, 1), new(-1, 1, -1)],   // -X
        [new(-1, 1, 1), new(1, 1, 1), new(1, 1, -1), new(-1, 1, -1)],       // +Y
        [new(-1, -1, -1), new(1, -1, -1), new(1, -1, 1), new(-1, -1, 1)],   // -Y
    ];

    /// <summary>Adds an axis-aligned box. <paramref name="size"/> is the full extent, not the half.</summary>
    public MeshBuilder AddBox(Vector3 centre, Vector3 size, Color colour)
    {
        Vector3 half = size * 0.5f;

        for (int face = 0; face < 6; face++)
        {
            int baseIndex = _vertices.Count;

            foreach (Vector3 corner in FaceCorners[face])
            {
                _vertices.Add(centre + corner * half);
                _normals.Add(FaceNormals[face]);
                _colours.Add(colour);
            }

            _indices.Add(baseIndex);
            _indices.Add(baseIndex + 1);
            _indices.Add(baseIndex + 2);
            _indices.Add(baseIndex);
            _indices.Add(baseIndex + 2);
            _indices.Add(baseIndex + 3);
        }

        return this;
    }

    /// <summary>A box rotated about the Y axis — for shields held at an angle, spears, and so on.</summary>
    public MeshBuilder AddRotatedBox(Vector3 centre, Vector3 size, float yawRadians, Color colour)
    {
        int start = _vertices.Count;
        AddBox(Vector3.Zero, size, colour);

        var rotation = new Basis(Vector3.Up, yawRadians);
        for (int i = start; i < _vertices.Count; i++)
        {
            _vertices[i] = centre + rotation * _vertices[i];
            _normals[i] = rotation * _normals[i];
        }

        return this;
    }

    /// <summary>A four-sided pyramid, used for tree canopies and spear points.</summary>
    public MeshBuilder AddCone(Vector3 baseCentre, float radius, float height, Color colour)
    {
        Vector3 apex = baseCentre + new Vector3(0, height, 0);
        Vector3[] rim =
        [
            baseCentre + new Vector3(-radius, 0, -radius),
            baseCentre + new Vector3(radius, 0, -radius),
            baseCentre + new Vector3(radius, 0, radius),
            baseCentre + new Vector3(-radius, 0, radius),
        ];

        for (int i = 0; i < 4; i++)
        {
            Vector3 a = rim[i];
            Vector3 b = rim[(i + 1) % 4];
            Vector3 normal = (b - a).Cross(apex - a).Normalized();

            int baseIndex = _vertices.Count;
            foreach (Vector3 v in new[] { a, b, apex })
            {
                _vertices.Add(v);
                _normals.Add(normal);
                _colours.Add(colour);
            }

            _indices.Add(baseIndex);
            _indices.Add(baseIndex + 1);
            _indices.Add(baseIndex + 2);
        }

        return this;
    }

    public ArrayMesh Build()
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = _vertices.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = _normals.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = _colours.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = _indices.ToArray();

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>The material every procedural mesh uses: vertex colours, no shine.</summary>
    public static StandardMaterial3D Material(bool unshaded = false)
    {
        return new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            Roughness = 0.95f,
            Metallic = 0.0f,
            SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
            ShadingMode = unshaded
                ? BaseMaterial3D.ShadingModeEnum.Unshaded
                : BaseMaterial3D.ShadingModeEnum.PerPixel,
            CullMode = BaseMaterial3D.CullModeEnum.Back,
        };
    }
}
