using DeclarativeContent;
using Godot;
using OpenRTS.Content;
using OpenRTS.Core;
using OpenRTS.Core.Combat;
using OpenRTS.Godot.Authoring;
using OpenRTS.Godot.Rendering;

/// <summary>Executed by Godot itself: tests scene serialization, transforms and headless independent output.</summary>
public partial class MapAuthoringChecks : Node
{
    internal const string Sample = "res://examples/maps/AuthoringMap.tscn";
    private int _checks;

    public override void _Ready()
    {
        try
        {
            CheckIsometricGrenade();
            if (OS.GetCmdlineUserArgs().Contains("--verify-grenade-release"))
            {
                GetTree().Quit();
                return;
            }
            Run();
            MapTerrainChecks.Run(Check);
            CheckVisualLayers2D();
            CheckDiagonalRelief();
            CheckDiagonalCliffMap();
            CheckAircraftOnReliefMaps();
            CheckDirectFire();
            CheckIndirectFireMap();
            CheckIndirectFireMap(isometric: true);
            GD.Print($"OPENRTS_AUTHORING_CHECKS:{_checks} passed"); GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
        finally { foreach (Node child in GetChildren()) child.Free(); }
    }

    private void CheckIsometricGrenade()
    {
        var manifest = GameManifest.Load(ProjectSettings.GlobalizePath("res://isometric-game.yaml"));
        var catalog = manifest.LoadUnits();
        var models = manifest.LoadModels();
        int unit = catalog.Units.ToList().FindIndex(u => u.Id == "rifleman");
        var type = catalog.Units[unit];
        var position = new WorldPosition(4096, 4096);
        var target = position with { X = position.X + 2048 };
        var sprites = new SpriteWorldView(catalog, models);
        var effectCatalog = EffectCatalog.Load(manifest.Effects);
        var effects = new CombatEffectsView(catalog, effectCatalog, models);
        AddChild(sprites.Node); AddChild(effects.Node);
        var source = GD.Load<SpriteFrames>("res://resources/units/isometric/swat-rifleman.tres");
        var projectiles = new ProjectilePose[1];
        bool graphics = DisplayServer.GetName() != "headless";

        Vector4 AtlasRegion(SimulationWorld world, string atlas, float alpha)
        {
            var poses = new EntityPose[world.Count];
            var mounts = new WeaponPose[world.WeaponCount];
            world.CopyPoses(poses); world.CopyWeaponPoses(mounts);
            sprites.Apply(poses.AsSpan(0, 1), alpha, world.Tick, world.TickRate, mounts);
            var batch = sprites.Node.GetChildren().OfType<MultiMeshInstance3D>().Single(n => n.Multimesh.VisibleInstanceCount == 1);
            var material = (ShaderMaterial)batch.Multimesh.Mesh.SurfaceGetMaterial(0);
            Check(material.GetShaderParameter("sprite_texture").As<Texture2D>().ResourcePath.EndsWith("/swat-" + atlas + ".png"),
                $"Rifleman at tick {world.Tick} used the wrong atlas, expected {atlas}.");
            return material.GetShaderParameter("uv_rect").AsVector4();
        }

        Vector4 FrameRegion(string clip, int frame)
        {
            var texture = (AtlasTexture)source.GetFrameTexture(clip, frame);
            var rect = texture.Region;
            var size = texture.Atlas.GetSize();
            return new(rect.Position.X / size.X, rect.Position.Y / size.Y, rect.Size.X / size.X, rect.Size.Y / size.Y);
        }

        using var world = new SimulationWorld(catalog.Units, 12288, 12288);
        var id = world.AddInitialUnit(unit, position, 1);
        world.Step([new(1, 0, 1, CommandKind.UseAbility, id, target, Action: "grenade")]);
        Check(world.ProjectileCount == 0 && AtlasRegion(world, "throw-windup", 0).IsEqualApprox(FrameRegion("throw-windup-2", 0)),
            "The eastward throw did not begin on its first held-grenade frame.");
        var mounts = new WeaponPose[type.Weapons.Count];
        world.CopyWeaponPoses(mounts);
        var grenade = mounts.Single(p => type.Weapons[p.Mount].Id == "grenade");
        long release = grenade.WindupEndTick;
        Check(release == 19, "The isometric throw must wind up for 18 ticks.");
        while (world.Tick < release - 1)
        {
            world.Step();
            Check(world.ProjectileCount == 0, "Grenade appeared before the release tick.");
        }
        Check(AtlasRegion(world, "throw-windup", 1).IsEqualApprox(
            FrameRegion("throw-windup-2", source.GetFrameCount("throw-windup-2") - 1)),
            "The last pre-release snapshot must still display the grenade in hand.");
        world.Step();
        Check(world.Tick == release && world.ProjectileCount == 1 && world.Events.ToArray().Count(e => e.Kind == WorldEventKind.WeaponFired) == 1 &&
            AtlasRegion(world, "throw-release", 0).IsEqualApprox(FrameRegion("throw-release-2", 0)),
            "The projectile and first empty-hand frame must appear on the same tick.");
        world.CopyProjectilePoses(projectiles);
        effects.Apply(projectiles, 0, world.Tick);
        var shot = effects.Node.GetNode<MultiMeshInstance3D>("Projectiles").Multimesh;
        var local = models.Models["infantry-rifleman"].Animations["throw"].LaunchLocal!.Value;
        var origin = new Vector3(4, SpriteWorldView.OverlayHeight, 4);
        var expected = origin + new Vector3(-(float)local.Z, (float)local.Y, (float)local.X) * 0.75f;
        var projectile = projectiles[0];
        var launch = effects.LaunchVisual(unit, projectile.Mount, projectile.Departure, effectCatalog.Weapons["iso-grenade"], Vector3.Right);
        Check(shot.VisibleInstanceCount == 1 && launch is { } departure && departure.Center.IsEqualApprox(expected),
            $"The projectile did not start at the east-facing release hand. Expected {expected}, got {launch?.Center}, visible count {shot.VisibleInstanceCount}.");
        if (graphics) Check(shot.GetInstanceTransform(0).Origin.IsEqualApprox(expected), "The rendered grenade did not start in the hand.");

        world.Step([new(release + 1, 0, 2, CommandKind.Move, id, position with { Y = position.Y + 96 })]);
        AtlasRegion(world, "travel", 0);
        var flight = new List<Vector3> { expected };
        while (world.ProjectileCount > 0)
        {
            world.CopyProjectilePoses(projectiles);
            effects.Apply(projectiles, 1, world.Tick);
            if (graphics) flight.Add(shot.GetInstanceTransform(0).Origin);
            world.Step();
        }
        Check(world.Events.ToArray().Any(e => e.Kind == WorldEventKind.ProjectileImpact && e.Position == target),
            "The released grenade lost its original target after a move order.");
        AtlasRegion(world, "stand", 0);
        if (graphics)
        {
            float peak = flight.Max(p => p.Y);
            Check(peak > expected.Y + 0.3f && flight[^1].Y < peak - 0.5f, "The grenade did not rise and then descend.");
            Check(flight.Zip(flight.Skip(1)).All(pair => pair.First.X < pair.Second.X &&
                MathF.Abs(pair.First.Y - pair.Second.Y) < 0.55f), "The grenade turned back or snapped vertically.");
        }

        using var cancelled = new SimulationWorld(catalog.Units, 12288, 12288);
        id = cancelled.AddInitialUnit(unit, position, 1);
        cancelled.Step([new(1, 0, 1, CommandKind.UseAbility, id, target, Action: "grenade")]);
        cancelled.Step([new(2, 0, 2, CommandKind.Stop, id)]);
        AtlasRegion(cancelled, "stand", 0);
        while (cancelled.Tick <= release) cancelled.Step();
        Check(cancelled.ProjectileCount == 0, "Cancelling windup left a ghost throw.");

        using var shooting = new SimulationWorld(catalog.Units, 12288, 12288);
        var shooter = shooting.AddInitialUnit(unit, position, 1);
        var enemy = shooting.AddInitialUnit(unit, target, 2);
        shooting.Step([new(1, 0, 1, CommandKind.AttackTarget, shooter, TargetEntity: enemy)]);
        WorldEvent fired = default;
        for (int tick = 1; tick <= 12; tick++)
        {
            fired = shooting.Events.ToArray().FirstOrDefault(e => e.Kind == WorldEventKind.WeaponFired && e.Entity == shooter);
            if (fired.Departure.Tick > 0) break;
            shooting.Step();
        }
        Check(fired.Departure.Tick > 0 && AtlasRegion(shooting, "shoot", 0).IsEqualApprox(FrameRegion("shoot-2", 0)),
            "The first firing frame did not face the target.");
        var muzzle = models.Models["infantry-rifleman"].Parts.Single(p => p.Id == "body").MuzzleLocal!.Value;
        var muzzleEast = origin + new Vector3(-(float)muzzle.Z, (float)muzzle.Y, (float)muzzle.X) * 0.75f;
        var rifleLaunch = effects.LaunchVisual(unit, fired.Mount, fired.Departure, effectCatalog.Weapons["rifleman"], Vector3.Right);
        Check(rifleLaunch is { } barrel && barrel.Center.IsEqualApprox(muzzleEast),
            "The rifle launch point did not follow the firing direction independently of the body.");
        if (graphics)
        {
            effects.Accept(fired); effects.Apply([], 0, shooting.Tick);
            var tracers = effects.Node.GetNode<MultiMeshInstance3D>("Tracers").Multimesh;
            var trace = tracers.GetInstanceTransform(0);
            Check(tracers.VisibleInstanceCount == 1 && (trace.Origin + trace.Basis.Z / 2).IsEqualApprox(muzzleEast),
                "The rendered tracer did not start at the muzzle.");
        }
        GD.Print("OPENRTS_GRENADE_RELEASE:PASS windup, release, arc, cancellation, movement and rifle muzzle");
    }

    private void CheckVisualLayers2D()
    {
        var root = GD.Load<PackedScene>("res://examples/maps/RockyPass.tscn").Instantiate<OpenRtsMap2D>();
        AddChild(root);
        var catalog = GameManifest.Load(ProjectSettings.GlobalizePath(root.ContentManifest)).LoadUnits();
        var original = MapCompiler.Compile(MapSceneExtractor2D.Extract(root), catalog);
        var terrain = root.Terrain!;
        var ramp = new Vector2I(15, 14);
        var atlas = terrain.GetCellAtlasCoords(ramp);
        int source = terrain.GetCellSourceId(ramp);
        TileMapLayer Overlay(OpenRtsMap2D map, Vector2I at, Vector2I tile)
        {
            var overlay = new TileMapLayer { TileSet = map.Terrain!.TileSet };
            map.AddChild(overlay);
            overlay.Owner = map;
            overlay.SetCell(at, 0, tile);
            return overlay;
        }
        var lower = Overlay(root, ramp, atlas);
        var upper = Overlay(root, ramp, Vector2I.Zero);
        root.VisualLayers = [lower, upper];
        var colored = MapCompiler.Compile(MapSceneExtractor2D.Extract(root), catalog);
        Check(colored.GameplayFingerprint == original.GameplayFingerprint && colored.DataFingerprint != original.DataFingerprint &&
            colored.Map.VisualAt(ramp.X, ramp.Y) == "grass" && colored.Source.Surface!.SequenceEqual(original.Source.Surface!),
            "The last visual paint must win without replacing the gameplay ramp.");
        root.VisualLayers = [upper, lower];
        Check(MapCompiler.Compile(MapSceneExtractor2D.Extract(root), catalog).Write() == original.Write(),
            "Swapping visual layer priority did not restore the original appearance.");
        using var packed = new PackedScene();
        Check(packed.Pack(root) == Error.Ok, "Visual layers could not be serialized.");
        var reopened = packed.Instantiate<OpenRtsMap2D>();
        try
        {
            Check(reopened.VisualLayers.Count == 2 && MapCompiler.Compile(MapSceneExtractor2D.Extract(reopened), catalog).Write() == original.Write(),
                "Visual layer order did not survive serialization.");
        }
        finally { reopened.Free(); }
        var seams = MapReliefDiagnostics.Scan(original.Map.Grid).ToHashSet();
        terrain.SetCell(ramp, source, Vector2I.Zero);
        var changed = MapCompiler.Compile(MapSceneExtractor2D.Extract(root), catalog);
        Check(MapReliefDiagnostics.Scan(changed.Map.Grid).Except(seams).Any(edge => edge.Ramp && (edge.Cell == ramp || edge.Neighbor == ramp)),
            "Replacing a ramp tile did not flag its newly discontinuous seam.");
        terrain.SetCell(ramp, source, atlas, (int)TileSetAtlasSource.TransformFlipH);
        Reject(() => MapSceneExtractor2D.Extract(root), "A visual layer hid an invalid flipped gameplay ramp.");

        var cliff = GD.Load<PackedScene>("res://examples/maps/DiagonalCliffs.tscn").Instantiate<OpenRtsMap2D>();
        AddChild(cliff);
        var cliffCatalog = GameManifest.Load(ProjectSettings.GlobalizePath(cliff.ContentManifest)).LoadUnits();
        var before = MapCompiler.Compile(MapSceneExtractor2D.Extract(cliff), cliffCatalog);
        var cut = cliff.Terrain!.GetUsedCells().First(cell => cliff.Terrain.GetCellTileData(cell).GetCustomData("UpperTerrainId").AsString() == "mud");
        cliff.VisualLayers = [Overlay(cliff, cut, new(6, 0))];
        var after = MapCompiler.Compile(MapSceneExtractor2D.Extract(cliff), cliffCatalog);
        Check(after.Map.UpperVisualAt(cut.X, cut.Y) == "water" && after.GameplayFingerprint == before.GameplayFingerprint &&
            after.DataFingerprint != before.DataFingerprint, "Visual styling of the upper triangle changed gameplay.");
    }

    private void CheckDirectFire()
    {
        var manifest = GameManifest.Load(ProjectSettings.GlobalizePath("res://isometric-game.yaml"));
        var catalog = manifest.LoadUnits();
        var models = manifest.LoadModels();
        var effects = EffectCatalog.Load(manifest.Effects);
        var view = new CombatEffectsView(catalog, effects, models);
        AddChild(view.Node);
        var units = catalog.Units.ToList();
        int targetType = units.FindIndex(unit => unit.Id == "rifleman");
        var rounds = new ProjectilePose[1];
        foreach (var name in new[] { "tank", "rocketeer" })
        {
            int type = units.FindIndex(unit => unit.Id == name);
            var weapon = units[type].Weapons[0].Weapon;
            var style = effects.Weapons[weapon.Id];
            var batch = view.Node.GetChildren().OfType<MultiMeshInstance3D>().Single(node =>
                (node.Multimesh.Mesh.SurfaceGetMaterial(0) as StandardMaterial3D)?.AlbedoTexture?.ResourcePath == style.ProjectileTexture)
                .Multimesh;
            view.Clear();
            using var world = SimulationWorld.CreateWithMechanics(new() { FogCellSize = 1024 },
                catalog.Units, 12288, 12288, players: [new(1, "Blue"), new(2, "Red"), new(3, "Green")]);
            var source = world.AddInitialUnit(type, new(4096, 4096), 1);
            var target = world.AddInitialUnit(targetType, new(8192, 4096), 2);
            world.Step([new(1, 0, 1, CommandKind.AttackTarget, source, TargetEntity: target)]);
            while (world.ProjectileCount == 0 && world.Tick <= 15) world.Step();
            Check(world.ProjectileCount == 1, name + ": direct fire fixture must launch one shot.");
            var fired = world.Events.ToArray().Single(e => e.Kind == WorldEventKind.WeaponFired && e.Entity == source);
            world.CopyProjectilePoses(rounds);
            var shot = rounds[0];
            var muzzle = view.LaunchVisual(type, shot.Mount, shot.Departure, style, Vector3.Right);
            view.Apply(rounds, 1, world.Tick);
            var previous = Center();
            Check(shot.Current == fired.SourcePosition && muzzle.HasValue && previous.IsEqualApprox(muzzle.Value.Center),
                name + ": the first shot must use the simulated and rendered launch anchors.");

            for (int i = 0; i < 5; i++)
            {
                world.Step();
                world.CopyProjectilePoses(rounds);
                view.Apply(rounds, 0, world.Tick);
                Check(Center().IsEqualApprox(previous), name + ": the projectile jumped between published frames.");
                view.Apply(rounds, 1, world.Tick);
                var current = Center();
                Check(current.X > previous.X, name + ": projectile moved away from its target in the initial flight.");
                previous = current;
            }
            world.Step([new(world.Tick + 1, 0, 2, CommandKind.Remove, source),
                new(world.Tick + 1, 0, 3, CommandKind.Spawn, Position: new(8192, 6144), UnitType: targetType, Owner: 3)]);
            var exchange = new FrameExchange();
            Check(exchange.TryPublish(world), "Could not publish the flight after its shooter was removed.");
            using var observed = exchange.AcquireLatest(viewer: 2)!;
            Check(!observed.Poses.ToArray().Any(p => p.Id == source) && observed.Projectiles.Length == 1 &&
                observed.Projectiles[0].LaunchProjection is { } anchor && anchor.Origin == fired.SourcePosition,
                name + ": the observer lost the previously seen launch anchor when the shooter disappeared.");
            using var lateObserver = exchange.AcquireLatest(viewer: 3)!;
            Check(lateObserver.Projectiles.Length == 1 && lateObserver.Projectiles[0].Departure == default &&
                lateObserver.Projectiles[0].LaunchProjection is { Origin: null },
                name + ": a viewer who missed the launch must receive altitude data without its origin.");
            view.Apply(observed.Projectiles, 1, world.Tick);
            float height = Center().Y;
            view.Apply(lateObserver.Projectiles, 1, world.Tick);
            Check(Mathf.IsEqualApprox(Center().Y, height),
                name + ": the projectile altitude changed between visible and hidden launch views.");

            Vector3 Center()
            {
                var data = batch.Buffer;
                return new(data[3], data[7], data[11]);
            }
        }
    }

    private void CheckIndirectFireMap(bool isometric = false)
    {
        var root = GD.Load<PackedScene>("res://examples/maps/IndirectFire.tscn").Instantiate<OpenRtsMap2D>();
        AddChild(root);
        var manifest = GameManifest.Load(ProjectSettings.GlobalizePath(isometric ? "res://isometric-game.yaml" : root.ContentManifest));
        var catalog = manifest.LoadUnits();
        var compiled = MapCompiler.Compile(MapSceneExtractor2D.Extract(root), catalog);
        var map = MapCompiler.Read(new("indirect-fire", compiled.Write()), catalog).Map;
        var players = new Dictionary<string, PlayerDefinition> { ["blue"] = new(1, "Blue"), ["red"] = new(2, "Red") };
        using var world = map.Prepare(players).CreateWorld();
        var effects = EffectCatalog.Load(manifest.Effects);
        var view = new CombatEffectsView(catalog, effects, manifest.LoadModels());
        AddChild(view.Node);
        var rounds = new ProjectilePose[128];
        var actors = new EntityPose[world.Count];
        var mounts = new WeaponPose[world.WeaponCount];
        var textures = view.Node.GetChildren().OfType<MultiMeshInstance3D>()
            .Select(batch => (batch.Multimesh, Texture: (batch.Multimesh.Mesh.SurfaceGetMaterial(0) as StandardMaterial3D)?.AlbedoTexture))
            .Where(batch => !string.IsNullOrEmpty(batch.Texture?.ResourcePath))
            .ToDictionary(batch => batch.Texture!.ResourcePath, batch => batch.Multimesh);
        var batches = new[] { "howitzer", "salvo-rocket", "cruise-missile", "ballistic-missile" }.ToDictionary(
            id => id, id => effects.Weapons[id].ProjectileModel is { } model
                ? view.Node.GetNode<MultiMeshInstance3D>(model.GetFile().GetBaseName()).Multimesh
                : textures[effects.Weapons[id].ProjectileTexture!]);
        var smoke = view.Node.GetNode<MultiMeshInstance3D>("RocketSmoke").Multimesh;
        var flashes = view.Node.GetNode<MultiMeshInstance3D>("Impacts").Multimesh;
        var kinds = new HashSet<ProjectileTrajectory>();
        var hits = new HashSet<string>();
        var loaded = new HashSet<string>();
        var emptied = new HashSet<string>();
        var departures = new HashSet<string>();
        var raised = new Dictionary<string, (Vector3 Position, Vector3 Direction)>();
        var pitched = new HashSet<string>();
        var flightSamples = new Dictionary<long, (int Tick, Vector3 Position, float Weight)>();
        var earlyScudSteps = new HashSet<long>();
        int texturedBatches = 0;
        bool descending = false;
        bool checkedMuzzle = false;
        for (int tick = 0; tick < 300; tick++)
        {
            if (tick > 0) world.Step();
            foreach (var e in world.Events) view.Accept(e);
            var active = rounds.AsSpan(0, world.ProjectileCount);
            world.CopyPoses(actors); world.CopyWeaponPoses(mounts); world.CopyProjectilePoses(active);
            view.Apply(active, 1, world.Tick, reliefProjection: true, actors: actors, weapons: mounts);
            foreach (var actor in actors)
            {
                if (actor.WeaponCount == 0) continue;
                var weapon = catalog.Units[actor.UnitType].Weapons[0].Weapon;
                var effect = effects.Weapons[weapon.Id];
                if (effect.MountedRocket is null) continue;
                var channel = mounts[actor.WeaponOffset];
                var batch = batches[weapon.Id];
                if (channel.WindupEndTick == world.Tick + 1)
                {
                    Check(batch.VisibleInstanceCount == 1, weapon.Id + ": ready projectile missing during windup.");
                    raised[weapon.Id] = Instance(batch);
                }
                if (!channel.Reloading && channel.WindupEndTick == 0 && batch.VisibleInstanceCount == 1)
                {
                    Check(MathF.Abs(Instance(batch).Direction.Y) < 0.001f, weapon.Id + ": ready projectile should rest flat.");
                    loaded.Add(weapon.Id);
                }
                if (channel.Reloading && batch.VisibleInstanceCount == 0) emptied.Add(weapon.Id);
            }
            foreach (var shot in active)
            {
                var weapon = catalog.Units[shot.UnitType].Weapons[shot.Mount].Weapon;
                var effect = effects.Weapons[weapon.Id];
                var batch = batches[weapon.Id];
                if (shot.Current.Elevation > 1536) kinds.Add(weapon.Trajectory);
                long age = world.Tick - shot.Departure.Tick;
                var anchor = shot.LaunchProjection!.Value;
                if (age == 0)
                {
                    var fired = world.Events.ToArray().Single(e => e.Kind == WorldEventKind.WeaponFired &&
                        e.Entity == shot.Departure.Source && e.Mount == shot.Mount);
                    Check(shot.Previous == fired.SourcePosition && shot.Current == fired.SourcePosition &&
                        anchor.Origin == fired.SourcePosition && anchor.CurrentWeight == 1,
                        weapon.Id + ": projectile and firing event must share the simulated muzzle.");
                }
                if (!checkedMuzzle && shot.Departure.Tick == world.Tick && weapon.Id == "howitzer")
                {
                    var shotPosition = Instance(batch).Position;
                    var launch = shot.Departure;
                    float body = SpriteWorldView.Angle(launch.BodyFacing, launch.BodyFacing, 1);
                    float aim = SpriteWorldView.Angle(launch.MountFacing, launch.MountFacing, 1);
                    if (isometric)
                    {
                        const float step = MathF.Tau / 32;
                        body = MathF.Floor(Mathf.PosMod(body, MathF.Tau) / step + 0.5f) * step;
                        aim = MathF.Floor(Mathf.PosMod(aim, MathF.Tau) / step + 0.5f) * step;
                    }
                    var offset = new Vector2(0, isometric ? 0.52f : 0.18f).Rotated(body) +
                        new Vector2(0, isometric ? -2.313f : (0.10f - 0.71f) * 1.65f).Rotated(aim);
                    var expected = new Vector3(launch.Origin.X / 1024f,
                        launch.Origin.Elevation / 1024f + SpriteWorldView.OverlayHeight, launch.Origin.Y / 1024f) +
                        new Vector3(offset.X, isometric ? 1.401f : 0, offset.Y) * (isometric ? 0.75f : 1);
                    bool flashAtMuzzle = Enumerable.Range(0, flashes.VisibleInstanceCount)
                        .Any(i => Instance(flashes, i).Position.DistanceTo(expected) < 0.001f);
                    Check(shotPosition.DistanceTo(expected) < 0.001f && flashAtMuzzle,
                        "The first projectile sprite and the flash must share the rotated muzzle position.");
                    checkedMuzzle = true;
                }
                if (shot.Departure.Tick == world.Tick && effect.MountedRocket is { } mount)
                {
                    Check(batch.VisibleInstanceCount == 1 && raised.TryGetValue(weapon.Id, out _), weapon.Id + ": launch did not replace the mounted sprite.");
                    var before = raised[weapon.Id]; var after = Instance(batch);
                    Check(before.Position.DistanceTo(after.Position) < 0.001f && before.Direction.Dot(after.Direction) > 0.9999f &&
                        Mathf.IsEqualApprox(after.Direction.Y, MathF.Sin(Mathf.DegToRad((float)mount.LaunchPitch))),
                        weapon.Id + ": the first flying sprite must match the raised rocket.");
                    if (isometric)
                    {
                        var data = batch.Buffer;
                        Check(Mathf.IsEqualApprox(new Vector3(data[2], data[6], data[10]).Length(), 0.75f),
                            weapon.Id + ": the rocket mesh did not match the scaled launcher.");
                    }
                    var tail = after.Position - after.Direction * (float)effect.Size;
                    var smokeBuffer = smoke.Buffer;
                    bool exhaust = false;
                    for (int i = 0; i < smoke.VisibleInstanceCount; i++)
                    {
                        int n = 16 * i;
                        exhaust |= new Vector3(smokeBuffer[n + 3], smokeBuffer[n + 7], smokeBuffer[n + 11]).DistanceTo(tail) < 0.001f &&
                            Mathf.IsEqualApprox(smokeBuffer[n], (float)effect.SmokeSize);
                    }
                    Check(exhaust, weapon.Id + ": smoke should originate at the rocket tail, with its own scale.");
                    departures.Add(weapon.Id);
                }
                if (batch.VisibleInstanceCount == 1)
                {
                    var actual = Instance(batch);
                    if (flightSamples.TryGetValue(shot.Id, out var before) && before.Tick == tick - 1)
                    {
                        var delta = before.Position.DirectionTo(actual.Position);
                        Check(anchor.PreviousWeight == before.Weight && actual.Direction.Dot(delta) > 0.9999f,
                            weapon.Id + ": flight must remain continuous and the projectile must face the rendered tangent.");
                        if (weapon.Id == "ballistic-missile" && age is >= 1 and <= 6)
                        {
                            Check(delta.Y > 0, $"SCUD dived on flight tick {age}.");
                            if (earlyScudSteps.Add(age) && age == 3)
                            {
                                using var restored = SimulationWorld.RestoreSnapshot(world.CaptureSnapshot(), () => map.Prepare(players).CreateWorld());
                                var restoredShots = new ProjectilePose[restored.ProjectileCount];
                                restored.CopyProjectilePoses(restoredShots);
                                Check(active.SequenceEqual(restoredShots), "A runtime snapshot must keep the launch anchor and its weight mid-flight.");
                                view.Apply([shot with { Departure = default, LaunchProjection = anchor with { Origin = null } }], 1, world.Tick);
                                Check(Mathf.IsEqualApprox(Instance(batch).Position.Y, actual.Position.Y),
                                    "A SCUD revealed from fog must keep the same altitude as a visible launch.");
                                view.Apply(active, 1, world.Tick, reliefProjection: true, actors: actors, weapons: mounts);
                            }
                        }
                        if (actual.Direction.Y > 0.1f) pitched.Add(weapon.Id);
                        descending |= actual.Direction.Y < -0.1f;
                    }
                    flightSamples[shot.Id] = (tick, actual.Position, anchor.CurrentWeight);
                }
            }
            foreach (var e in world.Events)
                if (e.Kind == WorldEventKind.ProjectileImpact)
                {
                    var weapon = catalog.Units[e.UnitType].Weapons[e.Mount].Weapon;
                    if (e.TargetEntity != default || weapon.ScatterRadius > 0 && actors.Any(actor =>
                        actor.Presentation.Owner == 2 &&
                        Math.Abs(actor.Current.Elevation - e.Position.Elevation) <= weapon.SplashRadius + 256 &&
                        ObjectGeometry.WithinRadius(catalog.Units[actor.UnitType], actor.Current, e.Position, weapon.SplashRadius)))
                        hits.Add(weapon.Id);
                }
            texturedBatches = Math.Max(texturedBatches, batches.Values.Count(batch => batch.VisibleInstanceCount > 0));
        }
        string[] launchers = ["ballistic-missile", "cruise-missile"];
        Check(checkedMuzzle, "The indirect-fire fixture never exercised a muzzle anchor.");
        Check(loaded.SetEquals(launchers) && departures.SetEquals(launchers) && emptied.SetEquals(launchers),
            "Both launchers must render the loaded, launch and empty states.");
        Check(pitched.Contains("howitzer") && pitched.Contains("ballistic-missile") && descending,
            "Elevated flight must exercise both climbing and descending sprite pitch.");
        Check(earlyScudSteps.Count == 6, "The SCUD check must cover every tick of its initial climb.");
        Check(kinds.SetEquals([ProjectileTrajectory.Arc, ProjectileTrajectory.Ballistic, ProjectileTrajectory.Cruise]) &&
            hits.SetEquals(["howitzer", "salvo-rocket", "ballistic-missile", "cruise-missile"]) && texturedBatches == 4,
            $"Indirect Fire did not render and hit across the cliff with all four units. Flew {string.Join(", ", kinds)}, hit {string.Join(", ", hits)}, textured {texturedBatches}.");
        view.Apply([], 1, world.Tick + 120);
        Check(smoke.VisibleInstanceCount == 0, "Rocket smoke should expire after flight.");

        static (Vector3 Position, Vector3 Direction) Instance(MultiMesh batch, int index = 0)
        {
            var data = batch.Buffer;
            int n = index * 16;
            return (new(data[n + 3], data[n + 7], data[n + 11]), -new Vector3(data[n + 2], data[n + 6], data[n + 10]).Normalized());
        }
    }

    private void CheckAircraftOnReliefMaps()
    {
        const int size = WorldPosition.UnitsPerCell;
        (string Scene, WorldPosition Start, WorldPosition Target)[] routes =
        [
            ("RockyPass", new(15 * size + 512, 24 * size + 512), new(15 * size + 512, 8 * size + 512)),
            ("DiagonalRamps", new(2 * size + 512, 2 * size + 512), new(14 * size + 512, 14 * size + 512)),
            ("DiagonalCliffs", new(size / 2, 8 * size + 512), new(8 * size + 512, 8 * size + 512)),
        ];
        var poses = new EntityPose[1];
        foreach (var (scene, start, target) in routes)
        {
            var map = GD.Load<PackedScene>($"res://examples/maps/{scene}.tscn").Instantiate<OpenRtsMap2D>();
            AddChild(map);
            var catalog = GameManifest.Load(ProjectSettings.GlobalizePath(map.ContentManifest)).LoadUnits();
            var compiled = MapCompiler.Compile(MapSceneExtractor2D.Extract(map), catalog);
            var grid = MapCompiler.Read(new(scene, compiled.Write()), catalog).Map.Grid;
            var aircraft = catalog.Units.Single(unit => unit.Id == "tiltrotor");
            var flight = aircraft.Flight!;
            using var world = new SimulationWorld([aircraft], grid.Width * size, grid.Height * size, navigationGrid: grid);
            var departure = start with { Elevation = grid.SurfaceElevationAt(start) + flight.CruiseAltitude };
            var id = world.AddInitialUnit(0, departure, owner: 1);
            FlyTo(target);
            FlyTo(departure);
            Check(poses[0].Current == departure, $"{scene}: aircraft did not settle back at its original altitude.");
            map.Free();

            void FlyTo(WorldPosition goal)
            {
                world.Step([new(world.Tick + 1, 0, 1, CommandKind.Move, id, goal)]);
                var expected = goal with { Elevation = grid.SurfaceElevationAt(goal) + flight.CruiseAltitude };
                bool safe = true;
                for (int tick = 0; tick < 240; tick++)
                {
                    world.CopyPoses(poses);
                    var pose = poses[0];
                    safe &= pose.Current.Elevation - flight.HalfHeight >= grid.SurfaceElevationAt(pose.Current) &&
                        Math.Abs(pose.Current.Elevation - pose.Previous.Elevation) <= flight.VerticalSpeed;
                    if (pose.Current == expected) break;
                    world.Step();
                }
                Check(safe && poses[0].Current == expected, $"{scene}: relief flight failed approaching {expected}, current {poses[0].Current}.");
            }
        }
    }

    private void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); _checks++; }
    private void Reject(Action action, string message)
    {
        try { action(); } catch (ContentException) { _checks++; return; }
        throw new InvalidOperationException(message);
    }

    private void CheckDiagonalRelief()
    {
        var map = GD.Load<PackedScene>("res://examples/maps/RockyPass.tscn").Instantiate<OpenRtsMap2D>();
        AddChild(map);
        var layer = map.Terrain!;
        var atlas = (TileSetAtlasSource)layer.TileSet!.GetSource(0);
        var ramp = new Vector2I(2, 0);
        var cell = new Vector2I(28, 21);
        int alternative = atlas.CreateAlternativeTile(ramp);
        try
        {
            var tile = atlas.GetTileData(ramp, alternative);
            tile.SetCustomData("TerrainId", "road");
            tile.SetCustomData("VisualId", "rock-ramp");
            tile.SetCustomData("Elevation", 1024);
            tile.SetCustomData("Rise", new Vector2I(128, -256));
            layer.SetCell(cell, 0, ramp, alternative);
            var catalog = GameManifest.Load(ProjectSettings.GlobalizePath(map.ContentManifest)).LoadUnits();
            var compiled = MapCompiler.Compile(MapSceneExtractor2D.Extract(map), catalog);
            var grid = MapCompiler.Read(new("diagonal relief", compiled.Write()), catalog).Map.Grid;
            int index = grid.Index(new(cell.X, cell.Y));
            Check(grid.SurfaceRiseAt(index) == new OpenRTS.Core.Navigation.SurfaceRise(128, -256) &&
                grid.SurfaceAt(index).Slope == 287, "The TileMapLayer diagonal relief was lost in compilation.");

            var terrain = new TerrainSurface(grid);
            using var mesh = terrain.Mesh;
            Check(mesh.GetSurfaceCount() == 2 && mesh.SurfaceGetMaterial(1) is BaseMaterial3D
                { CullMode: BaseMaterial3D.CullModeEnum.Disabled }, "Rock faces must be visible from both sides.");
            var camera = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Size = 8 };
            AddChild(camera);
            var center = new Vector3(28.5f, 0.5f, 21.5f);
            camera.Position = center + new Vector3(0, 4, 6);
            camera.LookAt(center);
            const int size = WorldPosition.UnitsPerCell;
            var point = new WorldPosition(28 * size + 256, 21 * size + 768, 928);
            Check(terrain.Pick(camera, camera.UnprojectPosition(new(28.25f, 928f / size, 21.75f))) == point,
                "Picking the diagonal surface lost exact XY or height.");
            Check(terrain.Pick(camera, camera.UnprojectPosition(new(28.5f, 0.5f, 22))) is null,
                "An exposed diagonal ramp cliff accepted a ground order.");
            camera.Projection = Camera3D.ProjectionType.Perspective;
            Check(terrain.Pick(camera, camera.UnprojectPosition(new(28.25f, 928f / size, 21.75f))) == point,
                "Perspective picking lost the diagonal ramp position.");
            Check(terrain.Pick(camera, camera.UnprojectPosition(new(40, 0, 21.5f))) is null,
                "A ray outside the terrain returned a ground point.");
        }
        finally
        {
            layer.EraseCell(cell);
            atlas.RemoveAlternativeTile(ramp, alternative);
        }
    }

    private void CheckDiagonalCliffMap()
    {
        var map = GD.Load<PackedScene>("res://examples/maps/DiagonalCliffs.tscn").Instantiate<OpenRtsMap2D>();
        AddChild(map);
        var catalog = GameManifest.Load(ProjectSettings.GlobalizePath(map.ContentManifest)).LoadUnits();
        var source = MapSceneExtractor2D.Extract(map);
        var compiled = MapCompiler.Compile(MapSource.Read(new("diagonal cliffs source", source.Write())), catalog);
        var game = MapCompiler.Read(new("diagonal cliffs artifact", compiled.Write()), catalog).Map;
        var grid = game.Grid;
        Check(source.Surface!.Where(cell => cell.Cut != default).Select(cell => cell.Cut.UpperCorner).Distinct().Count() == 4 &&
            grid.HasSurfaceCuts && !grid.HasSlopedSurface, "The four diagonal cliff tiles did not round-trip.");

        var terrain = new TerrainSurface(grid);
        using var mesh = terrain.Mesh;
        var camera = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Size = 20 };
        AddChild(camera);
        camera.Position = new(8, 15, 8);
        camera.RotationDegrees = new(-90, 0, 0);
        const int size = WorldPosition.UnitsPerCell;
        var low = new WorldPosition(6 * size + 256, 5 * size + 256);
        var high = new WorldPosition(6 * size + 768, 5 * size + 768, size);
        Check(Pick(low) == low && Pick(high) == high, "Picking the two halves lost XY or selected the wrong floor.");
        var shore = new WorldPosition(size + 256, size + 768);
        var water = new WorldPosition(size + 768, size + 256);
        var infantry = catalog.Units.Single(type => type.Id == "ramp-rifleman");
        var resolver = new OpenRTS.Core.Navigation.DestinationResolver(grid, infantry.Locomotor);
        Check(grid.TerrainAt(low) == catalog.Locomotion!.TerrainIndex("grass") &&
            grid.TerrainAt(high) == catalog.Locomotion.TerrainIndex("mud") && game.UpperVisualAt(6, 5) == "mud" &&
            grid.SurfaceElevationAt(shore) == grid.SurfaceElevationAt(water) && resolver.CanStop(shore) &&
            !resolver.CanStop(water) && game.UpperVisualAt(1, 1) == "water",
            "The two terrain IDs and visual IDs did not survive the compiler or flat shore clearance.");
        var wall = new Vector3(6.5f, 0.5f, 5.5f);
        camera.Position = wall + new Vector3(-5, 4, -5);
        camera.LookAt(wall);
        Check(terrain.Pick(camera, camera.UnprojectPosition(wall)) is null, "The diagonal rock face accepted a ground order.");

        var players = new Dictionary<string, PlayerDefinition> { ["blue"] = new(1, "Blue") };
        using var world = game.Prepare(players).CreateWorld();
        using var replay = game.Prepare(players).CreateWorld(4);
        var poses = new EntityPose[2];
        world.CopyPoses(poses);
        Check(poses[0].Current.Elevation == 0 && poses[1].Current.Elevation == size, "The two scouts loaded at incorrect elevations.");
        var goal = new WorldPosition(13 * size + 512, poses[0].Current.Y);
        WorldCommand[] orders = [new(1, 0, 1, CommandKind.Move, poses[0].Id, goal),
            new(1, 1, 1, CommandKind.Move, poses[1].Id, high)];
        world.Step(orders);
        replay.Step(orders);
        bool detoured = false, grounded = true;
        for (int t = 0; t < 350 && world.MovingCount != 0; t++)
        {
            world.Step();
            replay.Step();
            world.CopyPoses(poses);
            grounded &= poses[0].Current.Elevation == 0 && poses[1].Current.Elevation == size;
            detoured |= Math.Abs(poses[0].Current.Y - goal.Y) >= 3 * size;
        }
        Check(grounded && detoured && poses[0].Current == goal && poses[1].Current == high &&
            world.Report() == replay.Report(), "Movement across the plateau outline, either half, or replay was incorrect.");
        CheckDiagonalCliffCombat(grid, infantry.Locomotor);

        WorldPosition? Pick(WorldPosition position) => terrain.Pick(camera,
            camera.UnprojectPosition(new Vector3(position.X, position.Elevation, position.Y) / size));
    }

    private void CheckDiagonalCliffCombat(NavigationGrid grid, OpenRTS.Core.Navigation.Locomotor locomotor)
    {
        var low = new WorldPosition(4608, 4608);
        var highNearEdge = new WorldPosition(6710, 5686);
        var highBehindRock = new WorldPosition(7680, 6656);
        var direct = new WeaponDefinition(6144, 6144, 25, 1000, 0, projectile: ProjectileKind.Cannon,
            projectileSpeed: 512, trajectory: ProjectileTrajectory.Direct);
        foreach (bool uphill in new[] { true, false })
        {
            var blocked = uphill ? Fire(direct, low, highBehindRock) : Fire(direct, highBehindRock, low);
            var clear = uphill ? Fire(direct, low, highNearEdge) : Fire(direct, highNearEdge, low);
            Check(!blocked.Fired && blocked.Health == 100 && clear.Fired && clear.Health == 75,
                $"Direct fire {(uphill ? "uphill" : "downhill")} failed to distinguish rock from a clear ray.");
        }
        var planar = new WeaponDefinition(6144, 6144, 25, 1000, 0, projectile: ProjectileKind.Cannon);
        Check(Fire(planar, low, highBehindRock).Health == 75, "Planar weapon behavior changed on the cliff map.");
        Check(Fire(direct, new(6400, 5376), new(5632, 5632)).Health == 75,
            "The upper half of a cut incorrectly obstructed a ray entirely on low ground.");
        var missile = new WeaponDefinition(6144, 6144, 25, 1000, 0, projectile: ProjectileKind.Missile,
            projectileSpeed: 96, trajectory: ProjectileTrajectory.Direct);
        var redirected = Fire(missile, low, highNearEdge, moveTarget: true);
        Check(redirected.Fired && redirected.Health == 100 && redirected.Impact is { } impact &&
            impact.TargetEntity == default && impact.Position.Elevation is > 0 and < 1024 &&
            Math.Abs(impact.Position.X + impact.Position.Y - 12 * 1024) <= 2,
            $"A missile steered into rock instead of striking the diagonal wall: {redirected}.");

        (bool Fired, int Health, WorldEvent? Impact) Fire(WeaponDefinition weapon, WorldPosition from, WorldPosition to,
            bool moveTarget = false)
        {
            var body = BodyShape.Circle(64);
            using var shotWorld = new SimulationWorld([
                new("test-gun", 100, ["idle", "fire"], body: body, weapon: weapon, movable: false, locomotor: locomotor),
                new("test-target", 100, body: body, locomotor: locomotor)
            ], grid.Width * grid.CellSize, grid.Height * grid.CellSize, navigationGrid: grid);
            shotWorld.AddInitialUnit(0, from, owner: 1);
            shotWorld.AddInitialUnit(1, to, owner: 2);
            shotWorld.Step([new(1, 0, 1, CommandKind.AttackTarget, new(1), TargetEntity: new(2))]);
            bool fired = false;
            WorldEvent? impact = null;
            for (int tick = 1; tick <= 96; tick++)
            {
                foreach (var e in shotWorld.Events)
                    if (e.Kind == WorldEventKind.WeaponFired) fired = true;
                    else if (e.Kind == WorldEventKind.ProjectileImpact) impact = e;
                if (impact.HasValue) break;
                if (tick == 1 && moveTarget)
                    shotWorld.Step([new(2, 0, 2, CommandKind.Move, new(2), new(8704, 7680, 1024))]);
                else shotWorld.Step();
            }
            var poses = new EntityPose[2];
            shotWorld.CopyPoses(poses);
            return (fired, poses[1].Health, impact);
        }
    }

    private void Run()
    {
        var catalog = GameManifest.Load(ProjectSettings.GlobalizePath("res://game.yaml")).LoadUnits();
        var root = GD.Load<PackedScene>(Sample).Instantiate<OpenRtsMap>();
        AddChild(root);
        var source = MapSceneExtractor.Extract(root);
        var compiled = MapCompiler.Compile(source, catalog);
        Check(source.Objects.Select(o => o.Type).SequenceEqual(new[] { "rifleman", "tank", "engineer" }), "Three catalog types lost or reordered.");
        Check(source.Objects[0].Id == "patrol/rifle" && source.Objects[1].Position == new WorldPosition(14336, 12288), "Subscene ID/transform was not extracted.");
        Check(source.Slots.Single(s => s.Id == "east").Reverse, "Inspector start properties were not loaded.");
        string baseline = compiled.Write();
        var group = root.GetNode<OpenRtsMapGroup>("Patrol");
        group.Name = "RenamedPrefab";
        root.MoveChild(group, 0);
        root.GetNode<Node3D>("Decoration").Scale = new(3, 4, 5);
        Check(MapCompiler.Compile(MapSceneExtractor.Extract(root), catalog).Write() == baseline, "Rename/tree order/decoration changed semantic data.");

        var packed = new PackedScene();
        Check(packed.Pack(root) == Error.Ok, "Pack failed.");
        var reloaded = packed.Instantiate<OpenRtsMap>(); AddChild(reloaded);
        Check(MapCompiler.Compile(MapSceneExtractor.Extract(reloaded), catalog).Write() == baseline, "Pack/reopen changed stable ID/order/positions.");
        RemoveChild(reloaded); reloaded.Free(); packed.Dispose();

        var other = GD.Load<PackedScene>("res://examples/maps/AuthoringSquad.tscn").Instantiate<OpenRtsMapGroup>();
        other.InstanceId = "second"; other.Order = 3; other.Position = new(12, 0, 16); root.AddChild(other);
        var instanced = MapSceneExtractor.Extract(root);
        Check(instanced.Objects.Count == 6 && instanced.Objects[0].Id == "second/rifle", "Instance context or explicit group order was lost.");
        other.InstanceId = group.InstanceId;
        Reject(() => MapSceneExtractor.Extract(root), "Duplicate prefab namespace accepted.");
        root.RemoveChild(other); other.Free();

        var unit = group.GetNode<OpenRtsMapObject>("Rifleman");
        var duplicate = (OpenRtsMapObject)unit.Duplicate(); group.AddChild(duplicate);
        Reject(() => MapSceneExtractor.Extract(root), "Native duplicate ID/order accepted.");
        MapAuthoringIdentity.Repair(root, (node, field, value) => node.Set(field, value));
        Check(duplicate.ObjectId != unit.ObjectId && duplicate.Order > 2 && MapSceneExtractor.Extract(root).Objects.Count == 4, "Explicit identity repair failed.");
        group.RemoveChild(duplicate); duplicate.Free();

        unit.Position = new(0.00048828125f, 0, 0.00048828125f);
        Check(MapSceneExtractor.Extract(root).Objects.Single(o => o.Id == "patrol/rifle").Position == new WorldPosition(12289, 12289), "Half-unit rounding rule changed.");
        unit.Position = Vector3.Zero;
        var parent = new Node3D { Name = "Organization" }; group.AddChild(parent);
        unit.Reparent(parent);
        Check(MapCompiler.Compile(MapSceneExtractor.Extract(root), catalog).Write() == baseline, "Reparent within context changed semantic data.");
        unit.Reparent(group); group.RemoveChild(parent); parent.Free();

        unit.Scale = new(2, 1, 1); Reject(() => MapSceneExtractor.Extract(root), "Scale accepted."); unit.Scale = Vector3.One;
        unit.Rotation = new(0, 0.1f, 0); Reject(() => MapSceneExtractor.Extract(root), "Rotation accepted."); unit.Rotation = Vector3.Zero;
        unit.Position = new(0, 0.1f, 0); Reject(() => MapSceneExtractor.Extract(root), "Height accepted."); unit.Position = Vector3.Zero;
        unit.TopLevel = true; Reject(() => MapSceneExtractor.Extract(root), "TopLevel accepted."); unit.TopLevel = false;
        unit.Position = new(float.NaN, 0, 0); Reject(() => MapSceneExtractor.Extract(root), "Nonfinite position accepted."); unit.Position = Vector3.Zero;
        unit.Order = 1; Reject(() => MapSceneExtractor.Extract(root), "Duplicate order accepted."); unit.Order = 0;
        unit.TypeId = "missing"; Reject(() => MapCompiler.Compile(MapSceneExtractor.Extract(root), catalog), "Missing type accepted."); unit.TypeId = "rifleman";
        root.IsSkirmish = false; Reject(() => MapSceneExtractor.Extract(root), "Start on fixture accepted."); root.IsSkirmish = true;
        var nested = new OpenRtsMap(); root.AddChild(nested);
        Reject(() => MapSceneExtractor.Extract(root), "Nested map root accepted."); root.RemoveChild(nested); nested.Free();
        root.Scale = new(2, 1, 1); Reject(() => MapSceneExtractor.Extract(root), "Root transform accepted."); root.Scale = Vector3.One;
        var tank = group.GetNode<OpenRtsMapObject>("Tank");
        var engineer = group.GetNode<OpenRtsMapObject>("Engineer");
        unit.Order = tank.Order = int.MaxValue - 1; engineer.Order = -1;
        MapAuthoringIdentity.Repair(root, (node, field, value) => node.Set(field, value));
        Check(new[] { unit.Order, tank.Order, engineer.Order }.All(o => o >= 0) &&
            new[] { unit.Order, tank.Order, engineer.Order }.Distinct().Count() == 3, "Order repair overflowed its Int32 key space.");
        unit.Order = 0; tank.Order = 1; engineer.Order = 2;

        var fixture = new OpenRtsMap { Name = "Fixture", IsSkirmish = false, Size = new(8, 8) }; AddChild(fixture);
        fixture.AddChild(new OpenRtsMapObject { Name = "Owned", ObjectId = "owned", TypeId = "rifleman", OwnerSlot = "blue", Position = new(2, 0, 2) });
        var owned = MapCompiler.Compile(MapSceneExtractor.Extract(fixture), catalog);
        using (var fixtureWorld = owned.Map.Prepare(new Dictionary<string, PlayerDefinition> { ["blue"] = new(1, "Blue"), ["red"] = new(2, "Red") }).CreateWorld())
            Check(owned.Source.Objects[0].Owner == "blue" && owned.Source.Slots.All(s => !s.Position.HasValue), "Fixture owner/slots were not extracted.");
        RemoveChild(fixture); fixture.Free();

        var players = new Dictionary<string, PlayerDefinition> { ["west"] = new(1, "Blue"), ["east"] = new(2, "Red") };
        var set = StartingSet.Load(new("infantry", File.ReadAllText(ProjectSettings.GlobalizePath("res://content/start-sets/infantry.yaml"))));
        var loaded = MapCompiler.Read(new("sample.ortmap.json", baseline), catalog);
        using var world = compiled.Map.Prepare(players, set).CreateWorld();
        using var roundtrip = loaded.Map.Prepare(players, set).CreateWorld(4);
        Check(world.Report() == roundtrip.Report() && compiled.Map.Prepare(players, set).UnitCount == 9, "Scene compiled world differs from independent reader.");
        string output = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--authoring-output=", StringComparison.Ordinal))?[19..]
            ?? ProjectSettings.GlobalizePath("res://artifacts/maps/authoring-check.ortmap.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!); MapCompiler.Save(compiled, output);
        GD.Print("OPENRTS_AUTHORING_WORLD:" + System.Text.Json.JsonSerializer.Serialize(world.Report()));
        RemoveChild(root); root.Free();
    }
}
