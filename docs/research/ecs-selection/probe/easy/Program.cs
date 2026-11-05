using EasyEcs;
using var w = new World();
Entity first = default;
for (int i = 0; i < 10000; i++) { var e = w.CreateEntity(new P { X = i, Name = "u" }, new V { X = 1 }); if (i == 0) first = e; }
var count = 0;
var pending = new List<Entity>();
foreach (var row in w.Query<P,V>()) { row.Component1.X += row.Component2.X; pending.Add(row.Entity); count++; }
if (count != 10000 || first.Get<P>().X != 1) throw new Exception("query failed");
// An OpenRTS-controlled barrier, rather than relying on immediate mutations in queries.
foreach (var e in pending) e.Set(new Extra { Value = 2 });
if (!first.Has<Extra>()) throw new Exception("commit failed");
var old = first; first.Destroy(); w.CreateEntity(new P());
if (old.IsAlive) throw new Exception("stale handle");
Console.WriteLine("PASS: explicit query, managed field, 10000 rows, controlled commit, stale handle");
public struct P : IComponent { public int X; public string? Name; }
public struct V : IComponent { public int X; }
public struct Extra : IComponent { public int Value; }
