using War.Sim.Core;

namespace War.Sim.World;

/// <summary>
/// What a patch of ground is made of. Affects speed and how fast it tires men out.
/// </summary>
public enum GroundType : byte
{
    Grass = 0,
    Mud = 1,
    Rock = 2,
    Sand = 3,
    /// <summary>A shallow river crossing. Slow, exhausting, and lethal to cross under fire.</summary>
    Ford = 4,
    Road = 5,
}

/// <summary>
/// The battlefield: a heightmap, a forest density layer, and a ground-type layer,
/// sampled on a regular grid and bilinearly interpolated in between.
///
/// Terrain is not decoration. Height decides who is swinging uphill, forest decides
/// what you can see and whether your cavalry charge is worth anything, ground type
/// decides how exhausted your men are when they finally arrive, and all three together
/// are why the same two armies produce different battles on different maps.
///
/// Gradients are precomputed once at generation rather than sampled per query — every
/// soldier asks for the local slope every tick, and central differences on demand would
/// mean four extra height lookups per man per tick for no benefit.
/// </summary>
public sealed class Terrain
{
    /// <summary>Samples per side. Always odd so there is a sample exactly at the centre.</summary>
    public readonly int Resolution;

    /// <summary>Battlefield extent in metres. The world spans (0,0) to (Size, Size).</summary>
    public readonly Fix Size;

    public readonly Fix CellSize;
    private readonly Fix _invCellSize;

    private readonly Fix[] _height;
    private readonly FixVec2[] _gradient;
    private readonly byte[] _forest;
    private readonly byte[] _ground;

    public Terrain(int resolution, Fix size)
    {
        if (resolution < 2) throw new ArgumentOutOfRangeException(nameof(resolution));

        Resolution = resolution;
        Size = size;
        CellSize = size / (resolution - 1);
        _invCellSize = Fix.One / CellSize;

        int count = resolution * resolution;
        _height = new Fix[count];
        _gradient = new FixVec2[count];
        _forest = new byte[count];
        _ground = new byte[count];
    }

    private int Index(int x, int y) => y * Resolution + x;

    // ------------------------------------------------------------------ bounds

    public bool InBounds(FixVec2 position) =>
        position.X >= Fix.Zero && position.X <= Size &&
        position.Y >= Fix.Zero && position.Y <= Size;

    public FixVec2 ClampToBounds(FixVec2 position) => new(
        FixMath.Clamp(position.X, Fix.Zero, Size),
        FixMath.Clamp(position.Y, Fix.Zero, Size));

    /// <summary>
    /// How far outside the battlefield a position is. Routing units that get this far
    /// have quit the field and are removed.
    /// </summary>
    public Fix DistanceOutsideBounds(FixVec2 position)
    {
        Fix dx = FixMath.Max(-position.X, position.X - Size);
        Fix dy = FixMath.Max(-position.Y, position.Y - Size);
        return FixMath.Max(FixMath.Max(dx, dy), Fix.Zero);
    }

    // ----------------------------------------------------------------- sampling

    /// <summary>
    /// Resolves a world position to grid coordinates plus interpolation weights,
    /// clamped so a query just off the edge samples the edge rather than wrapping.
    /// </summary>
    private void Locate(FixVec2 position, out int x0, out int y0, out Fix tx, out Fix ty)
    {
        Fix gx = FixMath.Clamp(position.X * _invCellSize, Fix.Zero, Fix.FromInt(Resolution - 1));
        Fix gy = FixMath.Clamp(position.Y * _invCellSize, Fix.Zero, Fix.FromInt(Resolution - 1));

        x0 = gx.FloorToInt;
        y0 = gy.FloorToInt;

        if (x0 >= Resolution - 1) x0 = Resolution - 2;
        if (y0 >= Resolution - 1) y0 = Resolution - 2;
        if (x0 < 0) x0 = 0;
        if (y0 < 0) y0 = 0;

        tx = gx - Fix.FromInt(x0);
        ty = gy - Fix.FromInt(y0);
    }

    private static Fix Bilinear(Fix v00, Fix v10, Fix v01, Fix v11, Fix tx, Fix ty)
    {
        Fix bottom = v00 + (v10 - v00) * tx;
        Fix top = v01 + (v11 - v01) * tx;
        return bottom + (top - bottom) * ty;
    }

