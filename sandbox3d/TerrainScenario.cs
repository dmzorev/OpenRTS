using OpenRTS.Content;
using OpenRTS.Core;
using OpenRTS.Core.Navigation;

internal sealed class TerrainScenario(UnitCatalog catalog)
{
    public const int Width = 24;
    public const int Depth = 16;
    public const int CellSize = WorldPosition.UnitsPerCell;
    public const int RampStartX = 7;
    public const int RampStartY = 10;
    public const int PlateauStartX = 15;
    public const int PlateauStartY = 4;
    public const int ReliefEndX = 23;
    public const int ReliefEndY = 14;
    public const int RampRise = CellSize / 4;
    public const int PlateauElevation = (PlateauStartX - RampStartX) * RampRise;
    private static readonly (string Type, float X, float Y)[] Starts =
    [
        ("scout", 5, 7.5f), ("scout", 5, 10), ("scout", 3, 8.5f),
        ("tank", 7.5f, 6), ("apc", 7.5f, 9), ("engineer", 6, 12), ("sniper", 3.5f, 11.5f),
        ("rocketeer", 2.75f, 5.25f)
    ];

    public static int UnitCount => Starts.Length + 1;
    public NavigationGrid Grid { get; } = CreateGrid();

    private static NavigationGrid CreateGrid()
    {
        var samples = new SurfaceSample[Width * Depth];
        var rises = new SurfaceRise[samples.Length];
        for (int y = PlateauStartY; y < ReliefEndY; y++)
            for (int x = RampStartX; x < ReliefEndX; x++)
            {
                int cell = y * Width + x;
                if (x >= PlateauStartX) samples[cell] = new(PlateauElevation);
                else if (y >= RampStartY)
                {
                    samples[cell] = new((x - RampStartX) * RampRise + RampRise / 2, Slope: RampRise);
                    rises[cell] = new(RampRise, 0);
                }
            }
        return new NavigationGrid(Width, Depth, surface: samples, rises: rises);
    }

    public SimulationWorld CreateWorld()
    {
        var world = new SimulationWorld(catalog.Units, Width * CellSize, Depth * CellSize,
            initialCapacity: UnitCount, navigationGrid: Grid,
            players: [new PlayerDefinition(1, "Sandbox"), new PlayerDefinition(2, "Target")]);
        try
        {
            var types = catalog.Units.Select((unit, index) => (unit.Id, Index: index)).ToDictionary(x => x.Id, x => x.Index);
            foreach (var (type, x, y) in Starts)
                world.AddInitialUnit(types[type], new WorldPosition((int)(x * CellSize), (int)(y * CellSize)), owner: 1);
            world.AddInitialUnit(types["tank"], new WorldPosition(11 * CellSize, 4 * CellSize), owner: 2);
            return world;
        }
        catch { world.Dispose(); throw; }
    }
}
