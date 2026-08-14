using War.Sim.Core;

namespace War.Sim.World;

/// <summary>Parameters for a generated battlefield. Same settings plus same seed gives the same map, always.</summary>
public sealed class BattlefieldSettings
{
    /// <summary>Heightmap samples per side. Odd values put a sample exactly on the centre line.</summary>
    public int Resolution { get; init; } = 129;

    /// <summary>Battlefield extent in metres.</summary>
    public Fix Size { get; init; } = Fix.FromInt(1024);

    /// <summary>Vertical scale. Zero is a billiard table; two is genuinely broken ground.</summary>
    public Fix Hilliness { get; init; } = Fix.One;

    /// <summary>Fraction of the map under trees, roughly.</summary>
    public Fix ForestCoverage { get; init; } = Fix.Ratio(18, 100);

    /// <summary>Adds a central ridge running east to west — the classic "take the high ground" map.</summary>
    public bool CentralRidge { get; init; } = true;

    /// <summary>Cuts a river across the map with a single ford. Turns the battle into a crossing problem.</summary>
    public bool River { get; init; }

    public uint Seed { get; init; } = 1;
}

/// <summary>
/// Procedural battlefield generation: summed value-noise octaves for the base relief,
/// then optional set-piece features laid on top.
///
/// Everything here runs on <see cref="Fix"/> and <see cref="DetRandom"/>, so a map is
/// fully described by its settings and seed. Nothing needs to be stored or shipped.
/// </summary>
public static class TerrainGenerator
{
    public static Terrain Generate(BattlefieldSettings settings)
    {
        var terrain = new Terrain(settings.Resolution, settings.Size);
        var rng = new DetRandom(settings.Seed, RngStream.Terrain);

        int res = settings.Resolution;

        // Three octaves: broad relief, hills, then surface roughness.
        Fix[] octave1 = Lattice(5, rng);
        Fix[] octave2 = Lattice(9, rng);
        Fix[] octave3 = Lattice(17, rng);
        Fix[] forestNoise = Lattice(11, rng);

        Fix amplitude1 = Fix.FromInt(34) * settings.Hilliness;
        Fix amplitude2 = Fix.FromInt(13) * settings.Hilliness;
        Fix amplitude3 = Fix.FromInt(4) * settings.Hilliness;

        Fix forestThreshold = Fix.One - settings.ForestCoverage;

        for (int y = 0; y < res; y++)
        {
            Fix v = Fix.Ratio(y, res - 1);

            for (int x = 0; x < res; x++)
            {
                Fix u = Fix.Ratio(x, res - 1);

                Fix height =
                    Sample(octave1, 5, u, v) * amplitude1 +
                    Sample(octave2, 9, u, v) * amplitude2 +
                    Sample(octave3, 17, u, v) * amplitude3;

                if (settings.CentralRidge)
                    height += Ridge(v) * settings.Hilliness;

                terrain.SetHeight(x, y, height);

                // Forests grow where the noise is dense, but thin out on the ridge line
                // so the high ground stays worth contesting instead of being a thicket.
                Fix density = Sample(forestNoise, 11, u, v);
                Fix forest = density > forestThreshold
                    ? FixMath.Clamp01((density - forestThreshold) * 4)
                    : Fix.Zero;

                terrain.SetForest(x, y, (byte)(forest * 255).RoundToInt);
            }
        }

        if (settings.River) CarveRiver(terrain, settings, rng);

        terrain.RebuildGradients();
        AssignGroundTypes(terrain, settings);

        return terrain;
    }

    // ------------------------------------------------------------------ noise

    /// <summary>A square lattice of random values in [0, 1).</summary>
    private static Fix[] Lattice(int n, DetRandom rng)
    {
        var values = new Fix[n * n];
        for (int i = 0; i < values.Length; i++) values[i] = rng.NextFix();
        return values;
    }

    /// <summary>
    /// Samples a lattice at normalised coordinates with smoothstep interpolation.
    /// Smoothstep rather than linear because linear interpolation leaves visible
    /// creases along the lattice lines, and creases read as terracing on a hillside.
    /// </summary>
    private static Fix Sample(Fix[] lattice, int n, Fix u, Fix v)
    {
        Fix gx = u * (n - 1);
        Fix gy = v * (n - 1);

        int x0 = FixMath.Clamp(gx, Fix.Zero, Fix.FromInt(n - 1)).FloorToInt;
        int y0 = FixMath.Clamp(gy, Fix.Zero, Fix.FromInt(n - 1)).FloorToInt;
        if (x0 > n - 2) x0 = n - 2;
        if (y0 > n - 2) y0 = n - 2;
        if (x0 < 0) x0 = 0;
        if (y0 < 0) y0 = 0;

        Fix tx = SmoothStep(gx - Fix.FromInt(x0));
        Fix ty = SmoothStep(gy - Fix.FromInt(y0));

        Fix v00 = lattice[y0 * n + x0];
        Fix v10 = lattice[y0 * n + x0 + 1];
        Fix v01 = lattice[(y0 + 1) * n + x0];
        Fix v11 = lattice[(y0 + 1) * n + x0 + 1];

        Fix bottom = v00 + (v10 - v00) * tx;
        Fix top = v01 + (v11 - v01) * tx;
        return bottom + (top - bottom) * ty;
    }

