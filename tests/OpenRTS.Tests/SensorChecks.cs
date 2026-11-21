using OpenRTS.Content;
using OpenRTS.Core;
using OpenRTS.Core.Combat;
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
        test("Sensors: an occupied civilian building gives vision until unloading", () =>
        {
            using var world = SimulationWorld.CreateWithMechanics(new() {FogCellSize = 1024},
                [
                    new("infantry", 100, body: BodyShape.None, visionRadius: 1024, passenger: new("infantry")),
                    new("house", 100, movable: false, body: BodyShape.None, visionRadius: 4096,
                        transport: new(["infantry"], 4)),
                    new("enemy", 100, body: BodyShape.None)
                ], 16384, 16384, players: Players);
            var house = world.AddInitialUnit(1, new(4096, 4096));
            var infantry = world.AddInitialUnit(0, new(4096, 4096), 1);
            var enemy = world.AddInitialUnit(2, new(6144, 4096), 2);
            world.SynchronizeVisibility();
            Check(!world.IsVisible(1, enemy), "empty civilian building supplied vision");
            world.Step([new(1, 1, 1, CommandKind.Enter, infantry, TargetEntity: house)]);
            Check(world.IsVisible(1, enemy), "garrison did not supply vision");
            world.Step([new(2, 1, 1, CommandKind.Unload, house)]);
            Check(!world.IsVisible(1, enemy), "empty house kept player vision");
        });

        test("Sensors: jammer protects its owner and stealth deploys again after movement", () =>
        {
            using var world = SimulationWorld.CreateWithMechanics(new() { FogCellSize = 1024 },
                [
                    new("radar", 100, body: BodyShape.None, visionRadius: 12000, sensors: new(detectors: [new("radar", "radar.ground", 12000)])),
                    new("field", 100, body: BodyShape.None, sensors: new(jamRange: 4096, stealthRange: 4096, deployTicks: 3)),
                    new("target", 100, body: BodyShape.None, sensors: new(["visual", "radar.ground"])),
                    new("counter", 100, body: BodyShape.None, sensors: new(detectors: [new("optical", "visual", 12000, SensorDisplay.Model, ["stealth"])]))
                ], 20000, 16000, players: Players);
            world.AddInitialUnit(0, new(2048, 4096), 1);
            var field = world.AddInitialUnit(1, new(8192, 4096), 2);
            var target = world.AddInitialUnit(2, new(8192, 5120), 2);
            var friendly = world.AddInitialUnit(2, new(8192, 6144), 1);
            world.SynchronizeVisibility();
            Check(world.IsVisible(1, target), "stealth deployed immediately");
            for (int i = 0; i < 3; i++) world.Step();
            Check(!world.IsVisible(1, target) && world.IsVisible(1, friendly), "aura did not respect owner/deploy");
            var frames = new FrameExchange(); frames.TryPublish(world);
            using (var frame = frames.AcquireLatest(1)!) Check(frame.Contacts.Length == 0, "jammed target leaked a radar contact");
            using (var own = frames.AcquireLatest(2)!)
            {
                Check(own.Poses.ToArray().Single(p => p.Id == target).Cloaked, "own cloaked unit lost its visual flag");
                var emitter = own.Poses.ToArray().Single(p => p.Id == field).Emitter;
                Check(emitter.JamRange == 4096 && emitter.StealthRange == 4096 && emitter.HasStealthRotor && !emitter.Moving,
                    "deployed emitter was not projected for its owner");
            }
            world.Step([new(4, 2, 1, CommandKind.Move, field, new(9216, 4096))]);
            Check(world.IsVisible(1, target), "moving generator retained optical stealth");
            for (int i = 0; i < 16; i++) world.Step();
            Check(!world.IsVisible(1, target), "generator did not redeploy");
            world.Step([new(world.Tick + 1, 0, 1, CommandKind.Spawn, Position: new(2048, 5120), UnitType: 3, Owner: 1)]);
            Check(world.IsVisible(1, target), "counter detector did not bypass stealth");
            frames.TryPublish(world);
            using var detected = frames.AcquireLatest(1)!;
            Check(detected.Poses.ToArray().Single(p => p.Id == target).Cloaked &&
                detected.Poses.ToArray().Single(p => p.Id == field).Emitter is { JamRange: 0, StealthRange: 0 },
                "detected cloak was lost or enemy field radii were disclosed");
        });

        test("Sensors: building memory survives hidden destruction until the whole footprint is observed", () =>
        {
            using var world = SimulationWorld.CreateWithMechanics(new() { FogCellSize = 1024 },
                [
                    new("scout", 100, body: BodyShape.None, visionRadius: 8192, sensors: new()),
                    new("building", 100, movable: false, footprint: new(2, 2,
                        [new(0, 0, new(false)), new(1, 0, new(false)), new(0, 1, new(false)), new(1, 1, new(false))]))
                ], 16384, 16384, navigationGrid: new(16, 16), players: Players);
            var building = world.AddInitialUnit(1, new(4096, 4096), 2);
            var scout = world.AddInitialUnit(0, new(2048, 4096), 1);
            var frames = new FrameExchange();
            frames.TryPublish(world);
            world.Step([new(1, 0, 1, CommandKind.Remove, scout)]);
            frames.TryPublish(world);
            using var remembered = frames.AcquireLatest(1)!;
            Check(remembered.Contacts.Length == 1 && remembered.Contacts[0].Knowledge is { Id: 0, UnitType: 1 } &&
                remembered.Contacts[0].Health is null && remembered.Contacts[0].States is null,
                "building memory was lost or retained live fields");
            world.Step([new(2, 0, 1, CommandKind.Destroy, building)]);
            frames.TryPublish(world);
            using (var hidden = frames.AcquireLatest(1)!)
                Check(hidden.Contacts.Length == 1, "hidden destruction removed memory");
            world.Step([new(3, 0, 1, CommandKind.Spawn, Position: new(2048, 4096), UnitType: 0, Owner: 1)]);
            frames.TryPublish(world);
            using var inspected = frames.AcquireLatest(1)!;
            Check(inspected.Contacts.Length == 0 && remembered.Contacts.Length == 1,
                "inspection did not retire the old snapshot or changed an old lease");
        });

        test("Sensors: an HQ built by the crane reveals a distant radar contact until removal", () =>
        {
            var catalog = GameManifest.Load("sandbox/game.yaml").LoadUnits();
            int Type(string id) => catalog.Units.ToList().FindIndex(unit => unit.Id == id);
            using var world = SimulationWorld.CreateWithMechanics(new() { FogCellSize = 1024 }, catalog.Units,
                65536, 32768, navigationGrid: new(64, 32), players: [new(1, "Blue", 10000), new(2, "Red")]);
            var crane = world.AddInitialUnit(Type("crane"), new(2560, 8704), 1);
            var enemy = world.AddInitialUnit(Type("tank"), new(40960, 8704), 2);
            var frames = new FrameExchange();
            frames.TryPublish(world);
            using (var before = frames.AcquireLatest(1)!) Check(before.Contacts.Length == 0, "radar worked before construction");
            world.Step([new(1, 1, 1, CommandKind.PlanConstruction, crane, new(8192, 8704), Action: "site-radar")]);
            for (int i = 0; i < 320; i++) world.Step();
            frames.TryPublish(world);
            EntityId hq;
            using (var frame = frames.AcquireLatest(1)!)
            {
                hq = frame.Poses.ToArray().Single(p => p.UnitType == Type("headquarters")).Id;
                Check(frame.Contacts.Length == 1 && frame.Contacts[0].Knowledge.Position == new WorldPosition(40960, 8704) &&
                    !world.IsVisible(1, enemy), "completed HQ did not discover the target at its radar boundary");
            }
            world.Step([new(world.Tick + 1, 0, 1, CommandKind.Remove, hq)]);
            frames.TryPublish(world);
            using var after = frames.AcquireLatest(1)!;
            Check(after.Contacts.Length == 0, "removed HQ kept revealing targets");
        });

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

        test("Sensors: contact attack checks weapon threshold and fires without an entity lock", () =>
        {
            (SensorDisplay Minimum, SensorDisplay Observed, int MinRange, bool Fires)[] cases =
            [
                (SensorDisplay.Contact, SensorDisplay.Contact, 0, true),
                (SensorDisplay.Classified, SensorDisplay.Contact, 0, false),
                (SensorDisplay.Classified, SensorDisplay.Classified, 0, true),
                (SensorDisplay.Identified, SensorDisplay.Classified, 0, false),
                (SensorDisplay.Identified, SensorDisplay.Identified, 0, true),
                (SensorDisplay.Model, SensorDisplay.Identified, 0, false),
                (SensorDisplay.Identified, SensorDisplay.Identified, 4096, false)
            ];
            foreach (var (minimum, observed, minRange, fires) in cases)
            {
                var weapon = new WeaponDefinition(4096, 4096, 25, 40, windupTicks: 0, minimumContact: minimum, minRange: minRange);
                using var world = SimulationWorld.CreateWithMechanics(new() { FogCellSize = 1024 },
                    [
                        new("gun", 100, ["idle", "move", "fire"], weapon: weapon, body: BodyShape.None, sensors: new(detectors: [new("radar", Radar, 8192, observed)])),
                        new("target", 100, sensors: new(signatures: ["visual", Radar]))
                    ], 16384, 8192, players: Players);
                world.RestrictCommandsToPlayers();
                var gun = world.AddInitialUnit(0, new(1024, 2048), 1);
                var target = world.AddInitialUnit(1, new(4096, 2048), 2);
                var frames = new FrameExchange();
                frames.TryPublish(world);
                using var frame = frames.AcquireLatest(1)!;
                var contact = frame.Contacts[0].Knowledge;
                Check(contact.Level == observed && contact.Class == (observed == SensorDisplay.Contact ? ContactClass.Unknown : ContactClass.Infantry) &&
                    frame.Poses.Length == 1, "wrong contact disclosure");
                var attack = new WorldCommand(1, 1, 1, CommandKind.AttackContact, gun) { ContactId = contact.Id };
                world.Step([attack with { Issuer = 3, Owner = 1 }]);
                Check(world.Events.ToArray().Any(e => e.Kind == WorldEventKind.CommandDenied), "another player could use this contact");
                world.Step([attack with { Tick = 2 }]);
                var events = world.Events.ToArray();
                Check(events.Any(e => e.Kind == WorldEventKind.WeaponFired) == fires, $"unexpected fire for {minimum}/{observed}");
                if (minRange > 0)
                {
                    frames.TryPublish(world);
                    using var held = frames.AcquireLatest(1)!;
                    Check(held.Poses[0].Current.X == 1024, "The contact order moved closer inside MinRange.");
                }
                if (fires)
                {
                    var shot = events.Single(e => e.Kind == WorldEventKind.WeaponFired);
                    Check(shot.TargetEntity == default && shot.Position == contact.Position &&
                        events.Any(e => e.Kind == WorldEventKind.Damaged && e.Entity == target && e.Amount == 25),
                        "contact fire used an entity lock or did not trace the shot");
                }
            }
        });

        test("Sensors: losing a radar contact stops attack and a launched missile holds the measured destination", () =>
        {
            var weapon = new WeaponDefinition(4096, 4096, 25, 1, windupTicks: 0, projectile: ProjectileKind.Missile,
                projectileSpeed: 512, minimumContact: SensorDisplay.Contact);
            using var world = SimulationWorld.CreateWithMechanics(new() { FogCellSize = 1024 },
                [
                    new("gun", 100, ["idle", "move", "fire"], weapon: weapon, body: BodyShape.None),
                    new("radar", 100, body: BodyShape.None, sensors: new(detectors: [new("radar", Radar, 8192)])),
                    new("target", 100, sensors: new(signatures: ["visual", Radar]))
                ], 16384, 8192, speedPerTick: 1024, players: Players);
            var gun = world.AddInitialUnit(0, new(1024, 2048), 1);
            var radar = world.AddInitialUnit(1, new(1024, 3072), 1);
            var target = world.AddInitialUnit(2, new(4096, 2048), 2);
            var frames = new FrameExchange();
            frames.TryPublish(world);
            using var frame = frames.AcquireLatest(1)!;
            var contact = frame.Contacts[0].Knowledge;
            world.Step([new WorldCommand(1, 1, 1, CommandKind.AttackContact, gun) { ContactId = contact.Id }]);
            Check(world.ProjectileCount == 1, "missile was not launched");
            world.Step([new(2, 0, 1, CommandKind.Remove, radar), new(2, 2, 1, CommandKind.Move, target, new(4096, 6144))]);
            var events = world.Events.ToArray().ToList();
            for (int i = 0; i < 8; i++) { world.Step(); events.AddRange(world.Events.ToArray()); }
            Check(events.All(e => e.Kind is not (WorldEventKind.WeaponFired or WorldEventKind.Damaged)) &&
                events.Any(e => e.Kind == WorldEventKind.ProjectileImpact && e.Position == contact.Position && e.TargetEntity == default),
                "lost contact kept firing or the missile chased a hidden entity");
        });
    }
}
