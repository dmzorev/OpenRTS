namespace OpenRTS.Core.Navigation;

/// <summary>Prepares path costs for destination cells against an immutable grid revision.</summary>
public interface IGridPathfinder
{
    NavigationGrid Grid { get; }
    int LastBuildCount { get; }
    int LastVisitedCells { get; }
    double LastBuildMilliseconds { get; }

    void SetGrid(NavigationGrid grid);
    void Prepare(ReadOnlySpan<int> goals, CancellationToken cancellationToken = default);
    bool TryDistance(int goal, int cell, out int distance);
    void WriteState(BinaryWriter writer);
}