    /// <summary>t² (3 − 2t): zero derivative at both ends, so patches join without creases.</summary>
    private static Fix SmoothStep(Fix t)
    {
        t = FixMath.Clamp01(t);
        return t * t * (Fix.FromInt(3) - t * 2);
    }

    // --------------------------------------------------------------- features

    /// <summary>
    /// A smooth east–west ridge across the middle of the map. Gives both sides an
    /// obvious objective and makes the deployment choice matter before a shot is fired.
    /// </summary>
    private static Fix Ridge(Fix v)
    {
        Fix offset = (v - Fix.Half) * 2;              // −1 at the south edge, +1 at the north
        Fix falloff = Fix.One - offset * offset;      // parabolic crest at the centre line
        return FixMath.Max(falloff, Fix.Zero) * Fix.FromInt(18);
    }

    /// <summary>
    /// Cuts a north-flowing river with exactly one ford. The ford is the whole point:
    /// it forces both commanders through a single choke and makes crossing under
    /// missile fire a decision rather than a formality.
    /// </summary>
    private static void CarveRiver(Terrain terrain, BattlefieldSettings settings, DetRandom rng)
    {
        int res = settings.Resolution;
        int fordRow = res / 2 + rng.NextInt(-res / 8, res / 8);
        Fix bankDepth = Fix.FromInt(6);

        for (int y = 0; y < res; y++)
        {
            // A gentle meander so it doesn't read as a canal.
            Fix wobble = FixMath.Sin(Fix.Ratio(y, res) * Fix.TwoPi) * 6;
            int centre = (res / 2) + (wobble * Fix.Ratio(res, 128)).RoundToInt;

            for (int x = 0; x < res; x++)
            {
                int distance = x - centre;
                if (distance < 0) distance = -distance;
                if (distance > 3) continue;

                Fix cut = bankDepth * Fix.Ratio(4 - distance, 4);
                terrain.SetHeight(x, y, terrain.GetHeight(x, y) - cut);
                terrain.SetForest(x, y, 0);

                bool isFord = y >= fordRow - 2 && y <= fordRow + 2;
                terrain.SetGround(x, y, isFord ? GroundType.Ford : GroundType.Mud);
            }
        }
    }

    /// <summary>
    /// Derives ground type from the shape of the land: steep faces are rock, hollows
    /// collect mud, everything else is grass. River tiles set during carving are left
    /// alone.
    /// </summary>
    private static void AssignGroundTypes(Terrain terrain, BattlefieldSettings settings)
    {
        int res = settings.Resolution;

        // Find the low ground so "hollow" means something relative to this map.
        Fix lowest = Fix.MaxValue;
        Fix highest = Fix.MinValue;
        for (int y = 0; y < res; y++)
        {
            for (int x = 0; x < res; x++)
            {
                Fix h = terrain.GetHeight(x, y);
                if (h < lowest) lowest = h;
                if (h > highest) highest = h;
            }
        }

        Fix range = FixMath.Max(highest - lowest, Fix.One);
        Fix mudLine = lowest + range * Fix.Ratio(12, 100);

        for (int y = 0; y < res; y++)
        {
            for (int x = 0; x < res; x++)
            {
                // Don't overwrite the riverbed.
                Fix cellX = Fix.FromInt(x) * terrain.CellSize;
                Fix cellY = Fix.FromInt(y) * terrain.CellSize;
                GroundType existing = terrain.GroundAt(new FixVec2(cellX, cellY));
                if (existing is GroundType.Ford or GroundType.Mud && settings.River) continue;

                Fix steepness = terrain.GradientAt(new FixVec2(cellX, cellY)).Magnitude;

                GroundType type =
                    steepness > Fix.Ratio(45, 100) ? GroundType.Rock :
                    terrain.GetHeight(x, y) < mudLine ? GroundType.Mud :
                    GroundType.Grass;

                terrain.SetGround(x, y, type);
            }
        }
    }
}
