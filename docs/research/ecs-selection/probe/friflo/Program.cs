using Friflo.Engine.ECS;

// Feasibility probe, not a benchmark or an OpenRTS implementation.
CheckMembershipAndBarriers();
CheckHandles();
var serial = Run(1, false);
Check(serial == Run(4, false), "external jobs changed canonical result");
Check(serial == Run(4, true), "storage creation order changed canonical result");
Console.WriteLine($"PASS: membership, managed state, barriers, stale handles; 10000 rows, workers=1/4, reversed creation; hash={serial:x16}");

static void CheckMembershipAndBarriers()
{
    var store = new EntityStore();
    var player = store.CreateEntity(new Identity { Value = 1 }, new Player { Resources = 100 });
    var state = new QueueState();
    var factory = store.CreateEntity(new Identity { Value = 2 }, new Position(), new Production { State = state });
    Check(store.Query<Position>().Count == 1 && !player.HasComponent<Position>(), "nonspatial player membership");
    factory.GetComponent<Production>().State.Tasks.Add(42);
    Check(state.Tasks[0] == 42, "managed state reference");

    var buffer = store.GetCommandBuffer();
    store.Query<Position>().ForEachEntity((ref Position p, Entity e) =>
    {
        p.X += 5;
        buffer.AddComponent(e.Id, new Health { Value = 100 });
    });
    Check(store.Query<Health>().Count == 0, "structural write applied before barrier");
    buffer.Playback();
    Check(store.Query<Health>().Count == 1 && factory.GetComponent<Position>().X == 5, "barrier not applied");

    // Deaths are committed before production. A dead producer must not be visited.
    factory.DeleteEntity();
    var produced = 0;
    store.Query<Production>().ForEachEntity((ref Production p, Entity e) => produced++);
    Check(produced == 0, "dead producer still selected");

    // Separate producer commits can make the first spawn visible to the next one.
    store.CreateEntity(new Position { X = 10 });
    Check(store.Query<Position>().Count == 1, "spawn invisible to next production operation");
}

static void CheckHandles()
{
    var store = new EntityStore();
    var first = store.CreateEntity(new Position());
    var old = first;
    var internalId = first.Id;
    first.DeleteEntity();
    var next = store.CreateEntity(new Position());
    Check(old.IsNull, "stale handle accepted");
    Check(!next.IsNull, "new entity invalid");
    Console.WriteLine($"Internal ID reuse observed: {internalId == next.Id}; old handle invalid: {old.IsNull}");
}

static ulong Run(int workers, bool reverse)
{
    const int count = 10000;
    var store = new EntityStore();
    var handles = new Entity[count + 1];
    for (var index = 0; index < count; index++)
    {
        var id = reverse ? count - index : index + 1;
        handles[id] = store.CreateEntity(new Identity { Value = id }, new Position { X = id * 1024, Y = -id });
    }

    for (var tick = 0; tick < 4; tick++)
    {
        var inputs = new Row[count];
        var cursor = 0;
        store.Query<Identity, Position>().ForEachEntity((ref Identity id, ref Position p, Entity e) =>
            inputs[cursor++] = new Row(id.Value, p.X, p.Y));
        var results = new Row[count];
        // No ECS refs or store calls cross the worker boundary.
        Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = workers }, i =>
        {
            var row = inputs[i];
            results[i] = row with { X = row.X + row.Id % 17, Y = row.Y - row.Id % 7 };
        });
        Array.Sort(results, (a, b) => a.Id.CompareTo(b.Id));
        foreach (var row in results)
        {
            ref var p = ref handles[row.Id].GetComponent<Position>();
            p.X = row.X;
            p.Y = row.Y;
        }
    }

    var ordered = new List<Row>(count);
    store.Query<Identity, Position>().ForEachEntity((ref Identity id, ref Position p, Entity e) =>
        ordered.Add(new Row(id.Value, p.X, p.Y)));
    ordered.Sort((a, b) => a.Id.CompareTo(b.Id));
    var hash = 14695981039346656037UL;
    foreach (var row in ordered)
        foreach (var value in new[] { row.Id, row.X, row.Y })
            for (var shift = 0; shift < 32; shift += 8)
                hash = unchecked((hash ^ (byte)(value >> shift)) * 1099511628211UL);
    return hash;
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

struct Identity : IComponent { public int Value; }
struct Position : IComponent { public int X; public int Y; }
struct Health : IComponent { public int Value; }
struct Player : IComponent { public int Resources; }
struct Production : IComponent { public QueueState State; }
sealed class QueueState { public List<int> Tasks { get; } = new(); }
readonly record struct Row(int Id, int X, int Y);
