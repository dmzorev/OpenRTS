using System.Buffers;

namespace OpenRTS.Core;

internal static class GroupDestinations
{
    internal readonly record struct Member(EntityId Id, int Cell, int Index);

    internal static int[]? Slots(NavigationGrid grid, int anchor, int count)
    {
        if (count == 0) return [];
        if (grid.IsBlocked(anchor)) return null;
        int columns = Math.Min(grid.Width, (int)Math.Ceiling(Math.Sqrt(count)));
        int rows = (count + columns - 1) / columns;
        if (rows > grid.Height) { rows = grid.Height; columns = (count + rows - 1) / rows; }
        if (columns > grid.Width) return null;
        int left = Math.Clamp(anchor % grid.Width - (columns - 1) / 2, 0, grid.Width - columns);
        int top = Math.Clamp(anchor / grid.Width - (rows - 1) / 2, 0, grid.Height - rows);
        var slots = new int[count];
        bool clear = true;
        for (int i = 0; i < count; i++)
        {
            slots[i] = (top + i / columns) * grid.Width + left + i % columns;
            clear &= !grid.IsBlocked(slots[i]);
        }
        if (clear) return slots;
        // Terrain may deform the compact area; enumerate nearby connected cells instead of preserving a shape.
        int[] queue = ArrayPool<int>.Shared.Rent(grid.CellCount);
        uint[] visited = ArrayPool<uint>.Shared.Rent((grid.CellCount + 31) / 32);
        try
        {
            Array.Clear(visited, 0, (grid.CellCount + 31) / 32);
            int head = 0, tail = 1; queue[0] = anchor; visited[anchor >> 5] |= 1u << (anchor & 31);
            while (head < tail && tail < count)
            {
                int cell = queue[head++];
                for (int direction = 0; direction < 4 && tail < count; direction++)
                {
                    int next = LocalRouteSearch.Neighbor(grid, cell, direction);
                    if (next < 0 || grid.IsBlocked(next) || (visited[next >> 5] & 1u << (next & 31)) != 0) continue;
                    visited[next >> 5] |= 1u << (next & 31); queue[tail++] = next;
                }
            }
            if (tail < count) return null;
            queue.AsSpan(0, count).CopyTo(slots);
            return slots;
        }
        finally { ArrayPool<int>.Shared.Return(queue); ArrayPool<uint>.Shared.Return(visited); }
    }

    internal static void Assign(NavigationGrid grid, Member[] members, int[] slots, Action<int, int> assign)
    {
        var xMembers = Comparer<Member>.Create((a, b) => Compare(a.Cell, b.Cell, true, a.Id.Value, b.Id.Value));
        var yMembers = Comparer<Member>.Create((a, b) => Compare(a.Cell, b.Cell, false, a.Id.Value, b.Id.Value));
        var xSlots = Comparer<int>.Create((a, b) => Compare(a, b, true, (uint)a, (uint)b));
        var ySlots = Comparer<int>.Create((a, b) => Compare(a, b, false, (uint)a, (uint)b));
        Split(0, members.Length);
        void Split(int start, int count)
        {
            if (count == 0) return;
            if (count == 1) { assign(members[start].Index, slots[start]); return; }
            int minX = int.MaxValue, minY = int.MaxValue, maxX = 0, maxY = 0;
            for (int i = start; i < start + count; i++)
            {
                Bounds(members[i].Cell);
            }
            int width = maxX - minX, height = maxY - minY;
            minX = minY = int.MaxValue; maxX = maxY = 0;
            for (int i = start; i < start + count; i++) Bounds(slots[i]);
            bool x = Math.Max(width, maxX - minX) >= Math.Max(height, maxY - minY);
            Array.Sort(members, start, count, x ? xMembers : yMembers);
            Array.Sort(slots, start, count, x ? xSlots : ySlots);
            int half = count / 2; Split(start, half); Split(start + half, count - half);
            void Bounds(int cell)
            {
                minX = Math.Min(minX, cell % grid.Width); maxX = Math.Max(maxX, cell % grid.Width);
                minY = Math.Min(minY, cell / grid.Width); maxY = Math.Max(maxY, cell / grid.Width);
            }
        }
        int Compare(int a, int b, bool x, uint aTie, uint bTie)
        {
            int primary = x ? (a % grid.Width).CompareTo(b % grid.Width) : (a / grid.Width).CompareTo(b / grid.Width);
            if (primary != 0) return primary;
            int secondary = x ? (a / grid.Width).CompareTo(b / grid.Width) : (a % grid.Width).CompareTo(b % grid.Width);
            return secondary != 0 ? secondary : aTie.CompareTo(bTie);
        }
    }
}
