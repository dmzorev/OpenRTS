namespace OpenRTS.Core.Navigation;

/// <summary>Reusable configuration-space samples. Terrain sampling does not choose the exact-mode route lattice.</summary>
internal sealed class RouteGeometry
{
    internal const int ExactStep = 512;
    internal NavigationGrid Terrain { get; }
    internal NavigationGrid Nodes { get; }
    internal Locomotor Profile { get; }
    internal bool Exact { get; }
    private byte[]? _clearance;
    internal RouteGeometry(NavigationGrid terrain, Locomotor profile, bool exact = false)
    {
        Terrain = terrain; Profile = profile; Exact = exact;
        if (!exact) { Nodes = terrain; return; }
        int width = terrain.Width * terrain.CellSize, height = terrain.Height * terrain.CellSize;
        // A fixed lattice is a bounded planner, not a complete continuous visibility graph.
        Nodes = new((width + ExactStep - 1) / ExactStep, (height + ExactStep - 1) / ExactStep, cellSize: ExactStep);
    }
    internal int Neighbor(int cell, int direction) => GridNeighbors.Neighbor(Nodes, cell, direction);
    internal WorldPosition Point(int cell) => Nodes.Center(cell);
    internal int Cell(WorldPosition point) => Nodes.CellAt(point);
    internal bool Open(int cell, CancellationToken token = default)
    {
        if (!Exact) return !Terrain.IsBlocked(cell) && Profile.At(Terrain, cell).Traverse;
        Prepare(token); return (_clearance![cell] & 1) != 0;
    }
    internal bool Edge(int a, int b, CancellationToken token = default)
    {
        if (b < 0 || !Open(a, token) || !Open(b, token)) return false;
        if (!Exact) return true;
        int d = b - a; int direction = d == -Nodes.Width ? 0 : d == -1 ? 1 : d == 1 ? 2 : 3;
        return (_clearance![a] & (2 << direction)) != 0;
    }
    internal long Cost(int a, int b) => !Exact ? TerrainCost.Edge(Terrain, Profile, a, b) : TerrainCost.Segment(Terrain, Profile, Point(a), Point(b));
    internal bool Stop(int cell) => Open(cell) && (Exact ? TerrainGeometry.CanStop(Terrain, Point(cell), Profile.Body, Profile) : Profile.At(Terrain, cell).CanStop);
    internal void Prepare(CancellationToken token)
    {
        if (!Exact || _clearance is not null) return;
        var cells = new byte[Nodes.CellCount];
        for (int i = 0; i < cells.Length; i++)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            var p = Point(i); if (TerrainGeometry.Clear(Terrain, p, p, Profile.Body, Profile)) cells[i] = 1;
        }
        for (int i = 0; i < cells.Length; i++)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            if ((cells[i] & 1) == 0) continue;
            for (int d = 2; d <= 3; d++)
            {
                int next = Neighbor(i, d); if (next < 0 || (cells[next] & 1) == 0) continue;
                if (!TerrainGeometry.Clear(Terrain, Point(i), Point(next), Profile.Body, Profile)) continue;
                cells[i] |= (byte)(2 << d); cells[next] |= (byte)(2 << (d == 2 ? 1 : 0));
            }
        }
        _clearance = cells;
    }
    internal int Anchor(WorldPosition point, CancellationToken token = default)
    {
        if (!Exact) return Cell(point);
        int root = Cell(point), best = -1; long distance = long.MaxValue;
        int reach = Math.Max(2, (Math.Max(Profile.Body.HalfWidth, Profile.Body.HalfHeight) + ExactStep - 1) / ExactStep + 1);
        for (int y = Math.Max(0, root / Nodes.Width - reach); y <= Math.Min(Nodes.Height - 1, root / Nodes.Width + reach); y++)
            for (int x = Math.Max(0, root % Nodes.Width - reach); x <= Math.Min(Nodes.Width - 1, root % Nodes.Width + reach); x++)
            {
                int cell = y * Nodes.Width + x; if (!Open(cell, token)) continue;
                var p = Point(cell); long dx = (long)p.X - point.X, dy = (long)p.Y - point.Y, squared = dx * dx + dy * dy;
                if (squared > distance || squared == distance && cell >= best || !TerrainGeometry.Clear(Terrain, point, p, Profile.Body, Profile)) continue;
                best = cell; distance = squared;
            }
        return best;
    }
}
