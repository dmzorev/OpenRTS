using System.Diagnostics;
using DeclarativeContent;
using OpenRTS.Content;
using OpenRTS.Core;
using OpenRTS.Core.Orders;
using OpenRTS.Core.Presentation;
using OpenRTS.Network;
using OpenRTS.Samples;

internal static class ReplayChecks
{
    private static readonly Lazy<UnitCatalog> Catalog = new(() => GameManifest.Load("sandbox/game.yaml").LoadUnits());
    private static ContentSource Read(string path) => new(path, File.ReadAllText(Path.Combine("sandbox", path)));
    private static SimulationWorld World(string recipe, int workers = 1) => recipe switch {
        "production" => BuildingScenario.Create(Catalog.Value, workers),
        "aviation" => AviationScenario.Load(Catalog.Value, Read).CreateWorld(workers, restrictPlayerCommands: true),
        "sensors" => FogScenario.Create(Catalog.Value, holdTicks: 12, sensors: true),
        _ => throw new InvalidDataException("Unknown replay fixture.")
    };
    private sealed record History(byte[] Journal, MatchDescriptor Descriptor, string[] Hashes, WorldCommand[][] Commands);
    private static History Create(string recipe, int ticks)
    {
        using var world = World(recipe); world.RestrictCommandsToPlayers();
        var descriptor = new MatchDescriptor("replay-fixture", Catalog.Value.Summary.Fingerprint, recipe, 1, EndTick: ticks);
        var state = new MatchState(Guid.NewGuid(), 1, 0, MatchPhase.Playing, [new(1, SlotController.Human, true, true), new(2, SlotController.Human, true, true)], "start");
        using var output = new MemoryStream(); using var log = new StarJournal(output, descriptor, world.Report().StateHash, StarProtocol.WriteState(state), recipe);
        WorldCommand[] input = recipe switch { "production" => ProductionScenario.Commands(Catalog.Value), "aviation" => AviationScenario.Commands, _ => FogScenario.RadarCommands };
        var hashes = new string[ticks + 1]; hashes[0] = world.Report().StateHash;
        var commands = new WorldCommand[ticks + 1][]; commands[0] = [];
        long[] sequence = new long[3];
        for (long tick = 1; tick <= ticks; tick++)
        {
            var poses = new EntityPose[world.Count]; world.CopyPoses(poses);
            commands[tick] = input.Where(c => c.Tick == tick && Wire.Kinds.Contains(c.Kind)).Select(c => {
                var entity = c.Entity != default ? c.Entity : c.Recipients?.Ids[0] ?? default;
                uint owner = poses.FirstOrDefault(p => p.Id == entity).Presentation.Owner;
                if (owner is < 1 or > 2) owner = 1;
                return c.WithStamp(tick, owner, ++sequence[owner]);
            }).OrderBy(c => c.Issuer).ThenBy(c => c.Sequence).ToArray();
            world.Step(commands[tick]); hashes[tick] = world.Report().StateHash;
            log.Write(StarProtocol.TurnFrame(tick, state.Revision));
            for (uint p = 1; p <= 2; p++) log.Write(Wire.Batch(state.Match, p, tick, commands[tick].Where(c => c.Issuer == p).ToArray()));
            log.Write(StarProtocol.Check(StarProtocol.Hash, tick, hashes[tick]));
        }
        log.Write(StarProtocol.Check(StarProtocol.Finish, ticks, hashes[ticks])); log.Complete();
        return new(output.ToArray(), descriptor, hashes, commands);
    }
    private static void Check(bool value, string message = "Replay assertion failed") { if (!value) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception e) when (e is InvalidDataException or EndOfStreamException or ArgumentException or TimeoutException or OperationCanceledException) { return; }
        throw new Exception("Expected replay rejection");
    }
    internal static bool Tool(string[] args)
    {
        string? runtimePath = args.FirstOrDefault(a => a.StartsWith("--restore-runtime="))?[18..];
        if (runtimePath is not null)
        {
            using var world = SimulationWorld.RestoreSnapshot(File.ReadAllBytes(runtimePath), () => World("production", 2));
            Console.WriteLine($"RUNTIME_RESTORED {world.Tick} {world.Report().StateHash}"); return true;
        }
        string? path = args.FirstOrDefault(a => a.StartsWith("--restore-checkpoint="))?[21..];
        if (path is null) return false;
        using var input = File.OpenRead(path);
        using var restored = ReplayCheckpoint.Restore(input, info => World(info.Recipe, 2));
        Console.WriteLine($"RESTORED {restored.World.Tick} {restored.World.Report().StateHash}"); return true;
    }
    internal static void Run(Action<string, Action> test)
    {
        test("Lobby recipe keeps active seats and teams through player eight", () =>
        {
            var catalog = Catalog.Value;
            var ground = new MapLayer(["grass"], new int[96 * 16]);
            var slots = Enumerable.Range(0, 8).Select(i => new MapSlotSource($"start-{i}", new(4608 + i * 10240, 7168))).ToArray();
            var source = new MapSource("eight-seats", true, 96, 16, ground, ground, slots, []);
            var map = GameMap.Load(new("eight-seats.mapsource.json", source.Write()), catalog);
            var set = StartingSet.Load(Read("content/start-sets/supply.yaml"));
            foreach (int active in new[] { 6, 8 })
            {
                var seats = Enumerable.Range(0, 8).Select(i => new MatchSeat(
                    i >= active ? SlotController.None : SlotController.Human, i, i == active - 1 ? 8u : 0u)).ToArray();
                var options = new MatchSetupOptions(map.Id, set.Id, 2, 1, 25, seats).Bind(map, set);
                var restored = MatchSetupOptions.Parse(options.Serialize());
                Check(restored.Seats.SequenceEqual(seats) && restored.PlayerSlots(map).SequenceEqual(map.Slots));
                Check(restored.Prepare(map, set).UnitCount == active * set.Units.Count);
            }
        });
        test("B6 gameplay recipe binds a compiled map, starting set and committed local playtest", () =>
        {
            var terrain = LocomotionCatalog.Load([new("terrain.yaml", """
                grass: {Terrain: {Default: true}}
                foot: {Locomotor: {Terrain: {grass: {}}}}
                """)]);
            var catalog = UnitCatalog.Load([new("units.yaml", "scout: {Health: {HP: 100}}")], terrain);
            var ground = new MapLayer(["grass"], new int[12 * 8]);
            var source = new MapSource("replay-fixture", true, 12, 8, ground, ground,
                [new("blue", new(1536, 1536)), new("red", new(10752, 6656))],
                [new("wounded", "scout", new(4608, 1536), "neutral", InitialHealth: 35)]);
            var compiled = MapCompiler.Read(new("replay-fixture.ortmap.json", MapCompiler.Compile(source, catalog).Write()), catalog);
            var setInput = new ContentSource("replay-start.yaml", """
                version: 1
                id: replay-start
                credits: 5000
                units:
                  - { type: scout, offset: [0, 0] }
                """);
            var set = StartingSet.Load(setInput);
            var options = new MatchSetupOptions(source.Id, set.Id, 2, 1, 25,
                [new(SlotController.Human, 1), new(SlotController.None, 0), new(SlotController.None, 0), new(SlotController.None, 0)],
                Local: true).Bind(compiled.Map, set);
            var match = options.Prepare(compiled.Map, set);
            var descriptor = options.Describe(catalog.Summary.Fingerprint, match.Seed);
            CompiledMap Rebuild(MapLayer visual, MapObjectSource[] objects) => MapCompiler.Compile(new(source.Id, source.IsSkirmish,
                source.Width, source.Height, source.Terrain, visual, source.Slots, objects, source.CellSize, source.Origin, source.Surface), catalog);
            var visuals = new MapLayer(["different-visual"], new int[source.Width * source.Height]);
            var restyled = Rebuild(visuals, source.Objects.ToArray());
            Check(restyled.DataFingerprint != compiled.DataFingerprint && options.Prepare(restyled.Map, set).UnitCount == match.UnitCount);
            var changedObjects = source.Objects.ToArray();
            int wounded = Array.FindIndex(changedObjects, obj => obj.InitialHealth.HasValue);
            changedObjects[wounded] = changedObjects[wounded] with { InitialHealth = changedObjects[wounded].InitialHealth + 1 };
            Reject(() => options.Prepare(Rebuild(source.Visual, changedObjects).Map, set));
            Reject(() => options.Prepare(compiled.Map, StartingSet.Load(setInput with { Text = setInput.Text.Replace("5000", "5001", StringComparison.Ordinal) })));
            Check(StartingSet.Load(setInput with { Text = setInput.Text + "\n# formatting only\n" }).Fingerprint == set.Fingerprint);

            string directory = Directory.CreateTempSubdirectory("openrts-b6-").FullName;
            try
            {
                string journal = Path.Combine(directory, "recorded.journal");
                using var recorder = new LocalReplayRecorder(journal, descriptor, options.Serialize(), options.Seats.Select(s => s.Controller).ToArray());
                SimulationWorld World() => options.CreateWorld(match, 1, restrict: true);
                using var session = new SimulationSession(World, startPaused: true, validateTurn: recorder.ValidateTurn, observeTick: recorder.ObserveTick);
                Reject(() => LocalReplayRecorder.ValidateInput(new(0, 0, 0, CommandKind.Spawn)));
                Check(!File.Exists(journal), "A partial recording was visible before completion.");
                var intent = new WorldCommand(0, 0, 0, CommandKind.Stop, new(1));
                LocalReplayRecorder.ValidateInput(intent);
                session.Submit(intent);
                session.StepOnce();
                Check(SpinWait.SpinUntil(() =>
                {
                    using var frame = session.Frames.AcquireLatest();
                    return frame?.Tick == 1 || session.Completion.IsCompleted;
                }, TimeSpan.FromSeconds(5)));
                if (session.Completion.IsCompleted) session.Completion.GetAwaiter().GetResult();
                var recorded = recorder.Complete();
                Check(recorded.Tick == 1 && session.GetCommandLog().Length == 1);
                using var complete = File.OpenRead(journal);
                Check(StarJournal.Replay(complete, info => MatchSetupOptions.ReadReplay(info, catalog.Summary.Fingerprint)
                    .CreateWorld(options.Prepare(restyled.Map, set), 2, restrict: true)) == recorded);
                byte[] bytes = File.ReadAllBytes(journal);
                Reject(() => StarJournal.Replay(new MemoryStream(bytes[..^1]), _ => World()));
                Reject(() => StarJournal.Replay(new MemoryStream(bytes), info =>
                    MatchSetupOptions.ReadReplay(info with { Recipe = info.Recipe.Replace("\"Fog\":2", "\"Fog\":1", StringComparison.Ordinal) }, catalog.Summary.Fingerprint)
                        .CreateWorld(match, 1, restrict: true)));
                using var aborted = new LocalReplayRecorder(Path.Combine(directory, "aborted.journal"), descriptor,
                    options.Serialize(), options.Seats.Select(s => s.Controller).ToArray());
                using var initial = World();
                aborted.ObserveTick(initial);
                Reject(() => aborted.ValidateTurn(initial, Enumerable.Range(1, 257).Select(i => intent.WithStamp(1, 1, i)).ToArray()));
                Check(initial.Tick == 0 && Directory.EnumerateFiles(directory).SequenceEqual([journal]),
                    "A rejected turn advanced the world or published a replay.");

                foreach (string legacy in new[] { "openrts-lobby-v2", "openrts-lobby-v3" })
                {
                    string recipe = System.Text.Json.JsonSerializer.Serialize(new { Map = source.Id, Mode = set.Id, Fog = 2, Delay = 6, TickRate = 25, options.Seats });
                    var oldDescriptor = descriptor with { Rules = legacy, InputDelay = 6 };
                    using var oldHistory = new MemoryStream();
                    var state = new MatchState(Guid.NewGuid(), 1, 0, MatchPhase.Playing,
                        [new(1, SlotController.Human, true, true), new(2, SlotController.None, false, false)], "start");
                    using (var writer = new StarJournal(oldHistory, oldDescriptor, initial.Report().StateHash, StarProtocol.WriteState(state), recipe))
                    {
                        writer.Write(StarProtocol.Check(StarProtocol.Finish, 0, initial.Report().StateHash));
                        writer.Complete();
                    }
                    oldHistory.Position = 0;
                    Check(StarJournal.Replay(oldHistory, info =>
                    {
                        var old = MatchSetupOptions.ReadReplay(info, catalog.Summary.Fingerprint);
                        return old.CreateWorld(old.Prepare(compiled.Map, set), 1, restrict: true);
                    }) == initial.Report());
                }
            }
            finally { Directory.Delete(directory, recursive: true); }
        });
        test("Replay bootstrap rejects a mismatched authored map initializer", () =>
        {
            var catalog = Catalog.Value;
            var original = MapSource.Read(Read("content/maps/compilation-fixture.mapsource.json"));
            CompiledMap Compile(int hp)
            {
                var placements = original.Objects.ToArray();
                placements[0] = placements[0] with { InitialHealth = hp, HealthVariant = new("veteran", 180) };
                return MapCompiler.Compile(new(original.Id, original.IsSkirmish, original.Width, original.Height,
                    original.Terrain, original.Visual, original.Slots, placements), catalog);
            }
            var compiled = Compile(35);
            var loaded = MapCompiler.Read(new("test.ortmap.json", compiled.Write()), catalog);
            var players = new Dictionary<string, PlayerDefinition> { ["blue"] = new(1, "Blue"), ["red"] = new(2, "Red") };
            SimulationWorld Open(CompiledMap map) => map.Map.Prepare(players).CreateWorld(restrictPlayerCommands: true);
            using var world = Open(compiled);
            var descriptor = new MatchDescriptor("map-properties-test", compiled.EffectiveContentFingerprint, compiled.GameplayFingerprint, 1, EndTick: 1);
            var state = new MatchState(Guid.NewGuid(), 1, 0, MatchPhase.Playing,
                [new(1, SlotController.Human, true, true), new(2, SlotController.Human, true, true)], "start");
            using var output = new MemoryStream();
            using (var journal = new StarJournal(output, descriptor, world.Report().StateHash, StarProtocol.WriteState(state), "map-properties-test"))
            {
                world.Step();
                journal.Write(StarProtocol.TurnFrame(1, 1));
                journal.Write(Wire.Batch(state.Match, 1, 1, []));
                journal.Write(Wire.Batch(state.Match, 2, 1, []));
                journal.Write(StarProtocol.Check(StarProtocol.Hash, 1, world.Report().StateHash));
                journal.Write(StarProtocol.Check(StarProtocol.Finish, 1, world.Report().StateHash));
                journal.Complete();
            }
            byte[] bytes = output.ToArray();
            Check(StarJournal.Replay(new MemoryStream(bytes), descriptor, () => Open(loaded)) == world.Report());
            Reject(() => StarJournal.Replay(new MemoryStream(bytes), descriptor, () => Open(Compile(36))));
        });
        test("NET4 default replay and recovery accept a match beyond 100000 ticks", () => {
            const int ticks = 100001;
            SimulationWorld Empty() => new([new UnitDefinition("unit", 100)], 4096, 4096, 42, tickRate: 40);
            var descriptor = new MatchDescriptor("long-replay", "unit", "empty", 42, TickRate: 40, EndTick: 0);
            var state = new MatchState(Guid.NewGuid(), 1, 0, MatchPhase.Playing,
                [new(1, SlotController.Human, true, true), new(2, SlotController.Human, true, true)], "start");
            using var world = Empty(); world.RestrictCommandsToPlayers();
            using var history = new MemoryStream();
            using (var journal = new StarJournal(history, descriptor, world.Report().StateHash, StarProtocol.WriteState(state)))
            {
                for (int tick = 1; tick <= ticks; tick++)
                {
                    world.Step(); journal.Write(StarProtocol.TurnFrame(tick, 1));
                    for (uint player = 1; player <= 2; player++) journal.Write(Wire.Batch(state.Match, player, tick, []));
                    journal.Write(StarProtocol.Check(StarProtocol.Hash, tick, world.Report().StateHash));
                }
                journal.Write(StarProtocol.Check(StarProtocol.Finish, ticks, world.Report().StateHash)); journal.Complete();
            }
            history.Position = 0; Check(StarJournal.Inspect(history).EndTick == ticks);
            history.Position = 0; Check(StarJournal.Replay(history, descriptor, Empty) == world.Report());
            history.Position = 0; using var checkpoint = new MemoryStream(); ReplayCheckpoint.Capture(history, ticks, checkpoint);
            checkpoint.Position = 0; using var restored = ReplayCheckpoint.Restore(checkpoint, _ => Empty());
            Check(restored.World.Report() == world.Report());
            history.Position = 0; Reject(() => StarJournal.Inspect(history, new(MaximumTicks: 100000)));
            history.Position = 0; Reject(() => StarJournal.Inspect(history, new(MaximumBytes: history.Length - 1)));
        });
        foreach (var (recipe, ticks, checkpoint) in new[] { ("production", 100, 12), ("aviation", 420, 185), ("sensors", 48, 12) })
            test("NET4 replay recovery continues every hash: " + recipe, () => {
                var source = Create(recipe, ticks);
                using var journal = new MemoryStream(source.Journal);
                var info = StarJournal.Inspect(journal); Check(info.Recipe == recipe && info.Descriptor == source.Descriptor && info.EndTick == ticks);
                foreach (long boundary in new long[] { 0, checkpoint, ticks })
                {
                    journal.Position = 0; using var saved = new MemoryStream(); ReplayCheckpoint.Capture(journal, boundary, saved); saved.Position = 0;
                    using var restored = ReplayCheckpoint.Restore(saved, metadata => World(metadata.Recipe, 2));
                    Check(restored.World.Report().StateHash == source.Hashes[boundary]);
                    Console.WriteLine($"REPLAY_CHECKPOINT {recipe} tick={boundary} bytes={saved.Length} restoreMs={restored.RestoreTime.TotalMilliseconds:F2}");
                    for (long tick = boundary + 1; tick <= ticks; tick++) { restored.World.Step(source.Commands[tick]); Check(restored.World.Report().StateHash == source.Hashes[tick], "Recovered continuation differs at " + tick); }
                }
                Console.WriteLine($"REPLAY_RECOVERY {recipe} ticks={ticks} bytes={source.Journal.Length} hash={source.Hashes[ticks]}");
            });
        foreach (var (recipe, ticks, boundary) in new[] { ("production", 320, 12), ("aviation", 420, 185), ("sensors", 48, 12) })
            test("NET4.2 runtime snapshot continues every hash: " + recipe, () => {
                var history = Create(recipe, ticks);
                foreach (int at in new[] { 0, boundary, ticks })
                {
                    using var original = World(recipe); original.RestrictCommandsToPlayers();
                    for (int tick = 1; tick <= at; tick++) original.Step(history.Commands[tick]);
                    byte[] data = original.CaptureSnapshot();
                    Check(data.AsSpan().SequenceEqual(original.CaptureSnapshot()), "Snapshot is not canonical");
                    using var restored = SimulationWorld.RestoreSnapshot(data, () => World(recipe, 2));
                    Check(restored.Report() == original.Report());
                    Console.WriteLine($"RUNTIME_SNAPSHOT {recipe} tick={at} bytes={data.Length}");
                    for (int tick = at + 1; tick <= ticks; tick++)
                    { restored.Step(history.Commands[tick]); Check(restored.Report().StateHash == history.Hashes[tick], $"Runtime {recipe} continuation differs at {tick}"); }
                    Reject(() => SimulationWorld.RestoreSnapshot(data[..^1], () => World(recipe)));
                    byte[] bad = data.ToArray(); bad[^1] ^= 1;
                    Reject(() => SimulationWorld.RestoreSnapshot(bad, () => World(recipe)));
                    Reject(() => SimulationWorld.RestoreSnapshot(data, () => World(recipe == "aviation" ? "production" : "aviation")));
                }
            });
        test("NET4.2 runtime snapshot restores in a fresh operating-system process", () => {
            var history = Create("production", 30); using var world = World("production"); world.RestrictCommandsToPlayers();
            for (int tick = 1; tick <= 12; tick++) world.Step(history.Commands[tick]);
            string path = Path.Combine(Path.GetTempPath(), "openrts-runtime-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllBytes(path, world.CaptureSnapshot());
                var start = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true };
                if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") start.ArgumentList.Add(typeof(ReplayChecks).Assembly.Location);
                start.ArgumentList.Add("--restore-runtime=" + path);
                using var child = Process.Start(start)!;
                if (!child.WaitForExit(15000)) { child.Kill(entireProcessTree: true); throw new TimeoutException("Runtime restore process timed out"); }
                string output = child.StandardOutput.ReadToEnd(), errors = child.StandardError.ReadToEnd();
                Check(child.ExitCode == 0 && output.Contains($"RUNTIME_RESTORED 12 {history.Hashes[12]}"), errors + output);
            }
            finally { File.Delete(path); }
        });
        test("NET4 restart in another process restores the selected boundary", () => {
            var history = Create("production", 32); string path = Path.Combine(Path.GetTempPath(), "openrts-recovery-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var source = new MemoryStream(history.Journal)) using (var target = File.Create(path)) ReplayCheckpoint.Capture(source, 12, target);
                var start = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true };
                if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") start.ArgumentList.Add(typeof(ReplayChecks).Assembly.Location);
                start.ArgumentList.Add("--restore-checkpoint=" + path);
                using var child = Process.Start(start)!;
                if (!child.WaitForExit(15000)) { child.Kill(entireProcessTree: true); throw new TimeoutException("Recovery process timed out"); }
                string output = child.StandardOutput.ReadToEnd(), errors = child.StandardError.ReadToEnd();
                Check(child.ExitCode == 0 && output.Contains($"RESTORED 12 {history.Hashes[12]}"), errors + output);
            }
            finally { File.Delete(path); }
        });
        test("NET4 rejects truncation, corruption, quotas, wrong world and cancelled recovery", () => {
            var history = Create("production", 20);
            using var source = new MemoryStream(history.Journal); using var target = new MemoryStream(); ReplayCheckpoint.Capture(source, 12, target);
            byte[] data = target.ToArray();
            foreach (var bad in new[] { data[..^1], data[..20], data.Concat(new byte[] { 1 }).ToArray() }) Reject(() => ReplayCheckpoint.Restore(new MemoryStream(bad), info => World(info.Recipe)));
            var corrupt = data.ToArray(); corrupt[^10] ^= 1; Reject(() => ReplayCheckpoint.Restore(new MemoryStream(corrupt), info => World(info.Recipe)));
            Reject(() => ReplayCheckpoint.Restore(new MemoryStream(data), _ => World("aviation")));
            Reject(() => ReplayCheckpoint.Restore(new MemoryStream(data), info => World(info.Recipe), new(MaximumBytes: 64)));
            Reject(() => StarJournal.Inspect(new MemoryStream(history.Journal), new(MaximumTicks: 10)));
            Reject(() => StarJournal.Inspect(new MemoryStream(history.Journal[..^5])));
            using var stop = new CancellationTokenSource(); stop.Cancel(); Reject(() => ReplayCheckpoint.Restore(new MemoryStream(data), info => World(info.Recipe), cancellationToken: stop.Token));
            Reject(() => ReplayCheckpoint.Restore(new MemoryStream(data), info => { Thread.Sleep(30); return World(info.Recipe); }, new(CatchUpTimeout: TimeSpan.FromMilliseconds(5))));
        });
        test("NET4 retains explicit v2 replay and detects a changed middle checkpoint", () => {
            var history = Create("production", 16);
            using var input = new MemoryStream(history.Journal); using var reader = new BinaryReader(input);
            int header = reader.ReadInt32(); _ = reader.ReadBytes(header);
            using var legacy = new MemoryStream(); using var writer = new BinaryWriter(legacy, System.Text.Encoding.UTF8, true);
            var oldHeader = Wire.Pack(w => { w.Write((byte)40); w.Write(2); Wire.Text(w, Wire.Hash(DirectMatch.Descriptor(history.Descriptor))); Wire.Text(w, history.Hashes[0]); });
            writer.Write(oldHeader.Length); writer.Write(oldHeader); writer.Write(history.Journal.AsSpan((int)input.Position));
            legacy.Position = 0; Check(StarJournal.Replay(legacy, history.Descriptor, () => World("production")).StateHash == history.Hashes[16]);
            legacy.Position = 0; Reject(() => StarJournal.ReadInfo(legacy));
            input.Position = 0; using var tampered = new MemoryStream(); using var changed = new BinaryWriter(tampered, System.Text.Encoding.UTF8, true);
            while (input.Position < input.Length)
            {
                int size = reader.ReadInt32(); byte[] frame = reader.ReadBytes(size);
                if (frame[0] == StarProtocol.Hash && StarProtocol.ReadCheck(frame, StarProtocol.Hash).Tick == 5) frame = StarProtocol.Check(StarProtocol.Hash, 5, new string('A', 64));
                changed.Write(frame.Length); changed.Write(frame);
            }
            tampered.Position = 0;
            try { StarJournal.Replay(tampered, history.Descriptor, () => World("production")); throw new Exception("Changed world hash was accepted"); }
            catch (DesyncException error) { Check(error.Tick == 5); }
        });
        test("NET4 owner playback pauses, steps, seeks backwards and validates end", () => {
            var history = Create("production", 24); string path = Path.Combine(Path.GetTempPath(), "openrts-playback-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(path, history.Journal);
            try
            {
                using var session = new ReplaySession(path, info => World(info.Recipe));
                void Wait(Func<bool> ready)
                {
                    var clock = Stopwatch.StartNew();
                    while (!ready())
                    {
                        if (session.Completion.IsFaulted) session.Completion.GetAwaiter().GetResult();
                        while (session.TryReadEvent(out _)) { }
                        if (clock.Elapsed.TotalSeconds > 10) throw new TimeoutException("Playback stalled: " + session.Status);
                        Thread.Sleep(5);
                    }
                }
                Wait(() => session.Info is not null && session.Frames.AcquireLatest() is { } frame && Dispose(frame));
                Check(session.IsPaused && session.Position == 0); session.StepOnce(); Wait(() => session.Position == 1);
                session.Seek(15); Wait(() => session.Position == 15 && !session.IsSeeking);
                session.Seek(3); Wait(() => session.Position == 3 && !session.IsSeeking); Check(session.IsPaused);
                session.SetPlayback(8, false); Wait(() => session.Status == "Конец повтора"); Check(session.Position == 24 && session.IsPaused);
                session.Seek(0); Wait(() => session.Position == 0 && !session.IsSeeking);
                Reject(() => session.Seek(25));
                static bool Dispose(FrameExchange.FrameLease frame) { frame.Dispose(); return true; }
            }
            finally { File.Delete(path); }
        });
    }
}
