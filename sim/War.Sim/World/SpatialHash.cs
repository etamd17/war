using War.Sim.Core;

namespace War.Sim.World;

/// <summary>
/// A uniform-grid spatial index over soldier positions, rebuilt from scratch every tick.
///
/// Practically every interesting question in the simulation is a neighbour query — who
/// is close enough to hit, who am I bumping into, how many enemies are within thirty
/// metres of this unit — and at 2400 soldiers the naive answer is 2.9 million pairs per
/// tick. This turns it into a handful of cell lookups.
///
/// Rebuilding beats incremental updates here: soldiers move every tick anyway, and a
/// counting sort over a few thousand items is a couple of linear passes over contiguous
/// memory. It also allocates nothing after construction, which keeps the GC out of the
/// tick loop entirely.
///
/// Results come back in cell order and, within a cell, in insertion order — so query
/// results are deterministic, which matters because combat pairing depends on them.
/// </summary>
public sealed class SpatialHash
{
    private readonly int _dim;
    private readonly Fix _invCellSize;
    private readonly Fix _worldSize;

    private readonly int[] _cellStart;   // length _dim*_dim + 1, CSR-style offsets
    private readonly int[] _cellFill;    // scatter cursor, reused each build
    private int[] _items;                // soldier ids, grouped by cell
    private int _count;

    public Fix CellSize { get; }

    public SpatialHash(Fix worldSize, Fix cellSize, int initialCapacity = 4096)
    {
        _worldSize = worldSize;
        CellSize = cellSize;
        _invCellSize = Fix.One / cellSize;

        _dim = (worldSize * _invCellSize).FloorToInt + 1;
        if (_dim < 1) _dim = 1;

        _cellStart = new int[_dim * _dim + 1];
        _cellFill = new int[_dim * _dim];
        _items = new int[initialCapacity];
    }

    private int CellX(Fix x)
    {
        int c = (x * _invCellSize).FloorToInt;
        return c < 0 ? 0 : c >= _dim ? _dim - 1 : c;
    }

    private int CellY(Fix y) => CellX(y);

    // ------------------------------------------------------------------ build

    /// <summary>
    /// Indexes the given positions. <paramref name="ids"/> and <paramref name="positions"/>
    /// are parallel: the caller passes only the entries it wants indexed, which is how
    /// corpses stay out of the index instead of slowing every query down as the battle
    /// wears on.
    /// </summary>
    public void Build(ReadOnlySpan<int> ids, ReadOnlySpan<FixVec2> positions)
    {
        if (ids.Length != positions.Length)
            throw new ArgumentException("ids and positions must be parallel", nameof(positions));

        _count = ids.Length;
        if (_items.Length < _count)
        {
            int grown = _items.Length * 2;
            _items = new int[_count > grown ? _count : grown];
        }

        Array.Clear(_cellStart);

        // Pass 1 — count per cell. _cellStart[c + 1] holds the count for cell c so the
        // prefix sum in pass 2 can run in place.
        for (int i = 0; i < _count; i++)
        {
            int cell = CellY(positions[i].Y) * _dim + CellX(positions[i].X);
            _cellStart[cell + 1]++;
        }

        // Pass 2 — prefix sum into start offsets.
        for (int c = 0; c < _dim * _dim; c++)
        {
            _cellStart[c + 1] += _cellStart[c];
            _cellFill[c] = _cellStart[c];
        }

        // Pass 3 — scatter. Iterating ids in order means each cell's contents come out
        // in ascending id order, which is what makes queries reproducible.
        for (int i = 0; i < _count; i++)
        {
            int cell = CellY(positions[i].Y) * _dim + CellX(positions[i].X);
            _items[_cellFill[cell]++] = ids[i];
        }
    }

    // ------------------------------------------------------------------ query

    /// <summary>The ids sitting in one cell. Empty if the cell is out of range.</summary>
    public ReadOnlySpan<int> CellItems(int cellX, int cellY)
    {
        if (cellX < 0 || cellY < 0 || cellX >= _dim || cellY >= _dim)
            return ReadOnlySpan<int>.Empty;

        int cell = cellY * _dim + cellX;
        int start = _cellStart[cell];
        return _items.AsSpan(start, _cellStart[cell + 1] - start);
    }

    /// <summary>
    /// The inclusive cell rectangle covering a circle. Callers that want to avoid
    /// copying ids — the melee loop, mainly — walk this and use
    /// <see cref="CellItems"/> directly.
    /// </summary>
    public void CellRange(FixVec2 centre, Fix radius,
        out int minX, out int minY, out int maxX, out int maxY)
    {
        minX = CellX(centre.X - radius);
        maxX = CellX(centre.X + radius);
        minY = CellY(centre.Y - radius);
        maxY = CellY(centre.Y + radius);
    }

    /// <summary>
    /// Fills <paramref name="results"/> with ids within <paramref name="radius"/> of
    /// <paramref name="centre"/> and returns how many were written. Positions must be
    /// the same array that was indexed. Truncates silently if the buffer is too small —
    /// callers that care should size it for the worst case.
    /// </summary>
    public int Query(FixVec2 centre, Fix radius, FixVec2[] positions, Span<int> results)
    {
        CellRange(centre, radius, out int minX, out int minY, out int maxX, out int maxY);
        long radiusSqr = FixMath.SqrRaw(radius);
        int found = 0;

        for (int cy = minY; cy <= maxY; cy++)
        {
            for (int cx = minX; cx <= maxX; cx++)
            {
                foreach (int id in CellItems(cx, cy))
                {
                    if (FixVec2.SqrDistanceRaw(centre, positions[id]) > radiusSqr) continue;
                    if (found >= results.Length) return found;
                    results[found++] = id;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// The nearest indexed id to <paramref name="centre"/> within
    /// <paramref name="radius"/>, or −1. Searches outward a ring of cells at a time and
    /// stops as soon as no closer result is possible.
    /// </summary>
    public int FindNearest(FixVec2 centre, Fix radius, FixVec2[] positions, Func<int, bool>? filter = null)
    {
        int best = -1;
        long bestSqr = FixMath.SqrRaw(radius);

        int centreX = CellX(centre.X);
        int centreY = CellY(centre.Y);
        int maxRing = (radius * _invCellSize).FloorToInt + 1;

        for (int ring = 0; ring <= maxRing; ring++)
        {
            // Once we have a hit, any cell ring beyond its distance cannot improve on it.
            if (best >= 0)
            {
                Fix ringDistance = CellSize * (ring - 1);
                if (ringDistance > Fix.Zero && FixMath.SqrRaw(ringDistance) > bestSqr) break;
            }

            for (int cy = centreY - ring; cy <= centreY + ring; cy++)
            {
                for (int cx = centreX - ring; cx <= centreX + ring; cx++)
                {
                    // Only the perimeter of the ring is new.
                    bool onPerimeter = cx == centreX - ring || cx == centreX + ring ||
                                       cy == centreY - ring || cy == centreY + ring;
                    if (!onPerimeter) continue;

                    foreach (int id in CellItems(cx, cy))
                    {
                        if (filter != null && !filter(id)) continue;
                        long sqr = FixVec2.SqrDistanceRaw(centre, positions[id]);
                        if (sqr >= bestSqr) continue;
                        bestSqr = sqr;
                        best = id;
                    }
                }
            }
        }

        return best;
    }

    /// <summary>Number of ids currently indexed.</summary>
    public int Count => _count;

    /// <summary>Cells per side.</summary>
    public int Dimension => _dim;
}
