using Frent;
using Frent.Core;
using Frent.Systems;
using World w = new();
Entity first = default;
for (int i = 0; i < 10000; i++) { var e = w.Create(new P { X = i, Name = "u" }, new V { X = 1 }); if (i == 0) first = e; }
var query = w.Query<P, V>();
var count = 0;
foreach (var (p, v) in query) { p.Value.X += v.Value.X; count++; }
if (count != 10000 || first.Get<P>().X != 1 || first.Get<P>().Name != "u")
    throw new Exception("query or managed field failed");
// Register types before structural work in queries.
Component.RegisterComponent<Extra>();
foreach (var (e, p) in w.Query<P>().WithEntities()) { e.Add(new Extra { Value = 2 }); if (e.Has<Extra>()) throw new Exception("not deferred"); }
if (!first.Has<Extra>()) throw new Exception("not committed");
first.Delete();
if (first.IsAlive) throw new Exception("stale");
w.Create(new P());
if (first.IsAlive) throw new Exception("recycled stale");
Console.WriteLine("PASS: explicit query, managed field, 10000 rows, query-exit commit, stale handle");
Determinism.Verify();
struct P { public int X; public string? Name; }
struct V { public int X; }
struct Extra { public int Value; }
