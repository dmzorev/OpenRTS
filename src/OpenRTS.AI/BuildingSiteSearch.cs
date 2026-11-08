using OpenRTS.Core;
using OpenRTS.Core.Navigation;

namespace OpenRTS.AI;

public readonly record struct BuildingSite(WorldPosition Position, int QuarterTurns);

/// <summary>Supplies ordered candidates. The planner applies the same placement checks to every strategy.</summary>
public interface IBuildingSiteSearch
{
    IEnumerable<BuildingSite> Candidates(NavigationGrid grid, WorldPosition center, UnitDefinition building);
}

public sealed class CompactBuildingSiteSearch : IBuildingSiteSearch
{
    public IEnumerable<BuildingSite> Candidates(NavigationGrid grid, WorldPosition center, UnitDefinition building)
    {
        int size = grid.CellSize;
        int cx = Math.Clamp(center.X / size, 0, grid.Width - 1);
        int cy = Math.Clamp(center.Y / size, 0, grid.Height - 1);
        int radius = Math.Max(Math.Max(cx, grid.Width - 1 - cx), Math.Max(cy, grid.Height - 1 - cy));
        var offsets = Enumerable.Range(0, 4).Select(turn =>
        {
            var footprint = building.WithRotation(turn).Footprint!;
            return (X: footprint.HalfWidth % size, Y: footprint.HalfHeight % size);
        }).ToArray();

        for (int ring = 0; ring <= radius; ring++)
        {
            int left = cx - ring, right = cx + ring, top = cy - ring, bottom = cy + ring;
            if (top >= 0)
                for (int x = Math.Max(0, left); x <= Math.Min(grid.Width - 1, right); x++)
                    for (int turn = 0; turn < 4; turn++)
                        yield return At(x, top, turn);
            if (bottom != top && bottom < grid.Height)
                for (int x = Math.Max(0, left); x <= Math.Min(grid.Width - 1, right); x++)
                    for (int turn = 0; turn < 4; turn++)
                        yield return At(x, bottom, turn);
            for (int y = Math.Max(0, top + 1); y <= Math.Min(grid.Height - 1, bottom - 1); y++)
            {
                if (left >= 0)
                    for (int turn = 0; turn < 4; turn++) yield return At(left, y, turn);
                if (right != left && right < grid.Width)
                    for (int turn = 0; turn < 4; turn++) yield return At(right, y, turn);
            }
        }

        BuildingSite At(int x, int y, int turn) => new(new(x * size + offsets[turn].X, y * size + offsets[turn].Y), turn);
    }
}
