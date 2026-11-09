using OpenRTS.Content;
using OpenRTS.Core;
using OpenRTS.Core.Orders;
using OpenRTS.Core.Visibility;

internal static class SensorChecks
{
    private const string Radar = "radar.vehicle";
    private static readonly PlayerDefinition[] Players = [new(1, "Blue"), new(2, "Red"), new(3, "Green")];

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    internal static void Run(Action<string, Action> test)
    {
        test("Sensors: YAML compiles profiles and affects the gameplay fingerprint", () =>
        {
            UnitCatalog Load(int range) => UnitCatalog.Load([new("sensors", $$"""
                scout:
                  Health: {HP: 100}
                  Sensors:
                    Signatures: [visual, radar.vehicle]
                    Detectors:
                      - {Channel: radar, Signature: radar.vehicle, Range: {{range}}, Display: Identified, States: true}
                """)]);
            var first = Load(2048);
            var detector = first.Units[0].Sensors!.Detectors[0];
            Check(detector.Display == SensorDisplay.Identified && detector.Fields == LiveObservationFields.States &&
                first.Summary != Load(2049).Summary, "sensor profile was not compiled or fingerprinted");
        });

        test("Sensors: radar continuity preserves type, while live fields are sampled per frame", () =>
        {
            using var world = SimulationWorld.CreateWithMechanics(new() { FogCellSize = 1024 },
                [
                    new("radar", 100, body: BodyShape.None, sensors: new(detectors: [new("radar", Radar, 8192)])),
                    new("scanner", 100, body: BodyShape.None, sensors: new(detectors:
                        [new("radar", Radar, 8192, fields: LiveObservationFields.All)])),
                    new("target", 100, states: ["lit"], body: BodyShape.None),
                    new("observer", 100, body: BodyShape.None, visionRadius: 2048)
                ], 16384, 8192, players: Players);
            var radar = world.AddInitialUnit(0, new(1024, 2048), 1);
            var target = world.AddInitialUnit(2, new(8192, 2048), 2);
            world.GrantSignature(target, Radar);
            var frames = new FrameExchange();
            frames.TryPublish(world);
            using var old = frames.AcquireLatest(1)!;
            var first = old.Contacts[0];
            Check(old.Poses.Length == 1 && first.Knowledge.UnitType is null && first.Health is null && first.States is null &&
                old.FogStates[2 * old.FogColumns + 8] == CellVisibility.Unknown, "anonymous radar revealed model, terrain or fields");
            using (var other = frames.AcquireLatest(3)!)
                Check(other.Poses.Length == 0 && other.Contacts.Length == 0, "contact was shared with another player");

            world.Step([new(1, 1, 1, CommandKind.Aim, radar, TargetEntity: target)]);
            Check(world.Events.ToArray().Any(e => e.Kind == WorldEventKind.CommandDenied), "radar contact enabled an address command");
            world.Step([
                new(2, 0, 1, CommandKind.Spawn, Position: new(8192, 2048), UnitType: 3, Owner: 1),
                new(2, 0, 2, CommandKind.Spawn, Position: new(1024, 3072), UnitType: 1, Owner: 1)
            ]);
            var spawned = world.Events.ToArray();
            var observer = spawned.Single(e => e.Kind == WorldEventKind.Spawned && e.UnitType == 3).Entity;
            var scanner = spawned.Single(e => e.Kind == WorldEventKind.Spawned && e.UnitType == 1).Entity;
            frames.TryPublish(world);
            using (var optical = frames.AcquireLatest(1)!)
                Check(optical.Contacts.Length == 0 && optical.Poses.ToArray().Any(p => p.Id == target), "model did not take precedence");

            world.Step([new(3, 0, 1, CommandKind.Remove, observer), new(3, 2, 2, CommandKind.SetVisualStates, target, States: 1)]);
            frames.TryPublish(world);
            using (var scanned = frames.AcquireLatest(1)!)
            {
                var contact = scanned.Contacts[0];
                Check(contact.Knowledge.Id == first.Knowledge.Id && contact.Knowledge.UnitType == 2 &&
                    contact.Knowledge.ObservedTick > contact.Knowledge.IdentifiedTick && contact.Health == 100 && contact.States == 1,
                    "continuous scan failed to retain type or read live data");
            }
            world.Step([new(4, 0, 1, CommandKind.Remove, scanner)]);
            frames.TryPublish(world);
            using (var radarOnly = frames.AcquireLatest(1)!)
                Check(radarOnly.Contacts[0] is { Health: null, States: null, Knowledge.UnitType: 2 }, "live fields survived the scanner");

            long silence = world.GrantSignature(target, Radar, suppress: true);
            world.SynchronizeVisibility();
            Check(world.RevokeSensorGrant(target, silence), "radar signature grant was not revoked");
            frames.TryPublish(world);
            using var reacquired = frames.AcquireLatest(1)!;
            Check(reacquired.Contacts[0].Knowledge.Id != first.Knowledge.Id && reacquired.Contacts[0].Knowledge.UnitType is null &&
                old.Contacts[0] == first, "track relinked after loss or an old lease changed");
        });

        test("Sensors: terrain and timed concealment compose on a fully visible map", () =>
        {
            var forest = new ConcealmentDefinition("optical", "forest", terrain: 1);
            var cloak = new ConcealmentDefinition("optical", "cloak");
            var grid = new NavigationGrid(8, 4, terrain: Enumerable.Range(0, 32).Select(i => i == 12 ? (byte)1 : (byte)0));
            var locomotor = new Locomotor("test", [TerrainRule.Ground, TerrainRule.Ground]);
            using var world = SimulationWorld.CreateWithMechanics(new() { FogCellSize = 1024, FogMode = FogMode.Visible },
                [
                    new("forest-scan", 100, locomotor: locomotor, body: BodyShape.None, sensors: new(detectors:
                        [new("optical", "visual", 2048, SensorDisplay.Model, ["forest"])])),
                    new("cloak-scan", 100, locomotor: locomotor, body: BodyShape.None, sensors: new(detectors:
                        [new("optical", "visual", 2048, SensorDisplay.Model, ["cloak"])])),
                    new("target", 100, locomotor: locomotor, body: BodyShape.None, sensors: new(concealments: [forest]))
                ], 8192, 4096, navigationGrid: grid, players: Players);
            var scanner = world.AddInitialUnit(0, new(2048, 1024), 1);
            world.AddInitialUnit(1, new(2048, 1024), 1);
            var target = world.AddInitialUnit(2, new(4096, 1024), 2);
            var frames = new FrameExchange();
            frames.TryPublish(world);
            using (var frame = frames.AcquireLatest(1)!)
                Check(frame.FogStates.IsEmpty && frame.Poses.ToArray().Single(p => p.Id == target).Health == 0,
                    "range boundary, live field policy or Visible mode is incorrect");
            Check(!world.IsVisible(3, target), "full terrain vision bypassed concealment");
            world.GrantConcealment(target, cloak, durationTicks: 1);
            world.SynchronizeVisibility();
            Check(!world.IsVisible(1, target), "two different sensors bypassed independent constraints");
            world.Step([]);
            Check(world.IsVisible(1, target), "timed cloak did not expire");

            long a = world.GrantSignature(target, "visual", suppress: true);
            long b = world.GrantSignature(target, "visual", suppress: true);
            Check(!world.RevokeSensorGrant(scanner, a) && world.RevokeSensorGrant(target, a), "wrong grant owner");
            world.SynchronizeVisibility();
            Check(!world.IsVisible(1, target), "one suppression removed another");
            Check(world.RevokeSensorGrant(target, b), "second suppression missing");
            world.Step([new(2, 0, 1, CommandKind.Remove, scanner)]);
            Check(!world.IsVisible(1, target), "removed detector still observed the target");
            world.Step([new(3, 2, 1, CommandKind.Move, target, Position: new(5120, 1024))]);
            for (int i = 0; i < 8; i++) world.Step([]);
            Check(world.IsVisible(1, target), "leaving the forest did not restore ordinary sight");
        });
    }
}