    /// <summary>Ground elevation in metres.</summary>
    public Fix HeightAt(FixVec2 position)
    {
        Locate(position, out int x0, out int y0, out Fix tx, out Fix ty);
        int i = Index(x0, y0);
        int j = i + Resolution;
        return Bilinear(_height[i], _height[i + 1], _height[j], _height[j + 1], tx, ty);
    }

    /// <summary>
    /// Rate of climb per metre travelled, as a vector. Dot it with a direction of
    /// travel to find out whether a soldier is going up or down, and how steeply.
    /// </summary>
    public FixVec2 GradientAt(FixVec2 position)
    {
        Locate(position, out int x0, out int y0, out Fix tx, out Fix ty);
        int i = Index(x0, y0);
        int j = i + Resolution;
        return new FixVec2(
            Bilinear(_gradient[i].X, _gradient[i + 1].X, _gradient[j].X, _gradient[j + 1].X, tx, ty),
            Bilinear(_gradient[i].Y, _gradient[i + 1].Y, _gradient[j].Y, _gradient[j + 1].Y, tx, ty));
    }

    /// <summary>Forest density in [0, 1]. Zero is open ground, one is dense woodland.</summary>
    public Fix ForestAt(FixVec2 position)
    {
        Locate(position, out int x0, out int y0, out Fix tx, out Fix ty);
        int i = Index(x0, y0);
        int j = i + Resolution;
        return Bilinear(
            Fix.Ratio(_forest[i], 255), Fix.Ratio(_forest[i + 1], 255),
            Fix.Ratio(_forest[j], 255), Fix.Ratio(_forest[j + 1], 255), tx, ty);
    }

    /// <summary>Ground type at a position. Nearest sample — ground type does not blend.</summary>
    public GroundType GroundAt(FixVec2 position)
    {
        Locate(position, out int x0, out int y0, out Fix tx, out Fix ty);
        int x = tx > Fix.Half ? x0 + 1 : x0;
        int y = ty > Fix.Half ? y0 + 1 : y0;
        return (GroundType)_ground[Index(x, y)];
    }

    // ------------------------------------------------------------- authoring

    public void SetHeight(int x, int y, Fix value) => _height[Index(x, y)] = value;
    public Fix GetHeight(int x, int y) => _height[Index(x, y)];
    public void SetForest(int x, int y, byte density) => _forest[Index(x, y)] = density;
    public byte GetForest(int x, int y) => _forest[Index(x, y)];
    public void SetGround(int x, int y, GroundType type) => _ground[Index(x, y)] = (byte)type;

    /// <summary>
    /// Recomputes the gradient field from the heightmap. Must be called after any
    /// edit to the heights, and is called for you by <see cref="TerrainGenerator"/>.
    /// </summary>
    public void RebuildGradients()
    {
        Fix twoCells = CellSize * 2;

        for (int y = 0; y < Resolution; y++)
        {
            for (int x = 0; x < Resolution; x++)
            {
                // Central differences, falling back to one-sided at the edges.
                int xm = x > 0 ? x - 1 : x;
                int xp = x < Resolution - 1 ? x + 1 : x;
                int ym = y > 0 ? y - 1 : y;
                int yp = y < Resolution - 1 ? y + 1 : y;

                Fix spanX = Fix.FromInt(xp - xm) * CellSize;
                Fix spanY = Fix.FromInt(yp - ym) * CellSize;
                if (spanX.IsZero) spanX = twoCells;
                if (spanY.IsZero) spanY = twoCells;

                _gradient[Index(x, y)] = new FixVec2(
                    (_height[Index(xp, y)] - _height[Index(xm, y)]) / spanX,
                    (_height[Index(x, yp)] - _height[Index(x, ym)]) / spanY);
            }
        }
    }

    // ------------------------------------------------------- movement effects

    private static readonly Fix SlopeSpeedFactor = Fix.Ratio(9, 10);
    private static readonly Fix MinSpeedMultiplier = Fix.Ratio(1, 4);
    private static readonly Fix MaxSpeedMultiplier = Fix.Ratio(23, 20);
    private static readonly Fix ForestSlow = Fix.Ratio(35, 100);

