namespace OpenRTS.Core.Navigation;

internal static class GroupDestinations
{
    internal readonly record struct Member(EntityId Id, WorldPosition Position, int Index);
    // Destination packing is in world units too; increasing terrain CellSize must not spread a crowd out.
    private const int Pitch = WorldPosition.UnitsPerCell;

    internal static WorldPosition[]? Slots(NavigationGrid grid, WorldPosition anchor, int count, IReadOnlyList<Locomotor>? profiles = null, Func<WorldPosition, bool>? allowed = null)
    {
        if (count == 0) return [];
        bool Clear(WorldPosition a, WorldPosition b) => profiles is null ? TerrainGeometry.Clear(grid, a, b, MotionIndex.BodyRadius) :
            profiles.All(p => TerrainGeometry.Clear(grid, a, b, MotionIndex.BodyRadius, p));
        bool Stop(WorldPosition p) => (profiles is null || profiles.All(profile => profile.At(grid, grid.CellAt(p)).CanStop)) && (allowed is null || allowed(p));
        if (!Clear(anchor, anchor) || !Stop(anchor)) return null;
        int width = grid.Width * grid.CellSize, height = grid.Height * grid.CellSize, radius = MotionIndex.BodyRadius;
        int maxColumns = (width - radius * 2) / Pitch + 1, maxRows = (height - radius * 2) / Pitch + 1;
        int columns = Math.Min(maxColumns, (int)Math.Ceiling(Math.Sqrt(count)));
        int rows = (count + columns - 1) / columns;
        if (rows > maxRows) { rows = maxRows; columns = (count + rows - 1) / rows; }
        if (columns > maxColumns) return null;
        int left = Fit(anchor.X - (columns - 1) / 2 * Pitch, columns, width);
        int top = Fit(anchor.Y - (rows - 1) / 2 * Pitch, rows, height);
        var slots = new WorldPosition[count]; bool clear = true;
        for (int i = 0; i < count; i++)
        {
            slots[i] = new(left + i % columns * Pitch, top + i / columns * Pitch);
            clear &= Clear(slots[i], slots[i]) && Stop(slots[i]);
        }
        if (clear) return slots;
        // Deform only the destination area near terrain, not the physical shape of the units.
        var queue = new Queue<WorldPosition>(); var visited = new HashSet<WorldPosition> { anchor with { Elevation = 0 } };
        queue.Enqueue(anchor with { Elevation = 0 });
        int filled = 0;
        while (queue.Count > 0 && filled < count)
        {
            var point = queue.Dequeue(); if (Stop(point)) slots[filled++] = point;
            for (int direction = 0; direction < 4; direction++)
            {
                var next = new WorldPosition(point.X + (direction == 1 ? -Pitch : direction == 2 ? Pitch : 0),
                    point.Y + (direction == 0 ? -Pitch : direction == 3 ? Pitch : 0));
                if (!visited.Add(next) || !Clear(point, next)) continue;
                queue.Enqueue(next);
            }
        }
        return filled == count ? slots : null;
        int Fit(int start, int length, int extent)
        {
            if (start < radius) start += ((radius - start + Pitch - 1) / Pitch) * Pitch;
            long overflow = (long)start + (length - 1L) * Pitch - (extent - radius);
            if (overflow > 0) start -= checked((int)((overflow + Pitch - 1) / Pitch * Pitch));
            return start;
        }
    }

    internal static void Assign(Member[] members, WorldPosition[] slots, Action<int, WorldPosition> assign)
    {
        var xMembers = Comparer<Member>.Create((a, b) => Compare(a.Position, b.Position, true, a.Id.Value, b.Id.Value));
        var yMembers = Comparer<Member>.Create((a, b) => Compare(a.Position, b.Position, false, a.Id.Value, b.Id.Value));
        var xSlots = Comparer<WorldPosition>.Create((a, b) => Compare(a, b, true, 0, 0));
        var ySlots = Comparer<WorldPosition>.Create((a, b) => Compare(a, b, false, 0, 0));
        Split(0, members.Length);
        void Split(int start, int count)
        {
            if (count == 0) return;
            if (count == 1) { assign(members[start].Index, slots[start]); return; }
            int minX = int.MaxValue, minY = int.MaxValue, maxX = 0, maxY = 0;
            for (int i = start; i < start + count; i++)
            {
                Bounds(members[i].Position);
            }
            int width = maxX - minX, height = maxY - minY;
            minX = minY = int.MaxValue; maxX = maxY = 0;
            for (int i = start; i < start + count; i++) Bounds(slots[i]);
            bool x = Math.Max(width, maxX - minX) >= Math.Max(height, maxY - minY);
            Array.Sort(members, start, count, x ? xMembers : yMembers);
            Array.Sort(slots, start, count, x ? xSlots : ySlots);
            int half = count / 2; Split(start, half); Split(start + half, count - half);
            void Bounds(WorldPosition cell)
            {
                minX = Math.Min(minX, cell.X); maxX = Math.Max(maxX, cell.X);
                minY = Math.Min(minY, cell.Y); maxY = Math.Max(maxY, cell.Y);
            }
        }
        int Compare(WorldPosition a, WorldPosition b, bool x, uint aTie, uint bTie)
        {
            int primary = x ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y);
            if (primary != 0) return primary;
            int secondary = x ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X);
            return secondary != 0 ? secondary : aTie.CompareTo(bTie);
        }
    }
}
