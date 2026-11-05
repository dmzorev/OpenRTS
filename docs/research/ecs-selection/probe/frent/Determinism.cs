using Frent;
using Frent.Systems;

static class Determinism
{
    public static void Verify()
    {
        var serial = Run(1, false);
        if (serial != Run(4, false) || serial != Run(4, true))
            throw new Exception("worker count or creation order changed canonical result");
        Console.WriteLine($"PASS: Frent canonical result; 10000 rows, workers=1/4, reversed creation; hash={serial:x16}");
    }

    static ulong Run(int workers, bool reverse)
    {
        const int count = 10000;
        using var store = new World();
        var handles = new Entity[count + 1];
        var player = store.Create(new Identity { Value = count + 1 }, new Player { Resources = 100 });
        if (player.Has<Position>()) throw new Exception("player must be nonspatial");
        var cold = new QueueState();
        var producer = store.Create(new Production { State = cold });
        producer.Get<Production>().State.Tasks.Add(42);
        if (cold.Tasks[0] != 42) throw new Exception("managed state reference");
        producer.Delete();
        foreach (var _ in store.Query<Production>()) throw new Exception("dead producer still selected");
        var spawned = store.Create(new Position { X = 10 });
        var visible = 0;
        foreach (var _ in store.Query<Position>()) visible++;
        if (visible != 1) throw new Exception("spawn not visible to next query");
        spawned.Delete();

        for (var index = 0; index < count; index++)
        {
            var id = reverse ? count - index : index + 1;
            handles[id] = store.Create(new Identity { Value = id }, new Position { X = id * 1024, Y = -id });
        }

        for (var tick = 0; tick < 4; tick++)
        {
            var inputs = new Row[count];
            var cursor = 0;
            foreach (var (id, p) in store.Query<Identity, Position>())
                inputs[cursor++] = new Row(id.Value.Value, p.Value.X, p.Value.Y);
            var results = new Row[count];
            // Workers never retain ECS refs or call World/Entity APIs.
            Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = workers }, i =>
            {
                var row = inputs[i];
                results[i] = row with { X = row.X + row.Id % 17, Y = row.Y - row.Id % 7 };
            });
            Array.Sort(results, (a, b) => a.Id.CompareTo(b.Id));
            foreach (var row in results)
            {
                ref var p = ref handles[row.Id].Get<Position>();
                p.X = row.X;
                p.Y = row.Y;
            }
        }

        var ordered = new List<Row>(count);
        foreach (var (id, p) in store.Query<Identity, Position>())
            ordered.Add(new Row(id.Value.Value, p.Value.X, p.Value.Y));
        ordered.Sort((a, b) => a.Id.CompareTo(b.Id));
        var hash = 14695981039346656037UL;
        foreach (var row in ordered)
            foreach (var value in new[] { row.Id, row.X, row.Y })
                for (var shift = 0; shift < 32; shift += 8)
                    hash = unchecked((hash ^ (byte)(value >> shift)) * 1099511628211UL);
        return hash;
    }

    struct Identity { public int Value; }
    struct Position { public int X; public int Y; }
    struct Player { public int Resources; }
    struct Production { public QueueState State; }
    sealed class QueueState { public List<int> Tasks { get; } = new(); }
    readonly record struct Row(int Id, int X, int Y);
}