    /// <summary>
    /// Speed multiplier for moving through <paramref name="position"/> in
    /// <paramref name="direction"/>. Uphill costs, gentle downhill helps a little,
    /// forest and bad ground cost regardless of direction.
    /// </summary>
    public Fix SpeedMultiplierAt(FixVec2 position, FixVec2 direction)
    {
        Fix climb = FixVec2.Dot(GradientAt(position), direction);

        // Downhill gives less back than uphill takes away: you cannot recover a
        // charge by running down the far side of the hill you just climbed.
        Fix slope = climb > Fix.Zero
            ? Fix.One - climb * SlopeSpeedFactor
            : Fix.One - climb * (SlopeSpeedFactor / 3);

        Fix forest = Fix.One - ForestAt(position) * ForestSlow;

        Fix ground = GroundAt(position) switch
        {
            GroundType.Road => Fix.Ratio(115, 100),
            GroundType.Grass => Fix.One,
            GroundType.Rock => Fix.Ratio(95, 100),
            GroundType.Sand => Fix.Ratio(85, 100),
            GroundType.Mud => Fix.Ratio(65, 100),
            GroundType.Ford => Fix.Ratio(45, 100),
            _ => Fix.One,
        };

        return FixMath.Clamp(slope * forest * ground, MinSpeedMultiplier, MaxSpeedMultiplier);
    }

    /// <summary>
    /// How much faster than normal this ground tires a man moving through it.
    /// Climbing and wading are what actually exhaust an army before it ever fights.
    /// </summary>
    public Fix FatigueMultiplierAt(FixVec2 position, FixVec2 direction)
    {
        Fix climb = FixVec2.Dot(GradientAt(position), direction);
        Fix slope = climb > Fix.Zero ? Fix.One + climb * 2 : Fix.One;

        Fix ground = GroundAt(position) switch
        {
            GroundType.Road => Fix.Ratio(90, 100),
            GroundType.Mud => Fix.Ratio(170, 100),
            GroundType.Ford => Fix.Ratio(200, 100),
            GroundType.Sand => Fix.Ratio(130, 100),
            _ => Fix.One,
        };

        Fix forest = Fix.One + ForestAt(position) * Fix.Ratio(2, 10);
        return FixMath.Clamp(slope * ground * forest, Fix.Ratio(8, 10), Fix.FromInt(4));
    }

    // ------------------------------------------------------- line of sight

    /// <summary>
    /// Whether <paramref name="from"/> can see <paramref name="to"/>. Marches the line
    /// in fixed steps, accumulating forest occlusion and testing whether intervening
    /// ground rises above the sight line.
    ///
    /// This is what makes woods worth using: units inside them go unseen until they are
    /// close, so an ambush is a real tactic rather than a cosmetic one.
    /// </summary>
    public bool HasLineOfSight(FixVec2 from, FixVec2 to, Fix eyeHeight)
    {
        Fix distance = FixVec2.Distance(from, to);
        if (distance <= CellSize) return true;

        int steps = (distance * _invCellSize).FloorToInt;
        if (steps < 1) steps = 1;
        if (steps > 256) steps = 256;

        Fix startZ = HeightAt(from) + eyeHeight;
        Fix endZ = HeightAt(to) + eyeHeight;
        FixVec2 delta = to - from;

        Fix occlusion = Fix.Zero;
        Fix perStep = distance / steps;

        for (int i = 1; i < steps; i++)
        {
            Fix t = Fix.Ratio(i, steps);
            FixVec2 point = from + delta * t;

            // A ridge between the two blocks sight outright.
            Fix sightLine = startZ + (endZ - startZ) * t;
            if (HeightAt(point) > sightLine) return false;

            // Woodland blocks it gradually: a few metres of trees is a screen,
            // a hundred metres is a wall.
            occlusion += ForestAt(point) * perStep;
            if (occlusion > Fix.FromInt(18)) return false;
        }

        return true;
    }

    /// <summary>
    /// Height advantage of <paramref name="attacker"/> over <paramref name="defender"/>,
    /// in metres. Positive means the attacker is on the high ground.
    /// </summary>
    public Fix HeightAdvantage(FixVec2 attacker, FixVec2 defender) =>
        HeightAt(attacker) - HeightAt(defender);
}
