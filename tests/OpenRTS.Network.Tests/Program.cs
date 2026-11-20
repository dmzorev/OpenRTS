using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using OpenRTS.Core;
using OpenRTS.Core.Orders;
using OpenRTS.Core.Navigation;
using OpenRTS.Network;

var descriptor = new MatchDescriptor("net1-demo-1", "unit:hp100", "64x64/two-units/v1", 42, EndTick: 20);
SimulationWorld World(ulong seed = 42, int workers = 1)
{
    var world = new SimulationWorld([new UnitDefinition("unit", 100)], 65536, 65536, seed,
        navigationGrid: new NavigationGrid(64, 64), navigationWorkers: workers);
    world.AddInitialUnit(0, new(2048, 2048), 1);
    world.AddInitialUnit(0, new(4096, 4096), 2);
    return world;
}
IReadOnlyList<WorldCommand> Input(uint player, long tick) => tick == 2
    ? [new(0, 0, 0, CommandKind.MoveSelected, new(player), new(12000, (int)player * 2048), Recipients: new EntitySet([new(player)]))]
    : [];
string? Value(string prefix) => args.FirstOrDefault(a => a.StartsWith(prefix))?[prefix.Length..];
if (args.Contains("--tls-peer"))
{
    bool hosting = Value("--host=") is not null;
    int port = int.Parse(Value("--port=") ?? "17771");
    int delay = int.Parse(Value("--delay=") ?? "8"), ticks = int.Parse(Value("--ticks=") ?? "100");
    string journal = Value("--journal=")!, pinFile = Value("--pin-file=")!;
    var d = descriptor with { EndTick=0, InputDelay=delay };
    using var session = new DirectSession(hosting, Value("--host=") ?? Value("--join=")!, port,
        hosting ? null : File.ReadAllText(pinFile).Trim(), d, () => World(workers:hosting?1:2), journal);
    if (hosting)
    {
        File.WriteAllText(pinFile,session.Fingerprint);
        var readyClock=Stopwatch.StartNew();
        while (!session.Status.StartsWith("Ожидание подключения",StringComparison.Ordinal) && readyClock.Elapsed < TimeSpan.FromSeconds(5))
        { if (session.Completion.IsFaulted) session.Completion.GetAwaiter().GetResult(); Thread.Sleep(5); }
        Console.WriteLine("LISTEN " + port);
    }
    var records=new List<NetworkTickSample>(); long last=0; bool issued=false, leaving=false;
    double? submitted=null, observed=null; var clock=Stopwatch.StartNew();
    while (!session.Completion.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(45))
    {
        if (session.Statistics is { } sample && sample.Tick>last) { records.Add(sample);last=sample.Tick; }
        using var frame=session.Frames.AcquireLatest();
        if (frame is not null)
        {
            if (!issued && frame.Tick>=20)
            {
                session.Submit(new(0,0,0,CommandKind.Move,new(session.Player),new(12000,(int)session.Player*2048)));
                issued=true; submitted=clock.Elapsed.TotalMilliseconds;
            }
            if (issued && observed is null)
                foreach(var pose in frame.Poses)
                    if (pose.Id.Value==session.Player && pose.Current.X!=(hosting?2048:4096)) observed=clock.Elapsed.TotalMilliseconds;
            if (hosting && !leaving && frame.Tick>=ticks) {session.Leave();leaving=true;}
        }
        Thread.Sleep(2);
    }
    if (!session.Completion.IsCompleted) throw new TimeoutException("TLS WAN peer exceeded completion budget");
    var result=session.Completion.GetAwaiter().GetResult();
    if (session.Statistics is { } final && final.Tick>last) records.Add(final);
    using var replay=File.OpenRead(journal);
    if (MatchJournal.Replay(replay,d,()=>World())!=result) throw new Exception("TLS WAN replay differs");
    if (observed is null || submitted is null) throw new Exception("Input did not reach published frame");
    File.WriteAllText(Value("--metrics=")!,System.Text.Json.JsonSerializer.Serialize(new {
        result,descriptor=d, samples=records,submitToObservedFrameMs=observed-submitted,
        environment=new {os=System.Runtime.InteropServices.RuntimeInformation.OSDescription,architecture=System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),runtime=Environment.Version.ToString()}
    },new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine($"RESULT {result.Tick} {result.StateHash}");
    return;
}
if (args.Contains("--wan"))
{
    WanChecks.Profiles(Value("--output=") ?? Path.Combine(Path.GetTempPath(), "openrts-wan-results.json"));
    return;
}
if (Value("--host=") is { } host || Value("--join=") is not null)
{
    bool hosting = Value("--host=") is not null;
    int port = int.Parse(Value("--port=") ?? "17771");
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    TcpClient socket;
    if (hosting)
    {
        var listener = new TcpListener(IPAddress.Parse(Value("--host=")!), port); listener.Start();
        Console.WriteLine($"LISTEN {((IPEndPoint)listener.LocalEndpoint).Port}");
        try { socket = listener.AcceptTcpClientAsync(stop.Token).AsTask().GetAwaiter().GetResult(); }
        finally { listener.Stop(); }
    }
    else
    {
        socket = new TcpClient(); socket.ConnectAsync(Value("--join=")!, port, stop.Token).AsTask().GetAwaiter().GetResult();
    }
    uint player = hosting ? 1u : 2u;
    using var journal = File.Create(Value("--journal=") ?? $"net1-player{player}.journal");
    var report = DirectMatch.Run(socket, player, descriptor, () => World(workers: hosting ? 1 : 2),
        tick => Input(player, tick), journal, stop.Token);
    Console.WriteLine($"RESULT {report.Tick} {report.StateHash}");
    journal.Close();
    using var replay = File.OpenRead(Value("--journal=") ?? $"net1-player{player}.journal");
    if (MatchJournal.Replay(replay, descriptor, () => World()).StateHash != report.StateHash) throw new Exception("Replay mismatch");
    return;
}
if (ReplayChecks.Tool(args)) return;
int passed = 0;
if (args.Contains("--net5")) { DirectoryChecks.Run(Test); Console.WriteLine($"{passed} NET5 checks passed."); return; }
if (args.Contains("--net4")) { RuntimeSnapshotChecks.Run(Test); ReplayChecks.Run(Test); Console.WriteLine($"{passed} NET4 checks passed."); return; }
if (args.Contains("--net3")) { StarChecks.Run(Test); Console.WriteLine($"{passed} NET3 checks passed."); return; }
void Test(string name, Action action) { if (Value("--filter=") is { } filter && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) return; action(); passed++; Console.WriteLine("PASS Network: " + name); }
void Check(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
void Reject(Action action) { try { action(); } catch (Exception e) when (e is InvalidDataException or EndOfStreamException or ArgumentException) { return; } throw new Exception("Expected rejection"); }
(Guid, byte[]) Batch(long tick, long sequence = 1)
{
    var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
    return (id, Wire.Batch(id, 1, tick, [new(tick, 1, sequence, CommandKind.Stop, new(1))]));
}
Test("canonical codec, large selection, all supported intents, unsupported fields", () => {
    var id = Guid.NewGuid();
    foreach (var kind in Wire.Kinds)
    {
        var c = new WorldCommand(2, 1, 1, kind, new(1), new(2000, 3000, 4), TargetEntity: new(2),
            Recipients: new EntitySet(Enumerable.Range(1, 10000).Select(n => new EntityId((uint)n))), Queued: true);
        var b = Wire.Batch(id, 1, 2, [c]); var decoded = Wire.ReadBatch(b);
        Check(decoded.Commands[0].Recipients!.Count == 10000 && Wire.Batch(id, 1, 2, decoded.Commands).SequenceEqual(b));
    }
    Reject(() => Wire.Batch(id, 1, 2, [new(2, 1, 1, CommandKind.Spawn)]));
    Reject(() => Wire.Batch(id, 1, 2, [new(2, 1, 1, CommandKind.Stop, Owner: 2)]));
    Reject(() => Wire.ReadBatch([3]));
    var valid = Batch(2).Item2; Reject(() => Wire.ReadBatch(valid.Concat(new byte[] { 0 }).ToArray()));
});
Test("future reorder, identical/conflicting duplicate, expired identity and sequence gap", () => {
    var (id, b) = Batch(2); var inbox = new TickInbox(id, 1, 2);
    inbox.Add(Wire.Batch(id, 1, 3, [new(3, 1, 2, CommandKind.Stop, new(1))]), 1);
    inbox.Add(b, 1); inbox.Add(b, 1); Check(inbox.Take(2).Length == 1); inbox.Add(b, 2);
    Reject(() => inbox.Add(Wire.Batch(id, 1, 2, []), 2));
    Check(inbox.Take(3)[0].Sequence == 2);
    Reject(() => inbox.Add(Batch(9).Item2, 3));
    Reject(() => inbox.Add(Wire.Batch(Guid.NewGuid(), 1, 4, []), 3));
    Reject(() => inbox.Add(Wire.Batch(id, 2, 4, []), 3));
    var gap = new TickInbox(id, 1, 2); gap.Add(Batch(2, 2).Item2, 0); Reject(() => gap.Take(2));
});
Test("framing partial reads, coalesced frames, oversized and truncated payload", () => {
    var data = Batch(2).Item2;
    using var stream = new MemoryStream();
    Wire.Send(stream, data, default).GetAwaiter().GetResult(); Wire.Send(stream, data, default).GetAwaiter().GetResult();
    stream.Position = 0; using var split = new SplitStream(stream);
    Check(Wire.Receive(split, default).GetAwaiter().GetResult().SequenceEqual(data));
    Check(Wire.Receive(split, default).GetAwaiter().GetResult().SequenceEqual(data));
    Reject(() => Wire.Receive(new MemoryStream([255,255,255,127]), default).GetAwaiter().GetResult());
    Reject(() => Wire.Receive(new MemoryStream([10,0,0,0,1]), default).GetAwaiter().GetResult());
});
Task<SimulationReport>[] Pair(MatchDescriptor a, MatchDescriptor b, MemoryStream ja, MemoryStream jb,
    Func<SimulationWorld>? wa = null, Func<SimulationWorld>? wb = null, Action<SimulationWorld>? after = null, Func<uint, long, IReadOnlyList<WorldCommand>>? input = null)
{
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    var c = new TcpClient(); c.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
    var h = listener.AcceptTcpClient(); listener.Stop();
    return [Task.Run(() => DirectMatch.Run(h, 1, a, wa ?? (() => World()), t => (input ?? Input)(1, t), ja, timeout: TimeSpan.FromSeconds(3), afterTick: after)),
        Task.Run(() => DirectMatch.Run(c, 2, b, wb ?? (() => World(workers: 2)), t => (input ?? Input)(2, t), jb, timeout: TimeSpan.FromSeconds(3)))];
}
Test("two owners, different workers, every-tick hash, offline replay and incomplete log", () => {
    using var a = new MemoryStream(); using var b = new MemoryStream();
    var tasks = Pair(descriptor, descriptor, a, b); Task.WaitAll(tasks);
    Check(tasks[0].Result == tasks[1].Result);
    a.Position = 0; Check(MatchJournal.Replay(a, descriptor, () => World()) == tasks[0].Result);
    Reject(() => MatchJournal.Replay(new MemoryStream(a.ToArray()[..^1]), descriptor, () => World()));
});
Test("delay boundaries bootstrap and close empty ticks", () => {
    foreach (int delay in new[] { 1, 16 })
    {
        using var a = new MemoryStream(); using var b = new MemoryStream();
        var d = descriptor with { InputDelay = delay };
        var tasks = Pair(d, d, a, b); Task.WaitAll(tasks); Check(tasks[0].Result == tasks[1].Result);
        a.Position = 0; Check(MatchJournal.Replay(a, d, () => World()) == tasks[0].Result);
    }
});
Test("decoder rejects corrupt kind, issuer, count, invalid flags and sequence", () => {
    var bytes = Batch(2).Item2;
    foreach (int offset in new[] { 0, 17, 29, 33, 62 })
    {
        var bad = bytes.ToArray(); bad[offset] = 255; Reject(() => Wire.ReadBatch(bad));
    }
    Reject(() => Wire.Batch(Guid.NewGuid(), 1, 2, Enumerable.Repeat(new WorldCommand(2,1,1,CommandKind.Stop), 257).ToArray()));
    Reject(() => Wire.Batch(Guid.NewGuid(), 1, 2, [new(2,1,1,CommandKind.Stop, Recipients: new EntitySet(Enumerable.Range(1,16385).Select(n => new EntityId((uint)n))))]));
});
Test("foreign actor input is denied by gameplay on the agreed tick", () => {
    using var a = new MemoryStream(); using var b = new MemoryStream(); bool denied = false;
    var tasks = Pair(descriptor, descriptor, a, b, after: w => {
        if (w.Tick == 2) denied = w.Events.ToArray().Count(e => e.Kind == WorldEventKind.CommandDenied) == 2;
    }, input: (player, tick) => tick == 2 ? [new(0, 0, 0, CommandKind.MoveSelected, new(3 - player), new(10000, 10000))] : []);
    Task.WaitAll(tasks); Check(denied && tasks[0].Result == tasks[1].Result);
});
Test("missing input stops after the agreed bootstrap, cancel and EOF stop promptly", () => {
    foreach (bool disconnect in new[] { false, true })
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var fake = new TcpClient(); fake.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var host = listener.AcceptTcpClient(); listener.Stop();
        using var journal = new MemoryStream(); using var stop = new CancellationTokenSource(3000); int publications = 0;
        SimulationWorld? owned = null;
        var task = Task.Run(() => DirectMatch.Run(host, 1, descriptor, () => owned = World(), _ => [], journal, stop.Token,
            afterTick: _ => Interlocked.Increment(ref publications)));
        using var stream = fake.GetStream();
        var response = Wire.Receive(stream, stop.Token).GetAwaiter().GetResult(); response[5] = 2; Array.Clear(response, 9, 16);
        Wire.Send(stream, response, stop.Token).GetAwaiter().GetResult();
        var ready = Wire.Receive(stream, stop.Token).GetAwaiter().GetResult(); Wire.Send(stream, ready, stop.Token).GetAwaiter().GetResult();
        _ = Wire.Receive(stream, stop.Token).GetAwaiter().GetResult();
        Check(!task.Wait(100) && Volatile.Read(ref publications) <= descriptor.InputDelay - 1);
        if (disconnect) fake.Dispose(); else stop.Cancel();
        try { task.GetAwaiter().GetResult(); throw new Exception("Expected interrupted match"); }
        catch (Exception e) when (e is OperationCanceledException or IOException) { }
        Check(owned!.Tick == descriptor.InputDelay - 1);
        try { owned.Step(); throw new Exception("World was not disposed"); } catch (ObjectDisposedException) { }
    }
});
Test("mismatched descriptor and initial seed reject before first Step", () => {
    foreach (bool initial in new[] { false, true })
    {
        using var a = new MemoryStream(); using var b = new MemoryStream();
        var tasks = Pair(descriptor, initial ? descriptor : descriptor with { ScenarioHash = "other" }, a, b,
            wb: initial ? () => World(43) : null);
        try { Task.WaitAll(tasks); throw new Exception("Expected mismatch"); }
        catch (AggregateException) { Check(tasks.All(t => t.IsFaulted)); }
    }
});
Test("first divergent tick stops the match and records failure", () => {
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    using var fake = new TcpClient(); fake.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
    var host = listener.AcceptTcpClient(); listener.Stop();
    using var journal = new MemoryStream(); using var stop = new CancellationTokenSource(3000);
    var task = Task.Run(() => DirectMatch.Run(host, 1, descriptor with { HashInterval=1 }, () => World(), _ => [], journal, stop.Token));
    using var stream = fake.GetStream();
    var hello = Wire.Receive(stream, stop.Token).GetAwaiter().GetResult();
    var response = hello.ToArray(); response[5] = 2; Array.Clear(response, 9, 16);
    Wire.Send(stream, response, stop.Token).GetAwaiter().GetResult();
    var ready = Wire.Receive(stream, stop.Token).GetAwaiter().GetResult(); Wire.Send(stream, ready, stop.Token).GetAwaiter().GetResult();
    var batch = Wire.ReadBatch(Wire.Receive(stream, stop.Token).GetAwaiter().GetResult());
    var remoteBatch = Wire.Batch(batch.Match, 2, batch.Tick, []);
    Wire.Send(stream, remoteBatch, stop.Token).GetAwaiter().GetResult();
    Wire.Send(stream, remoteBatch, stop.Token).GetAwaiter().GetResult();
    _ = Wire.Receive(stream, stop.Token).GetAwaiter().GetResult();
    Wire.Send(stream, Wire.Pack(w => { w.Write((byte)4); w.Write(1L); Wire.Text(w, "incorrect"); }), stop.Token).GetAwaiter().GetResult();
    try { task.GetAwaiter().GetResult(); throw new Exception("Expected desync"); }
    catch (DesyncException e) { Check(e.Tick == 1); }
    Check(System.Text.Encoding.UTF8.GetString(journal.ToArray()).Contains("Desync"));
});
Test("missing peer cancels bounded wait and releases world/socket", () => {
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    using var peer = new TcpClient(); peer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
    var socket = listener.AcceptTcpClient(); listener.Stop();
    using var stop = new CancellationTokenSource(100); using var journal = new MemoryStream();
    var watch = Stopwatch.StartNew();
    try { DirectMatch.Run(socket, 1, descriptor, () => World(), _ => [], journal, stop.Token); throw new Exception("Expected cancel"); }
    catch (OperationCanceledException) { Check(watch.Elapsed < TimeSpan.FromSeconds(3)); }
});
Test("deadline is reported as timeout rather than user cancellation", () => {
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    using var fake = new TcpClient(); fake.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
    var socket = listener.AcceptTcpClient(); listener.Stop(); using var journal = new MemoryStream();
    try { DirectMatch.Run(socket, 1, descriptor, () => World(), _ => [], journal, timeout: TimeSpan.FromMilliseconds(100)); throw new Exception("Expected timeout"); }
    catch (TimeoutException) { }
});
Test("two operating-system processes connect and independently replay", () => {
    string dir = Path.Combine(Path.GetTempPath(), "openrts-net1-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
    Process Start(params string[] options)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") psi.ArgumentList.Add(typeof(SplitStream).Assembly.Location);
        foreach (var option in options) psi.ArgumentList.Add(option);
        return Process.Start(psi)!;
    }
    using var host = Start("--host=127.0.0.1", "--port=0", "--journal=" + Path.Combine(dir,"a.log"));
    Process? join = null;
    try
    {
        var line = host.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult()!;
        join = Start("--join=127.0.0.1", "--port=" + line.Split(' ')[1], "--journal=" + Path.Combine(dir,"b.log"));
        Check(host.WaitForExit(20000) && join.WaitForExit(20000));
        string ho = host.StandardOutput.ReadToEnd(), jo = join.StandardOutput.ReadToEnd();
        if (host.ExitCode != 0 || join.ExitCode != 0) throw new Exception(host.StandardError.ReadToEnd() + join.StandardError.ReadToEnd());
        Check(ho == jo && ho.StartsWith("RESULT 20 "));
    }
    finally { if (!host.HasExited) host.Kill(true); if (join is { HasExited: false }) join.Kill(true); join?.Dispose(); Directory.Delete(dir, true); }
});
Test("TLS live peers pace ticks, submit complete player intents, leave and replay", () => {
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
    string dir = Path.Combine(Path.GetTempPath(), "openrts-net2-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
    var d = descriptor with { EndTick = 0 };
    try
    {
        using var host = new DirectSession(true, "127.0.0.1", port, null, d, () => World(), Path.Combine(dir,"host.log"));
        Thread.Sleep(100);
        using var join = new DirectSession(false, "127.0.0.1", port, host.Fingerprint, d, () => World(workers: 2), Path.Combine(dir,"join.log"));
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(8))
        {
            if (host.Completion.IsFaulted || join.Completion.IsFaulted) throw new Exception($"HOST {host.Completion.Exception} JOIN {join.Completion.Exception}");
            if (join.Completion.IsFaulted) join.Completion.GetAwaiter().GetResult();
            using var frame = host.Frames.AcquireLatest();
            if (frame?.Tick >= 5) break;
            Thread.Sleep(10);
        }
        Check(watch.Elapsed >= TimeSpan.FromMilliseconds(150) && watch.Elapsed < TimeSpan.FromSeconds(8));
        host.Submit(new(0,0,0,CommandKind.Move, new(1), new(10000,2000)));
        join.Submit(new(0,0,0,CommandKind.SetRally, new(2), new(4000,4000)));
        Thread.Sleep(300); host.Leave();
        Check(Task.WaitAll([host.Completion, join.Completion], 5000));
        Check(host.Completion.Result == join.Completion.Result);
        using var file = File.OpenRead(host.JournalPath);
        Check(MatchJournal.Replay(file, d, () => World()) == host.Completion.Result);
    }
    finally { Directory.Delete(dir, true); }
});
Test("TLS rejects a wrong host fingerprint and disposing a listener is bounded", () => {
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
    using var host = new DirectSession(true, "127.0.0.1", port, null, descriptor with { EndTick=0 }, () => World(), Path.GetTempFileName());
    Thread.Sleep(100);
    using var join = new DirectSession(false, "127.0.0.1", port, new string('0',64), descriptor with { EndTick=0 }, () => World(), Path.GetTempFileName());
    try { join.Completion.Wait(TimeSpan.FromSeconds(5)); throw new Exception("Expected TLS failure"); }
    catch (AggregateException e) { Check(e.Flatten().InnerExceptions.Any(x => x is System.Security.Authentication.AuthenticationException)); }
});
Test("extended wire fields survive production, ability and transform intents", () => {
    foreach (var c in new WorldCommand[] {
        new(2,1,1,CommandKind.EnqueueTask,new(1),Action:"rifleman",Queue:"infantry",Amount:5,Repeat:true),
        new WorldCommand(2,1,1,CommandKind.Transform,new(1),Action:"deploy") { TransformHere=true,QuarterTurns=3 },
        new WorldCommand(2,1,1,CommandKind.AttackContact,new(1),Owner:1) {ContactId=123},
        new(2,1,1,CommandKind.CancelConstruction,Owner:1,TargetEntity:new(2)),
        new(2,1,1,CommandKind.PlaceProduced,new(1),new(1000,1000),Action:"building",TaskId:55)
    })
    {
        var id=Guid.NewGuid(); var b=Wire.Batch(id,1,2,[c]);
        Check(Wire.ReadBatch(b).Commands[0] == c);
    }
});
DirectoryChecks.Run(Test);
WanChecks.Run(Test);
StarChecks.Run(Test);
RuntimeSnapshotChecks.Run(Test);
ReplayChecks.Run(Test);
Console.WriteLine($"{passed} Network checks passed.");

sealed class SplitStream(Stream inner) : Stream
{
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer[..Math.Min(1, buffer.Length)], ct);
    public override int Read(byte[] b, int o, int n) => inner.Read(b,o,Math.Min(1,n));
    public override void Flush() => throw new NotSupportedException(); public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long n) => throw new NotSupportedException(); public override void Write(byte[] b,int o,int n) => throw new NotSupportedException();
}
