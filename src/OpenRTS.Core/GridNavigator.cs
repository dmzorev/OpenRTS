using System.Buffers;
using System.Diagnostics;

namespace OpenRTS.Core;

/// <summary>Shared reverse-BFS fields with a deterministic work budget and ordered publication.</summary>
public sealed class GridNavigator
{
    private sealed class Field
    {
        public int Goal = -1;
        public int[] Distances = [];
        public int Visited;
    }

    private readonly Field[] _fields;
    private readonly Dictionary<int, int> _cached = new();
    private readonly HashSet<int> _unique = [];
    private readonly List<int> _goals = [];
    private readonly int[] _jobs, _jobGoals;
    private readonly ParallelOptions _parallel;
    private readonly bool _reverseJobs;
    private int _requestCursor, _replacementCursor;
    public NavigationGrid Grid { get; private set; }
    public int Workers => _parallel.MaxDegreeOfParallelism;
    public int FieldsPerTick => _jobs.Length;
    public int CachedFields => _fields.Length;
    public int LastBuildCount { get; private set; }
    public int LastVisitedCells { get; private set; }
    public double LastBuildMilliseconds { get; private set; }

    public GridNavigator(NavigationGrid grid, int workers = 1, int fieldsPerTick = 2, int cachedFields = 8, bool reverseJobs = false)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (workers < 1 || workers > 64) throw new ArgumentOutOfRangeException(nameof(workers));
        if (fieldsPerTick < 1 || fieldsPerTick > 64) throw new ArgumentOutOfRangeException(nameof(fieldsPerTick));
        if (cachedFields < 1 || cachedFields > 64) throw new ArgumentOutOfRangeException(nameof(cachedFields));
        Grid = grid;
        // Distance storage is capped at 64 MiB. Each in-flight build additionally rents one queue.
        int capacity = Math.Min(cachedFields, Math.Max(1, NavigationGrid.MaximumCells / grid.CellCount));
        _fields = Enumerable.Range(0, capacity).Select(_ => new Field()).ToArray();
        _jobs = new int[Math.Min(fieldsPerTick, capacity)];
        _jobGoals = new int[_jobs.Length];
        _parallel = new() { MaxDegreeOfParallelism = workers };
        _reverseJobs = reverseJobs;
    }

    public void SetBlocked(int cell, bool blocked)
    {
        var next = Grid.WithBlocked(cell, blocked);
        if (ReferenceEquals(next, Grid)) return;
        Grid = next;
        _cached.Clear();
        foreach (var field in _fields) field.Goal = -1;
        _requestCursor = _replacementCursor = 0;
    }

    public void Prepare(ReadOnlySpan<int> goals, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _unique.Clear(); _goals.Clear();
        foreach (int goal in goals)
        {
            if ((uint)goal >= (uint)Grid.CellCount) throw new ArgumentOutOfRangeException(nameof(goals));
            if (_unique.Add(goal)) _goals.Add(goal);
        }
        _goals.Sort();
        LastBuildCount = LastVisitedCells = 0;
        LastBuildMilliseconds = 0;
        if (_goals.Count == 0) return;
        _requestCursor %= _goals.Count;
        for (int visited = 0; visited < _goals.Count && LastBuildCount < _jobs.Length; visited++)
        {
            int index = (_requestCursor + visited) % _goals.Count;
            int goal = _goals[index];
            if (_cached.ContainsKey(goal)) continue;
            _jobGoals[LastBuildCount++] = goal;
        }
        if (LastBuildCount == 0) return;
        int last = _goals.BinarySearch(_jobGoals[LastBuildCount - 1]);
        _requestCursor = (last + 1) % _goals.Count;
        for (int i = 0; i < LastBuildCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int slot = _replacementCursor;
            _replacementCursor = (_replacementCursor + 1) % _fields.Length;
            _jobs[i] = slot;
            var field = _fields[slot];
            if (field.Goal >= 0) _cached.Remove(field.Goal);
            field.Goal = -1;
            if (field.Distances.Length == 0) field.Distances = new int[Grid.CellCount];
        }
        BuildJobs(cancellationToken);
    }

    private void BuildJobs(CancellationToken cancellationToken)
    {
        long start = Stopwatch.GetTimestamp();
        void Execute(int task)
        {
            int index = _reverseJobs ? LastBuildCount - task - 1 : task;
            var field = _fields[_jobs[index]];
            field.Visited = Build(Grid, _jobGoals[index], field.Distances, cancellationToken);
        }
        if (Workers == 1)
            for (int i = 0; i < LastBuildCount; i++) Execute(i);
        else
        {
            _parallel.CancellationToken = cancellationToken;
            Parallel.For(0, LastBuildCount, _parallel, Execute);
        }
        LastBuildMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        // Publication order is request order, regardless of task scheduling or completion order.
        for (int i = 0; i < LastBuildCount; i++)
        {
            var field = _fields[_jobs[i]];
            field.Goal = _jobGoals[i];
            _cached.Add(field.Goal, _jobs[i]);
            LastVisitedCells += field.Visited;
        }
    }

    public bool TryDistance(int goal, int cell, out int distance)
    {
        if ((uint)cell >= (uint)Grid.CellCount) throw new ArgumentOutOfRangeException(nameof(cell));
        if (_cached.TryGetValue(goal, out int slot)) { distance = _fields[slot].Distances[cell]; return true; }
        distance = -1;
        return false;
    }

    internal void WriteState(BinaryWriter writer)
    {
        Grid.WriteState(writer);
        writer.Write(FieldsPerTick); writer.Write(CachedFields);
        writer.Write(_requestCursor); writer.Write(_replacementCursor);
        foreach (var field in _fields) writer.Write(field.Goal);
    }

    private static int Build(NavigationGrid grid, int goal, int[] distance, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Array.Fill(distance, -1);
        cancellationToken.ThrowIfCancellationRequested();
        if (grid.IsBlocked(goal)) return 0;
        int[] queue = ArrayPool<int>.Shared.Rent(grid.CellCount);
        try
        {
            int head = 0, tail = 1;
            queue[0] = goal; distance[goal] = 0;
            while (head < tail)
            {
                if ((head & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                int cell = queue[head++], x = cell % grid.Width, next = distance[cell] + 1;
                if (cell >= grid.Width) Visit(cell - grid.Width);
                if (x > 0) Visit(cell - 1);
                if (x + 1 < grid.Width) Visit(cell + 1);
                if (cell + grid.Width < grid.CellCount) Visit(cell + grid.Width);
                void Visit(int neighbor)
                {
                    if (distance[neighbor] >= 0 || grid.IsBlocked(neighbor)) return;
                    distance[neighbor] = next;
                    queue[tail++] = neighbor;
                }
            }
            return tail;
        }
        finally { ArrayPool<int>.Shared.Return(queue); }
    }
}
