using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using OpenRTS.Core;
using OpenRTS.Core.Orders;

public partial class Sandbox
{
    private async void VerifyBuildings(FrameExchange.FrameLease frame)
    {
        try
        {
            _verificationDone = true;
            long tick = frame.Tick; int rate = frame.TickRate; var grid = frame.Grid!;
            var poses = frame.Poses.ToArray(); var weapons = frame.Weapons.ToArray(); var report = _session!.Completion.GetAwaiter().GetResult();
            Check(tick == BuildingScenario.VerificationTicks && poses.Length == 27 && _sprites!.WreckCount == 1, "Destroyed object must leave only an adapter wreck");
            Check(grid.TerrainAt(grid.CellAt(new(51 * 1024, 27 * 1024))) == _catalog!.Locomotion!.TerrainIndex("forest"), "Trees did not override road");
            Check(grid.TerrainAt(grid.CellAt(new(68 * 1024, 28 * 1024))) == _catalog.Locomotion.TerrainIndex("mountains"), "Cliff did not override terrain");
            Check(!grid.IsBlocked(grid.CellAt(new(51 * 1024, 42 * 1024))), "Wreck retained a blocking footprint");
            Check(_catalog.Units.Single(u => u.Id == "light-infantry").Body.HalfWidth == 160 && _catalog.Units.Single(u => u.Id == "large-tank").Body.HalfWidth == 640, "Mixed body sizes missing");
            Check(Pick(new(51 * 1024 + 100, 10 * 1024 + 100))?.Id.Value == 3, "Building picking still uses the center");
            _placeableId = "barracks"; var snapped = PlacementPoint(new(60 * 1024 + 137, 32 * 1024 + 50)); var fp = _catalog.Units[PlaceableType].Footprint!;
            Check((snapped.X - fp.HalfWidth) % 1024 == 0 && (snapped.Y - fp.HalfHeight) % 1024 == 0, "Placement anchor not aligned");
            HandleBuildingKey(Key.B); Check(_placing, "Placement mode missing"); HandleBuildingKey(Key.Escape); Check(!_placing, "Placement cancel missing");
            _sprites!.Apply(poses, 1, tick, rate, weapons);
            var overrides = _manifest.Settings.Presentation.ModelOverrides;
            if (overrides.TryGetValue("civilian-house", out var civilianModel) && civilianModel == "iso-civilian-house")
                CheckCivilianIsometric();
            else CheckCivilianTopDown();
            _sprites.Apply(poses, 1, tick, rate, weapons);
            bool directionalRadar = overrides.TryGetValue("headquarters", out var radarModel) && radarModel == "iso-radar";
            bool directionalPower = overrides.TryGetValue("power-plant", out var powerModel) && powerModel == "iso-power-plant";
            if (directionalRadar)
            {
                var station = poses.Single(p => p.Id.Value == 4);
                var first = SpriteFrame("radar-dish");
                _sprites.Apply([station], 1, tick + rate, rate);
                Check(SpriteFrame("radar-dish").Y != first.Y, "Radar did not advance through the directional atlas");
                _sprites.Apply(poses, 1, tick, rate, weapons);
            }
            else
            {
                var radar = Batch("radar").Multimesh.Buffer; float angle = (float)((tick + 1) / (double)rate * 65 * Math.PI / 180);
                Check(Math.Abs(radar[0] - MathF.Cos(angle) * 1.45f) < 0.001f && Math.Abs(radar[8] - MathF.Sin(angle) * 1.45f) < 0.001f, "Radar did not rotate in simulation time");
            }
            var power = (directionalPower ? IsometricBatch("power-plant") : Batch("power-plant")).Multimesh;
            var colors = power.Buffer;
            Check(power.VisibleInstanceCount == 5 && colors[2 * 20 + 12] < 0.6f && colors[1 * 20 + 12] > 0.99f && colors[3 * 20 + 19] > 0.005f && colors[4 * 20 + 19] > 0.005f, "Normal/damaged/working/combined states are not distinct");
            if (_manifest.Settings.Presentation.Camera == OpenRTS.Content.GameCamera.Isometric)
                CheckSelectionScale();
            bool directionalDoors = _manifest.Settings.Presentation.ModelOverrides.TryGetValue("barracks", out var barracksModel) && barracksModel == "iso-barracks";
            if (directionalDoors)
            {
                var barracks = poses.Single(p => p.Id.Value == 2);
                var factory = poses.Single(p => p.Id.Value == 3);
                Check(SpriteFrame("barracks").X > 0, "The scheduled barracks door did not advance");
                CheckDoor(barracks, "barracks", 0, 4);
                CheckDoor(factory, "factory", 0, 8);
                CheckDoor(factory, "factory", _catalog.Units[factory.UnitType].StateMask("heavy"), 8, 4);
                CheckFactoryExit(factory.UnitType);
            }
            else
            {
                bool openDoor = _sprites.Node.GetChildren().OfType<MultiMeshInstance3D>().Any(n => n.Multimesh.VisibleInstanceCount > 0 &&
                    n.Multimesh.Mesh.SurfaceGetMaterial(0) is ShaderMaterial m && m.GetShaderParameter("sprite_texture").AsGodotObject() is Texture2D t && t.ResourcePath.EndsWith("door-strip.png", StringComparison.Ordinal) && m.GetShaderParameter("uv_rect").AsVector4().X > 0.66f);
                Check(openDoor, "Door overlay did not advance to the open frame");
            }
            bool animatedCrane = overrides.TryGetValue("crane", out var craneModel) && craneModel == "iso-crane";
            if (animatedCrane) CheckCrane();
            var bunkerPreview = overrides.TryGetValue("bunker-upgradable", out var bunkerModel) && bunkerModel == "iso-bunker"
                ? CheckBunker() : [];
            var generatorPreview = overrides.TryGetValue("stealth-generator", out var stealthModel) &&
                stealthModel == "iso-stealth-generator" ? CheckGenerators() : [];
            var tankPreview = overrides.TryGetValue("tank", out var tankModel) && tankModel == "iso-tank"
                ? CheckTank() : [];
            var apcPreview = overrides.TryGetValue("apc-upgradable", out var apcModel) &&
                apcModel == "iso-apc-upgradable" ? CheckTrackedApc() : [];
            var truckPreview = overrides.TryGetValue("supply-truck", out var truckModel) &&
                truckModel == "iso-supply-truck" ? CheckMilitaryTruck() : [];
            var specialistPreview = overrides.TryGetValue("ballistic-carrier", out var ballisticModel) &&
                ballisticModel == "iso-ballistic-carrier" ? CheckSpecialistTrucks() : [];
            if (overrides.TryGetValue("howitzer-carrier", out var howitzerModel) && howitzerModel == "iso-wheeled-howitzer")
                CheckWheeledArtillery();
            if (overrides.TryGetValue("tank-mixed", out var mixedModel) && mixedModel == "iso-tank-mixed")
                CheckMixedTank();
            if (overrides.TryGetValue("radar-buggy", out var buggyModel) && buggyModel == "iso-radar-buggy")
                CheckRadarBuggy();
            if (overrides.TryGetValue("mine", out var mineModel) && mineModel == "iso-mine")
                CheckMine();
            if (overrides.TryGetValue("oil-derrick", out var oilModel) && oilModel == "iso-oil-derrick")
                CheckOilDerrick();
            var supplyOverlap = directionalPower && _manifest.Settings.Presentation.Camera == OpenRTS.Content.GameCamera.Isometric &&
                overrides.TryGetValue("supply-center", out var supplyModel) && supplyModel == "iso-supply-center" ? CheckSupplyOcclusion() : [];
            var layerA = poses.Single(p => p.Id.Value == 17); var layerB = poses.Single(p => p.Id.Value == 18) with { Previous = layerA.Current, Current = layerA.Current };
            _sprites.Apply([layerB, layerA], 1, tick, rate);
            var overlap = (directionalPower ? IsometricBatch("power-plant") : Batch("power-plant")).Multimesh.Buffer;
            Check(overlap[20 + 7] > overlap[7] + 0.0009f, "Packed/input order changed visual stacking priority");
            _sprites.Apply(poses, 1, tick, rate, weapons);
            int nodes = _sprites.Node.GetChildCount(); var stress = new EntityPose[10000];
            for (int i = 0; i < stress.Length; i++)
            { var position = new WorldPosition((i % 100 * 5 + 5) * 1024, (i / 100 * 5 + 5) * 1024); stress[i] = layerA with { Id = new((uint)i + 1), Previous = position, Current = position }; }
            _sprites.Apply(stress, 1, tick, rate); Check(_sprites.Node.GetChildCount() == nodes && _sprites.SpriteCount == 10000, "Footprint renderer created per-building nodes or lost instances");
            _sprites.Apply(poses, 1, tick, rate, weapons);
            int liveSprites = _sprites.SpriteCount;
            _sprites.Accept(new(tick + 10, WorldEventKind.Destroyed, layerA.Id, layerA.Current, layerA.UnitType, Owner: 1));
            _sprites.Apply(poses, 1, tick, rate, weapons);
            Check(_sprites.SpriteCount == liveSprites, "A future death event replaced or duplicated a live frame");
            _sprites.Apply(poses.Where(p => p.Id != layerA.Id).ToArray(), 1, tick, rate, weapons);
            Check(_sprites.SpriteCount < liveSprites, "A future wreck appeared before its simulation tick");
            _sprites.Accept(new(tick + 11, WorldEventKind.Removed, layerA.Id));
            _sprites.Apply(poses, 1, tick, rate, weapons);
            foreach (var path in Directory.EnumerateFiles(ProjectSettings.GlobalizePath("res://assets/buildings"), "*.png"))
            { using var image = Image.LoadFromFile(path); Check(image.DetectAlpha() != Image.AlphaMode.None && image.GetPixel(0, 0).A < 0.02f, "Missing building alpha: " + path); }
            if (_capturePath is not null && DisplayServer.GetName() != "headless")
            {
                _selection.Change([], OpenRTS.Godot.Controls.SelectionChange.Replace); UpdateSelection(1); _footprintView!.Apply(null, default);
                _label!.Text = "OpenRTS · N2\nЗдания · размеры · переопределения навигации\nВнизу: обычное / повреждённое / работает / повреждённое-работает / руины";
                await CaptureBuildings("buildings-overview.png");
                _camera!.Size = 28; _camera.Position = new(31.5f, 80, 42.5f); await CaptureBuildings("buildings-states.png");
                _camera.Size = 12; _camera.Position = new(53.5f, 80, 12);
                _selection.Change([new(3)], OpenRTS.Godot.Controls.SelectionChange.Replace); UpdateFootprintPreview();
                _label.Text = "Завод · проходимый ремонтный двор\nСиний — запрет · зелёный — остановка · оранжевый — транзит";
                await CaptureBuildings("buildings-factory.png");
                if (!directionalDoors) await CaptureDoorOcclusion(poses, tick, rate);
                if (animatedCrane && directionalRadar && directionalPower && overrides.ContainsKey("helipad"))
                {
                    _selection.Change([], OpenRTS.Godot.Controls.SelectionChange.Replace);
                    UpdateSelection(1);
                    _footprintView.Apply(null, default);
                    foreach (var layer in GetChildren().OfType<CanvasLayer>()) layer.Hide();
                    var infrastructure = new[] { "power-plant", "headquarters", "crane", "helipad" }.Select((id, i) =>
                    {
                        var type = _catalog.Units.ToList().FindIndex(unit => unit.Id == id);
                        var at = new WorldPosition((22 + 4 * i) * 1024, (40 - 4 * i) * 1024);
                        var facing = FacingDirection.North;
                        return new EntityPose(new((uint)(i + 1)), type, at, at,
                            new(1, facing, facing, facing, facing, new(_catalog.Units[type].IdleAnimation, 0, 0), AnimationPlayback.None, 0),
                            Health: _catalog.Units[type].MaxHealth);
                    }).ToArray();
                    _camera.Size = 15;
                    _camera.Position = new Vector3(28, 0, 34) + _camera.Basis.Z * 60 + _camera.Basis.Y * 0.5f;
                    _sprites.Apply(infrastructure, 1, 0, rate);
                    await CaptureBuildings("buildings-infrastructure.png");
                }
                if (bunkerPreview.Length > 0)
                {
                    _camera!.Size = 7.65f;
                    _camera.Position = new Vector3(31, 0, 37) + _camera.Basis.Z * 60 + _camera.Basis.Y * 0.45f;
                    _sprites.Apply(bunkerPreview, 1, 0, rate);
                    await CaptureBuildings("buildings-bunker.png");
                }
                if (generatorPreview.Length > 0)
                {
                    _camera!.Size = 9.5f;
                    _camera.Position = new Vector3(34, 0, 35) + _camera.Basis.Z * 60 + _camera.Basis.Y * 0.75f;
                    _sprites.Apply(generatorPreview, 1, rate, rate);
                    await CaptureBuildings("buildings-generators.png");
                }
                if (tankPreview.Length > 0)
                {
                    _camera!.Size = 7.3f;
                    _camera.Position = new Vector3(33.5f, 0, 35.5f) + _camera.Basis.Z * 60 + _camera.Basis.Y * 0.3f;
                    _sprites.Apply(tankPreview, 1, 0, rate);
                    await CaptureBuildings("buildings-medium-tank.png");
                }
                if (apcPreview.Length > 0)
                {
                    _camera!.Size = 7.3f;
                    _camera.Position = new Vector3(33.5f, 0, 35.5f) + _camera.Basis.Z * 60 + _camera.Basis.Y * 0.36f;
                    _sprites.Apply(apcPreview, 1, 0, rate);
                    await CaptureBuildings("buildings-tracked-apc.png");
                }
                if (truckPreview.Length > 0)
                {
                    _camera!.Size = 8.7f;
                    _camera.Position = new Vector3(34.2f, 0, 33.8f) + _camera.Basis.Z * 60 + _camera.Basis.Y * 0.5f;
                    var cruise = truckPreview[^1];
                    WeaponPose[] mounted = [new(cruise.Id, 0, cruise.Presentation.WeaponFacing,
                        cruise.Presentation.WeaponFacing, AnimationPlayback.None, 0, 0, default)];
                    truckPreview[^1] = cruise with { WeaponCount = 1 };
                    _sprites.Apply(truckPreview, 1, 0, rate, mounted);
                    _combatEffects!.Apply([], 1, 0, actors: truckPreview, weapons: mounted, cameraBack: _camera.Basis.Z);
                    await CaptureBuildings("buildings-military-truck.png");
                    _combatEffects.Apply([], 1, 0);
                }
                if (specialistPreview.Length > 0)
                {
                    _camera!.Size = 7.5f;
                    _camera.Position = new Vector3(34, 0, 34) + _camera.Basis.Z * 60 + _camera.Basis.Y * 0.45f;
                    var launchers = specialistPreview.Skip(2).Select(p => new WeaponPose(p.Id, 0,
                        p.Presentation.WeaponFacing, p.Presentation.WeaponFacing, AnimationPlayback.None, 0, 0, default)).ToArray();
                    _sprites.Apply(specialistPreview, 1, 0, rate, launchers);
                    _combatEffects!.Apply([], 1, 0, actors: specialistPreview, weapons: launchers, cameraBack: _camera.Basis.Z);
                    await CaptureBuildings("buildings-specialist-trucks.png");
                    _combatEffects.Apply([], 1, 0);
                }
                if (supplyOverlap.Length > 0)
                {
                    _camera!.Size = 8;
                    _camera.Position = new Vector3(30, 0, 33) + _camera.Basis.Z * 60 + _camera.Basis.Y;
                    _sprites.Apply(supplyOverlap, 1, 0, rate);
                    await CaptureBuildings("buildings-supply-occlusion.png");
                }
            }
            GD.Print($"OPENRTS_BUILDINGS:PASS footprint terrain, wrecks, sizes, states, radar, doors, stacking and 10000-building batches; nodes={nodes}");
            GD.Print("OPENRTS_SIMULATION:" + JsonSerializer.Serialize(report)); GetTree().Quit();

            MultiMeshInstance3D IsometricBatch(string file, string category = "buildings") =>
                AssetBatch(category + "/isometric/" + file);

            MultiMeshInstance3D BuildingBatch(string path) => AssetBatch("buildings/" + path);

            MultiMeshInstance3D AssetBatch(string path) => _sprites.Node.GetChildren().OfType<MultiMeshInstance3D>()
                .Single(node => node.Multimesh.VisibleInstanceCount > 0 &&
                    node.Multimesh.Mesh.SurfaceGetMaterial(0) is ShaderMaterial material &&
                    material.GetShaderParameter("sprite_texture").AsGodotObject() is Texture2D texture &&
                    texture.ResourcePath == "res://assets/" + path + ".png");

            EntityPose[] CheckSupplyOcclusion()
            {
                var center = new WorldPosition(31 * 1024 + 512, 34 * 1024);
                var supply = poses.Single(p => p.Id.Value == 5) with { Previous = center, Current = center };
                supply = supply with { Presentation = supply.Presentation with
                    { Body = new(_catalog.Units[supply.UnitType].IdleAnimation, 0, 0), States = 0 } };
                var plant = poses.Single(p => p.Id.Value == 17);
                plant = plant with { Presentation = plant.Presentation with { Owner = 2 } };
                var behind = new WorldPosition(center.X - 2560, center.Y - 2048);
                var rearPlant = plant with { Previous = behind, Current = behind };
                _sprites.Apply([rearPlant, supply], 1, 0, rate);
                Check(BuildingBatch("supply-center").Multimesh.Buffer[7] > IsometricBatch("power-plant").Multimesh.Buffer[7],
                    "The supply center must cover a power plant behind it.");
                var inFront = center with { X = center.X + 3584 };
                _sprites.Apply([supply, plant with { Previous = inFront, Current = inFront }], 1, 0, rate);
                Check(IsometricBatch("power-plant").Multimesh.Buffer[7] > BuildingBatch("supply-center").Multimesh.Buffer[7],
                    "Moving the power plant along world X must be able to put it in front of the supply center.");
                return [rearPlant, supply];
            }

            Vector4 SpriteFrame(string file, string category = "buildings")
            {
                var material = (ShaderMaterial)IsometricBatch(file, category).Multimesh.Mesh.SurfaceGetMaterial(0);
                return material.GetShaderParameter("uv_rect").AsVector4();
            }

            void CheckCrane()
            {
                int craneType = _catalog.Units.ToList().FindIndex(unit => unit.Id == "crane");
                var powerPlant = poses.Single(p => p.Id.Value == 1);
                var crane = powerPlant with { UnitType = craneType, Order = default,
                    Presentation = powerPlant.Presentation with { States = 0 } };
                _sprites.Apply([crane], 0, 0, rate);
                Check(SpriteFrame("crane-arm").Y == 0.5f, "Idle crane did not face its work bay");
                EntityId site = new(10001);
                var engaged = crane with { Order = new(UnitActionKind.Build, OrderPhase.Engaging, default, site, 0, 0) };
                var target = crane.Current with { X = crane.Current.X + 5 * 1024 };
                _sprites.Apply([engaged], 0, rate / 4, rate, projects: [new(site, 1, powerPlant.UnitType, target, false)]);
                var work = SpriteFrame("crane-arm");
                Check(work.Y == 0.25f && work.X > 0, "Crane did not face the site and lower its hook");
                _sprites.Apply([crane with { WeaponCount = 1 }], 0, tick + 6, rate,
                    [new(crane.Id, 0, FacingDirection.North, FacingDirection.North, new(_catalog.Units[craneType].AnimationIndex("produce"), tick, 15), 0, 0, site)]);
                var repair = SpriteFrame("crane-arm");
                Check(repair.Y == 0 && repair.X > 0, "Crane did not use the repair mount's facing and work animation");
            }

            EntityPose[] CheckBunker()
            {
                var units = _catalog!.Units.ToList();
                int bunkerType = units.FindIndex(u => u.Id == "bunker");
                int builderType = units.FindIndex(u => u.Id == "construction-engineer");
                var preview = new EntityPose[3];
                foreach (var (upgrade, index) in new[] { ("cannon", 1), ("mixed", 2) })
                {
                    using var world = new SimulationWorld(units, 64 * 1024, 64 * 1024, players: [new(1, "Blue", 10000)]);
                    var at = new WorldPosition((28 + index * 3) * 1024, (40 - index * 3) * 1024);
                    var bunker = world.AddInitialUnit(bunkerType, at, 1);
                    var builder = world.AddInitialUnit(builderType, at with { Y = at.Y + 2048 }, 1);
                    var actors = new EntityPose[world.Count];
                    world.CopyPoses(actors);
                    _sprites.Apply([actors[0]], 1, 0, rate);
                    Check(_sprites.SpriteCount == 1, "An unarmed bunker should show only the hexagonal hull.");
                    if (index == 1)
                    {
                        var start = new WorldPosition(28 * 1024, 40 * 1024);
                        preview[0] = actors[0] with { Id = new(100), Current = start, Previous = start };
                    }
                    world.Step([new(1, 0, 1, CommandKind.StartUpgrade, builder, TargetEntity: bunker, Action: upgrade)]);
                    for (int i = 0; i < 150; i++) world.Step();
                    world.CopyPoses(actors);
                    var upgraded = actors[0];
                    Check(upgraded.Upgrade.Completed == upgrade, $"The bunker {upgrade} upgrade failed.");
                    _sprites.Apply([upgraded], 1, 0, rate);
                    string module = $"bunker-{upgrade}-turret";
                    var first = SpriteFrame(module);
                    Check(_sprites.SpriteCount == 2, $"The {upgrade} upgrade must occupy one turret mount.");
                    var channels = new WeaponPose[world.WeaponCount];
                    world.CopyWeaponPoses(channels);
                    var east = FacingDirection.FromQuarterTurns(1);
                    for (int i = 0; i < channels.Length; i++)
                        channels[i] = channels[i] with { PreviousFacing = east, Facing = east };
                    _sprites.Apply([upgraded], 1, 0, rate, channels);
                    Check(Math.Abs(SpriteFrame(module).Y - 0.25f) < 0.001f && SpriteFrame(module).Y != first.Y,
                        $"The {upgrade} turret did not turn independently of the hull.");
                    var demo = index == 1 ? FacingDirection.FromQuarterTurns(2) : east;
                    preview[index] = upgraded with { Id = new((uint)(100 + index)), WeaponCount = 0,
                        Presentation = upgraded.Presentation with { PreviousWeaponFacing = demo, WeaponFacing = demo } };
                }
                _sprites.Apply(poses, 1, tick, rate, weapons);
                return preview;
            }

            EntityPose[] CheckGenerators()
            {
                var units = _catalog!.Units.ToList();
                int Type(string id) => units.FindIndex(unit => unit.Id == id);
                int shieldType = Type("shield-generator");
                var shield = units[shieldType];
                Check(shield.Footprint is { Width: 4, Height: 4 } && shield.VisionRadius == 0 &&
                    shield.Economy?.Energy == 0 && shield.Products.Count == 0 && shield.Weapons.Count == 0 &&
                    shield.Abilities.Count == 0 && shield.Sensors is { StealthRange: 0, JamRange: 0 },
                    "The shield generator must be a passive 4x4 structure.");
                using var world = new SimulationWorld(units, 40 * 1024, 40 * 1024,
                    navigationGrid: new(40, 40), players: [new(1, "Blue", 10000)]);
                var center = new WorldPosition(20 * 1024, 20 * 1024);
                var engineer = world.AddInitialUnit(Type("construction-engineer"), center with { X = 16 * 1024 }, 1);
                world.Step([new(1, 0, 1, CommandKind.PlanConstruction, engineer, center, Action: "site-shield")]);
                for (int i = 0; i < 360; i++) world.Step();
                var placed = new EntityPose[world.Count];
                world.CopyPoses(placed);
                var complete = placed.Single(p => p.UnitType == shieldType);
                Check(!complete.Construction.Active && complete.Current == center,
                    "The construction engineer did not complete the shield generator.");

                var at = new WorldPosition(31 * 1024, 38 * 1024);
                var stealth = new EntityPose(new(100), Type("stealth-generator"), at, at,
                    complete.Presentation with { States = 0 })
                    { Emitter = new(12288, 8192, 0, 50, false, true) };
                _sprites.Apply([stealth], 1, 0, rate);
                var initialFrame = SpriteFrame("stealth-emitter");
                var initialPivot = SpriteScreenPivot("stealth-emitter", stealth, "rotor");
                _sprites.Apply([stealth], 1, 50 + rate / 2, rate);
                Check(SpriteFrame("stealth-emitter").Y != initialFrame.Y, "Stealth rotor did not advance through the directional atlas.");
                var deployedPivot = SpriteScreenPivot("stealth-emitter", stealth, "rotor");
                Check(deployedPivot.IsEqualApprox(initialPivot), $"Stealth rotor mount moved from {initialPivot} to {deployedPivot}.");
                _sprites.Apply(poses, 1, tick, rate, weapons);
                var previewCenter = new WorldPosition(37 * 1024, 32 * 1024);
                return [stealth, complete with { Current = previewCenter, Previous = previewCenter }];
            }

            EntityPose[] CheckTank()
            {
                const string hull = "medium-tank-hull", turret = "medium-tank-turret";
                int typeIndex = _catalog!.Units.ToList().FindIndex(unit => unit.Id == "tank");
                var type = _catalog.Units[typeIndex];
                var north = FacingDirection.North;
                var east = FacingDirection.FromQuarterTurns(1);
                var south = FacingDirection.FromQuarterTurns(2);
                var west = FacingDirection.FromQuarterTurns(3);
                EntityPose Pose(uint id, uint owner, int x, int y, FacingDirection body, FacingDirection gun)
                {
                    var point = new WorldPosition(x * 512, y * 512);
                    return new(new(id), typeIndex, point, point,
                        new(owner, body, body, gun, gun, new(type.IdleAnimation, 0, 0), AnimationPlayback.None, 0));
                }

                var pose = Pose(101, 1, 62, 76, north, east);
                _sprites.Apply([pose], 1, 0, rate);
                var standingHull = SpriteFrame(hull, "units");
                var aimingTurret = SpriteFrame(turret, "units");
                Check(standingHull.Y == 0 && Mathf.IsEqualApprox(aimingTurret.Y, 0.5f) &&
                    Mathf.IsEqualApprox(standingHull.W, 1f / 16) && Mathf.IsEqualApprox(aimingTurret.W, 1f / 16),
                    "Medium tank did not select independent directional hull and turret frames.");
                CheckAnchor(hull, pose, "body", "units");
                CheckAnchor(turret, pose, "turret", "units");
                var buffer = IsometricBatch(hull, "units").Multimesh.Buffer;
                Check(Mathf.IsEqualApprox(buffer[0], 3.43796f * 0.75f), "Isometric tank mesh did not use the reduced width.");

                var nextFacing = new FacingDirection(199, -1000);
                var turning = pose with { Presentation = pose.Presentation with
                    { BodyFacing = nextFacing, PreviousBodyFacing = nextFacing, Body = new(type.MoveAnimation, 0, 0) } };
                _sprites.Apply([turning], 0, (int)Math.Ceiling(rate / 12d), rate);
                var movingHull = SpriteFrame(hull, "units");
                Check(Mathf.IsEqualApprox(movingHull.Y, standingHull.W) &&
                    Mathf.IsEqualApprox(movingHull.X, standingHull.Z) && SpriteFrame(turret, "units") == aimingTurret,
                    "Medium tank lost its adjacent heading, tread animation, or independent aim.");

                var style = OpenRTS.Content.EffectCatalog.Load(_manifest.Effects).Weapons["cannon"];
                var muzzle = _combatEffects!.LaunchVisual(typeIndex, 0, new(1, pose.Id, pose.Current, north, east), style, Vector3.Right);
                var expected = new Vector3(pose.Current.X / 1024f, 0, pose.Current.Y / 1024f) + new Vector3(1.474f, 0.917f, 0) * 0.75f;
                Check(muzzle is { } launch && _camera!.UnprojectPosition(launch.Center).DistanceTo(_camera.UnprojectPosition(expected)) < 0.05f,
                    "The cannon effect missed the visible muzzle of the east-facing turret.");
                _sprites.Apply(poses, 1, tick, rate, weapons);
                return [pose, Pose(102, 2, 67, 71, south, west), Pose(103, 1, 72, 66, west, new(707, 707))];
            }

            EntityPose[] CheckTrackedApc()
            {
                const string bodyAtlas = "tracked-apc-hull";
                var units = _catalog!.Units.ToList();
                int apc = units.FindIndex(unit => unit.Id == "apc-mg");
                int missileApc = units.FindIndex(unit => unit.Id == "apc-rocket");
                var north = FacingDirection.North;
                var east = FacingDirection.FromQuarterTurns(1);
                var south = FacingDirection.FromQuarterTurns(2);
                var west = FacingDirection.FromQuarterTurns(3);
                EntityPose Pose(int index, int type, FacingDirection body, FacingDirection gun, string? upgrade = null)
                {
                    var point = new WorldPosition((62 + 5 * index) * 512, (76 - 5 * index) * 512);
                    uint state = upgrade is null ? 0 : units[type].StateMask(upgrade);
                    return new(new((uint)(111 + index)), type, point, point,
                        new(index == 1 ? 2u : 1u, body, body, gun, gun,
                            new(units[type].IdleAnimation, 0, 0), AnimationPlayback.None, state));
                }

                var gunApc = Pose(0, apc, east, north);
                _sprites.Apply([gunApc], 1, 0, rate);
                var idleHull = SpriteFrame(bodyAtlas, "units");
                var gunFrame = SpriteFrame("tracked-apc-turret", "units");
                Check(Mathf.IsEqualApprox(idleHull.Y, 0.5f) && gunFrame.Y == 0 &&
                    Mathf.IsEqualApprox(gunFrame.W, 1f / 16) && _sprites.SpriteCount == 2,
                    "The APC's hull and turret did not select independent directions.");
                CheckAnchor(bodyAtlas, gunApc, "body", "units");
                var center = new Vector3(gunApc.Current.X / 1024f, 0, gunApc.Current.Y / 1024f);
                var pivot = SpriteScreenPivot("tracked-apc-turret", gunApc, "turret", "units");
                Check(pivot.DistanceTo(_camera!.UnprojectPosition(center + Vector3.Right * (0.246f * 0.75f))) < 0.05f,
                    "The APC turret socket did not rotate with the hull.");
                var driving = gunApc with { Presentation = gunApc.Presentation with { Body = new(units[apc].MoveAnimation, 0, 0) } };
                _sprites.Apply([driving], 0, (int)Math.Ceiling(rate / 12d), rate);
                Check(Mathf.IsEqualApprox(SpriteFrame(bodyAtlas, "units").X, idleHull.Z),
                    "The APC lost its second tread frame.");
                var effect = OpenRTS.Content.EffectCatalog.Load(_manifest.Effects).Weapons["rifle"];
                var muzzle = _combatEffects!.LaunchVisual(apc, 0, new(1, gunApc.Id, gunApc.Current, east, north), effect, Vector3.Forward);
                Check(muzzle is { } launch && _camera.UnprojectPosition(launch.Center).DistanceTo(
                    _camera.UnprojectPosition(center + new Vector3(.288f, 1.098f, -.563f) * 0.75f)) < 0.05f,
                    "The APC shot effect did not follow the off-center turret.");

                var cannon = Pose(1, apc, south, east, "cannon");
                var rockets = Pose(2, apc, west, south, "rockets");
                foreach (var (pose, atlas) in new[] { (cannon, "tracked-apc-cannon"), (rockets, "tracked-apc-missiles"),
                             (Pose(2, missileApc, west, south), "tracked-apc-missiles") })
                {
                    _sprites.Apply([pose], 1, 0, rate);
                    Check(_sprites.SpriteCount == 2 && Mathf.IsEqualApprox(SpriteFrame(atlas, "units").W, 1f / 16),
                        "An APC variant selected the wrong turret or drew two towers.");
                }
                _sprites.Apply(poses, 1, tick, rate, weapons);
                return [gunApc, cannon, rockets];
            }

            EntityPose[] CheckMilitaryTruck()
            {
                const string hull = "military-truck-hull", cab = "military-truck-cab", crane = "military-truck-crane";
                const string empty = "military-truck-cargo-empty", loaded = "military-truck-cargo-loaded";
                const string salvo = "military-truck-mlrs", rail = "military-truck-tomahawk";
                var units = _catalog!.Units.ToList();
                var north = FacingDirection.North;
                var south = FacingDirection.FromQuarterTurns(2);
                var east = FacingDirection.FromQuarterTurns(1);
                EntityPose Truck(uint id, string name, int x, int y, FacingDirection body, FacingDirection aim, bool carrying = false)
                {
                    int type = units.FindIndex(unit => unit.Id == name);
                    var point = new WorldPosition(x * 512, y * 512);
                    uint state = carrying ? units[type].StateMask("loaded") : 0;
                    return new(new(id), type, point, point,
                        new(1, body, body, aim, aim, new(units[type].IdleAnimation, 0, 0), AnimationPlayback.None, state));
                }

                var builder = Truck(201, "construction-engineer", 63, 70, south, east);
                _sprites.Apply([builder], 1, 0, rate);
                Check(SpriteFrame(crane, "units").Y == 0, "The idle truck crane did not point aft.");
                var site = new EntityId(209);
                var working = builder with { Order = new(UnitActionKind.Build, OrderPhase.Engaging, default, site, 0, 0) };
                var target = builder.Current with { X = builder.Current.X + 2048 };
                _sprites.Apply([working], 1, 0, rate, projects: [new(site, 1, units.FindIndex(unit => unit.Id == "power-plant"), target, false)]);
                Check(Mathf.IsEqualApprox(SpriteFrame(crane, "units").Y, 0.5f), "The mobile crane did not turn toward the site.");

                var emptyTruck = Truck(202, "supply-truck", 69, 64, south, south);
                var loadedTruck = Truck(203, "supply-truck", 74, 59, north, north, carrying: true);
                var emptyVariant = emptyTruck with { Presentation = emptyTruck.Presentation with
                    { Body = new(units[emptyTruck.UnitType].MoveAnimation, 0, 0) } };
                _sprites.Apply([emptyVariant], 0, (int)Math.Ceiling(rate / 12d), rate);
                var wheel = SpriteFrame(hull, "units");
                Check(Mathf.IsEqualApprox(wheel.X, 3 * wheel.Z), "The supply truck did not reach the second wheel frame.");
                Check(!_sprites.Node.GetChildren().OfType<MultiMeshInstance3D>().Any(n =>
                    n.Multimesh.VisibleInstanceCount > 0 && n.Multimesh.Mesh.SurfaceGetMaterial(0) is ShaderMaterial material &&
                    material.GetShaderParameter("sprite_texture").AsGodotObject() is Texture2D texture &&
                    texture.ResourcePath.EndsWith("/" + loaded + ".png", StringComparison.Ordinal)),
                    "An empty supply truck still drew its loaded bed.");
                float emptyHeight = IsometricBatch(empty, "units").Multimesh.Buffer[7];
                Check(emptyHeight < IsometricBatch(cab, "units").Multimesh.Buffer[7] &&
                    emptyHeight > IsometricBatch(hull, "units").Multimesh.Buffer[7],
                    "The empty cargo bed must sit between the deck and the cab from the front.");
                _sprites.Apply([loadedTruck], 1, 0, rate);
                Check(_sprites.SpriteCount == 3 &&
                    IsometricBatch(loaded, "units").Multimesh.Buffer[7] > IsometricBatch(cab, "units").Multimesh.Buffer[7],
                    "The loaded cargo bed must replace the empty bed and cover the deck from the rear.");
                CheckAnchor(hull, loadedTruck, "body", "units");
                CheckAnchor(cab, loadedTruck, "cab", "units");

                var salvoTruck = Truck(204, "mlrs", 64, 77, north, north);
                var cruiseTruck = Truck(205, "tomahawk", 70, 71, south, south);
                var effects = OpenRTS.Content.EffectCatalog.Load(_manifest.Effects);
                foreach (var (truck, file, effectId) in new[] { (salvoTruck, salvo, "salvo-rocket"), (cruiseTruck, rail, "cruise-missile") })
                {
                    var frames = new System.Collections.Generic.HashSet<Vector4>();
                    for (int direction = 0; direction < 32; direction++)
                    {
                        float yaw = direction * MathF.Tau / 32;
                        var aim = new FacingDirection((int)(MathF.Sin(yaw) * 1024), (int)(-MathF.Cos(yaw) * 1024));
                        _sprites.Apply([truck with { Presentation = truck.Presentation with
                            { WeaponFacing = aim, PreviousWeaponFacing = aim } }], 1, 0, rate);
                        frames.Add(SpriteFrame(file, "units"));
                    }
                    Check(frames.Count == 32, file + " did not select 32 independent mount facings.");
                    var style = effects.Weapons[effectId];
                    var launch = _combatEffects!.LaunchVisual(truck.UnitType, 0,
                        new(1, truck.Id, truck.Current, north, east), style, Vector3.Right);
                    var offset = style.MountedRocket is null ? new Vector3(0.58f, 1.019f, 0.56f) : new Vector3(-0.94f, 0.994f, 0.56f);
                    var expected = new Vector3(truck.Current.X / 1024f, 0, truck.Current.Y / 1024f) + offset * 0.75f;
                    Check(launch.HasValue, file + " did not publish a visual launch point.");
                    var (flightDirection, center) = launch!.Value;
                    var anchor = style.MountedRocket is null ? center : center - flightDirection * (float)style.Size;
                    Check(_camera!.UnprojectPosition(_combatEffects.Node.ToGlobal(anchor)).DistanceTo(_camera.UnprojectPosition(expected)) < 0.05f,
                        file + " did not align its projectile with the visible mount.");
                }
                _sprites.Apply(poses, 1, tick, rate, weapons);
                return [builder, emptyTruck, loadedTruck, salvoTruck, cruiseTruck];
            }

            EntityPose[] CheckSpecialistTrucks()
            {
                var units = _catalog!.Units.ToList();
                var north = FacingDirection.North;
                var east = FacingDirection.FromQuarterTurns(1);
                var south = FacingDirection.FromQuarterTurns(2);
                EntityPose DisplayTruck(int index, string id, int x, int y, FacingDirection body, FacingDirection aim) =>
                    new(new((uint)(220 + index)), units.FindIndex(type => type.Id == id), new(x * 512, y * 512), new(x * 512, y * 512),
                        new(1, body, body, aim, aim, new(0, 0, 0), AnimationPlayback.None, 0));

                var stealthTruck = DisplayTruck(0, "stealth-truck", 62, 71, south, south) with
                    { Emitter = new(8192, 6144, 0, 0, false, true) };
                _sprites.Apply([stealthTruck], 1, 0, rate);
                var emitterAtRest = SpriteFrame("military-truck-stealth-rotor", "units");
                _sprites.Apply([stealthTruck], 1, rate / 2, rate);
                Check(SpriteFrame("military-truck-stealth-rotor", "units") != emitterAtRest,
                    "Deployed mobile stealth rotor did not spin.");
                _sprites.Apply([stealthTruck with { Emitter = stealthTruck.Emitter with { Moving = true } }], 1, rate / 2, rate);
                Check(SpriteFrame("military-truck-stealth-rotor", "units") == emitterAtRest,
                    "The mobile stealth rotor must stop while driving.");

                var shieldTruck = DisplayTruck(1, "shield-truck", 68, 65, north, north);
                _sprites.Apply([shieldTruck], 1, 0, rate);
                Check(_sprites.SpriteCount == 3 && IsometricBatch("military-truck-shield", "units").Multimesh.VisibleInstanceCount == 1,
                    "The mobile shield generator did not draw its shared chassis and projector.");

                var scud = DisplayTruck(2, "scud", 66, 77, south, east);
                var cruise = DisplayTruck(3, "tomahawk", 72, 71, north, north);
                _sprites.Apply([scud], 1, 0, rate);
                var frame = SpriteFrame("military-truck-scud", "units");
                _sprites.Apply([scud with { Presentation = scud.Presentation with { WeaponFacing = north, PreviousWeaponFacing = north } }], 1, 0, rate);
                Check(SpriteFrame("military-truck-scud", "units") == frame, "SCUD deck yaw must ignore weapon aim.");
                _sprites.Apply([scud with { Presentation = scud.Presentation with { BodyFacing = east, PreviousBodyFacing = east } }], 1, 0, rate);
                Check(SpriteFrame("military-truck-scud", "units") != frame, "SCUD deck must turn with the chassis.");
                _sprites.Apply(poses, 1, tick, rate, weapons);
                return [stealthTruck, shieldTruck, scud with { WeaponCount = 1 }, cruise with { WeaponOffset = 1, WeaponCount = 1 }];
            }

            void CheckWheeledArtillery()
            {
                const string hull = "wheeled-artillery-hull", howitzer = "wheeled-artillery-turret";
                const string launcher = "wheeled-artillery-air-defense", radar = "wheeled-artillery-radar";
                var units = _catalog!.Units.ToList();
                int artilleryType = units.FindIndex(u => u.Id == "howitzer");
                int antiAirType = units.FindIndex(u => u.Id == "mobile-aa");
                var weapon = units[antiAirType].Weapons.Single().Weapon;
                Check(weapon.CanTarget(new TargetCategories(["air"])) && !weapon.CanTarget(TargetCategories.Ground),
                    "The mobile air-defense weapon must target aircraft only.");
                var north = FacingDirection.North;
                var east = FacingDirection.FromQuarterTurns(1);
                var point = new WorldPosition(32 * 1024, 36 * 1024);
                EntityPose Vehicle(int type) => new(new(242), type, point, point,
                    new(1, north, north, east, east, new(units[type].IdleAnimation, 0, 0), AnimationPlayback.None, 0));

                var artillery = Vehicle(artilleryType);
                _sprites.Apply([artillery], 1, 0, rate);
                var chassisFrame = SpriteFrame(hull, "units");
                Check(SpriteFrame(howitzer, "units").Y == 0.5f && _sprites.SpriteCount == 3,
                    "Wheeled artillery did not show an independent turret on the shared chassis.");
                CheckAnchor(hull, artillery, "body", "units");
                var aa = Vehicle(antiAirType);
                _sprites.Apply([aa], 1, 0, rate);
                var missileFrame = SpriteFrame(launcher, "units");
                var radarFrame = SpriteFrame(radar, "units");
                var antenna = SpriteScreenPivot(radar, aa, "radar", "units");
                var expected = _camera!.UnprojectPosition(new Vector3(32 - 0.304f * 0.75f, 0, 36 + 0.52f * 0.75f));
                Check(antenna.DistanceTo(expected) < 0.05f && SpriteFrame(hull, "units") == chassisFrame,
                    "The radar socket must rotate with the launcher while both variants share one hull.");
                _sprites.Apply([aa], 1, rate / 2, rate);
                Check(SpriteFrame(radar, "units") != radarFrame && SpriteFrame(launcher, "units") == missileFrame &&
                    antenna.DistanceTo(SpriteScreenPivot(radar, aa, "radar", "units")) < 0.05f,
                    "The radar must spin without changing the missile turret facing or moving its socket.");
                _sprites.Apply(poses, 1, tick, rate, weapons);
            }

            void CheckMixedTank()
            {
                var units = _catalog!.Units.ToList();
                int type = units.FindIndex(u => u.Id == "tank-mixed");
                var definition = units[type];
                var north = FacingDirection.North;
                var east = FacingDirection.FromQuarterTurns(1);
                var point = new WorldPosition(32 * 1024, 36 * 1024);
                var tank = new EntityPose(new(243), type, point, point,
                    new(1, north, north, north, north, new(definition.IdleAnimation, 0, 0), AnimationPlayback.None, 0),
                    WeaponCount: definition.Weapons.Count);
                WeaponPose[] mounts = definition.Weapons.Select((mount, i) =>
                {
                    var angle = mount.Id == "main" ? east : north;
                    return new WeaponPose(tank.Id, i, angle, angle, AnimationPlayback.None, 0, 0, default);
                }).ToArray();
                const string turret = "mixed-tank-turret", rack = "mixed-tank-missiles", radar = "mixed-tank-radar";
                _sprites.Apply([tank], 1, 0, rate, mounts);
                Check(_sprites.SpriteCount == 4 && SpriteFrame(turret, "units").Y == 0.5f &&
                    SpriteFrame(rack, "units").Y == 0 && SpriteFrame("medium-tank-hull", "units").Y == 0,
                    "The mixed tank must use the standard hull and aim guns and missiles independently.");
                var radarFrame = SpriteFrame(radar, "units");
                var radarPoint = _camera!.UnprojectPosition(new Vector3(32 - .293f * 0.75f, 0, 36));
                _sprites.Apply([tank], 1, rate / 2, rate, mounts);
                Check(SpriteFrame(radar, "units") != radarFrame &&
                    radarPoint.DistanceTo(SpriteScreenPivot(radar, tank, "radar", "units")) < 0.05f,
                    "The mixed tank radar must spin around a socket attached to the gun turret.");

                var styles = OpenRTS.Content.EffectCatalog.Load(_manifest.Effects).Weapons;
                foreach (var mount in mounts)
                {
                    bool cannon = definition.Weapons[mount.Mount].Id == "main";
                    var offset = cannon ? new Vector3(1.039f, 1.036f, 0) : new Vector3(.53f, 1.385f, -.433f);
                    var expected = new Vector3(32, 0, 36) + offset * 0.75f;
                    var launch = _combatEffects!.LaunchVisual(type, mount.Mount,
                        new(1, tank.Id, tank.Current, north, mount.Facing), styles[definition.Weapons[mount.Mount].Weapon.Id], Vector3.Forward);
                    Check(launch is { } firing &&
                        _camera.UnprojectPosition(_combatEffects.Node.ToGlobal(firing.Center)).DistanceTo(_camera.UnprojectPosition(expected)) < 0.05f,
                        "A mixed-tank shot did not start at the visual anchor of its own weapon mount.");
                }
                _sprites.Apply(poses, 1, tick, rate, weapons);
            }

            void CheckRadarBuggy()
            {
                int type = _catalog!.Units.ToList().FindIndex(u => u.Id == "radar-buggy");
                var unit = _catalog.Units[type];
                var north = FacingDirection.North;
                var east = FacingDirection.FromQuarterTurns(1);
                var point = new WorldPosition(32 * 1024, 36 * 1024);
                var buggy = new EntityPose(new(248), type, point, point,
                    new(1, north, north, north, north, new(unit.IdleAnimation, 0, 0), AnimationPlayback.None, 0), WeaponCount: 1);
                const string hull = "radar-buggy-hull", turret = "radar-buggy-turret";
                var aim = new WeaponPose(buggy.Id, 0, east, east, AnimationPlayback.None, 0, 0, default);
                _sprites.Apply([buggy], 1, 0, rate, [aim]);
                var idleFrame = SpriteFrame(hull, "units");
                var aimFrame = SpriteFrame(turret, "units");
                Check(_sprites.SpriteCount == 2 && idleFrame.Y == 0 && aimFrame.Y == 0.5f,
                    "The radar buggy's turret must aim independently of the hull.");
                var mountPoint = _camera!.UnprojectPosition(new Vector3(32, 0, 36 - .035f * 0.75f));
                Check(mountPoint.DistanceTo(SpriteScreenPivot(turret, buggy, "turret", "units")) < 0.05f,
                    "The radar buggy turret must pivot at its hull socket.");

                var moving = buggy with { Presentation = buggy.Presentation with { Body = new(unit.MoveAnimation, 0, 4) } };
                _sprites.Apply([moving], 1, 2, rate, [aim]);
                Check(SpriteFrame(hull, "units").X != idleFrame.X && SpriteFrame(turret, "units") == aimFrame,
                    "The radar buggy must animate its wheels while keeping the turret on target.");
                var styles = OpenRTS.Content.EffectCatalog.Load(_manifest.Effects).Weapons;
                var launch = _combatEffects!.LaunchVisual(type, 0, new(1, buggy.Id, point, north, east),
                    styles[unit.Weapons[0].Weapon.Id], Vector3.Forward);
                var muzzle = new Vector3(32, 0, 36) + new Vector3(.614f, 1.532f, -.035f) * 0.75f;
                Check(launch is { } firing &&
                    _camera.UnprojectPosition(_combatEffects.Node.ToGlobal(firing.Center)).DistanceTo(_camera.UnprojectPosition(muzzle)) < 0.05f,
                    "The radar buggy's shot must start at the rotated muzzle.");
                _sprites.Apply(poses, 1, tick, rate, weapons);
            }

            void CheckMine()
            {
                var units = _catalog!.Units.ToList();
                int mineType = units.FindIndex(u => u.Id == "mine");
                int builderType = units.FindIndex(u => u.Id == "construction-engineer");
                using var world = new SimulationWorld(units, 64 * 1024, 64 * 1024, players: [new(1, "Blue", 10000)]);
                var at = new WorldPosition(32 * 1024 + 512, 36 * 1024);
                var mine = world.AddInitialUnit(mineType, at);
                var builder = world.AddInitialUnit(builderType, at with { Y = at.Y + 3 * 1024 }, 1);
                var actors = new EntityPose[world.Count];
                world.CopyPoses(actors);
                var neutral = actors.Single(p => p.Id == mine);
                _sprites.Apply([neutral], 1, 0, rate, preview: new(mine, 1, mineType, neutral.Current, false, "owned"));
                Check(_sprites.SpriteCount == 2 && IsometricBatch("mine-upgraded").Multimesh.VisibleInstanceCount == 1,
                    "The upgrade preview must show the upgraded mine over the neutral mine.");
                CheckAnchor("mine-upgraded", neutral, "owned-mine");
                world.Step([new(1, 0, 1, CommandKind.StartUpgrade, builder, TargetEntity: mine, Action: "owned")]);
                for (int i = 0; i < 430; i++) world.Step();
                world.CopyPoses(actors);
                var upgraded = actors.Single(p => p.Id == mine);
                Check(upgraded.Presentation.Owner == 1 && upgraded.Upgrade.Completed == "owned" && upgraded.Current == neutral.Current,
                    "The neutral mine did not complete its upgrade at the same footprint anchor.");

                foreach (var (pose, file, part) in new[] { (neutral, "mine", "body"), (upgraded, "mine-upgraded", "owned-mine") })
                    for (int turn = 0; turn < 4; turn++)
                    {
                        var facing = FacingDirection.FromQuarterTurns(turn);
                        var rotated = pose with { QuarterTurns = turn, Presentation = pose.Presentation with
                            { BodyFacing = facing, PreviousBodyFacing = facing } };
                        _sprites.Apply([rotated], 1, 0, rate);
                        Check(_sprites.SpriteCount == 1 && Mathf.IsEqualApprox(SpriteFrame(file).Y, turn / 4f),
                            "A mine variant must replace the other sprite and retain all four quarter turns.");
                        CheckAnchor(file, rotated, part);
                    }
                _sprites.Apply(poses, 1, tick, rate, weapons);
            }

            void CheckOilDerrick()
            {
                var oil = poses.Single(p => _catalog!.Units[p.UnitType].Id == "oil-derrick");
                var art = oil with { Presentation = oil.Presentation with { States = 0 } };
                for (int turn = 0; turn < 4; turn++)
                {
                    var facing = FacingDirection.FromQuarterTurns(turn);
                    var rotated = art with { QuarterTurns = turn, Presentation = art.Presentation with
                        { BodyFacing = facing, PreviousBodyFacing = facing } };
                    _sprites.Apply([rotated], 1, 0, rate);
                    Check(_sprites.SpriteCount == 1 && Mathf.IsEqualApprox(SpriteFrame("oil-derrick").Y, turn / 4f),
                        "The oil derrick did not select the corresponding directional sprite.");
                    CheckAnchor("oil-derrick", rotated, "body");
                }

                var blue = art with { Presentation = art.Presentation with { Owner = 1 } };
                var red = blue with { Id = new(244), Presentation = blue.Presentation with { Owner = 2 } };
                _sprites.Apply([blue, red], 1, 0, rate);
                var batch = IsometricBatch("oil-derrick").Multimesh;
                var colors = batch.Buffer;
                Check(batch.VisibleInstanceCount == 2 && colors[16] < colors[18] && colors[36] > colors[38],
                    "The oil derrick must use its owner's team color.");
                _sprites.Apply(poses, 1, tick, rate, weapons);
            }

            void CheckCivilianTopDown()
            {
                foreach (var (id, texture) in new[] { ("civilian-house", "civilian-house-states.png"), ("civilian-apartments", "civilian-apartments-states.png"), ("civilian-clinic", "city-clinic.png"), ("civilian-market", "city-market.png"), ("civilian-library", "city-library.png"), ("civilian-station", "city-station.png"), ("civilian-community", "city-community.png") })
                {
                    var house = poses.Single(p => _catalog!.Units[p.UnitType].Id == id);
                    uint garrison = _catalog!.Units[house.UnitType].StateMask("garrisoned");
                    foreach (var (health, state, expected) in new[] { (1000, 0u, 0), (1000, garrison, 1), (500, 0u, 2), (500, garrison, 3), (250, garrison, 4) })
                    {
                        _sprites.Apply([house with { Health = health, Presentation = house.Presentation with { States = state } }], 1, tick, rate);
                        var visible = _sprites.Node.GetChildren().OfType<MultiMeshInstance3D>().Where(n => n.Multimesh.VisibleInstanceCount > 0 &&
                            n.Multimesh.Mesh.SurfaceGetMaterial(0) is ShaderMaterial m && m.GetShaderParameter("sprite_texture").AsGodotObject() is Texture2D t && t.ResourcePath.EndsWith(id == "civilian-apartments" && expected is 1 or 3 ? "civilian-apartments-team-trim.png" : texture, StringComparison.Ordinal)).ToArray();
                        Check(visible.Length == 1 && Math.Abs(((ShaderMaterial)visible[0].Multimesh.Mesh.SurfaceGetMaterial(0)).GetShaderParameter("uv_rect").AsVector4().X - expected / 5f) < 0.002f, "Civilian house state selected the wrong sprite");
                    }
                }
                foreach (var (id, batch) in new[] { ("civilian-house", "ownership-flag"), ("civilian-apartments", "civilian-apartments-team-trim") })
                {
                    var house = poses.Single(p => _catalog!.Units[p.UnitType].Id == id);
                    uint garrison = _catalog!.Units[house.UnitType].StateMask("garrisoned");
                    var blue = house with { Presentation = house.Presentation with { Owner = 1, States = garrison } };
                    var red = blue with { Id = new(1000), Presentation = blue.Presentation with { Owner = 2 } };
                    _sprites.Apply([blue, red], 1, tick, rate);
                    var marker = _sprites.Node.GetChildren().OfType<MultiMeshInstance3D>().Where(n => n.Multimesh.VisibleInstanceCount > 0 && n.Multimesh.Mesh.SurfaceGetMaterial(0) is ShaderMaterial mat && mat.GetShaderParameter("sprite_texture").AsGodotObject() is Texture2D tex && tex.ResourcePath.EndsWith(batch + ".png", StringComparison.Ordinal)).Select(n => n.Multimesh).Single();
                    var markerColors = marker.Buffer;
                    Check(marker.VisibleInstanceCount == 2 && markerColors[16] < markerColors[18] && markerColors[36] > markerColors[38], "Ownership marker did not use each player's color");
                    _sprites.Apply([blue with { Presentation = blue.Presentation with { States = 0 } }], 1, tick, rate);
                    Check(marker.VisibleInstanceCount == 0, "Ownership marker appeared without a garrison");
                }
                foreach (string name in new[] { "clinic", "market", "library", "station", "community" })
                {
                    using var art = Image.LoadFromFile(ProjectSettings.GlobalizePath($"res://assets/buildings/city-{name}.png"));
                    foreach (int cell in new[] { 1, 3 })
                    {
                        int pixels = 0, left = (int)Math.Round(cell * art.GetWidth() / 5.0), right = (int)Math.Round((cell + 1) * art.GetWidth() / 5.0);
                        for (int y = 85; y < 645; y += 2) for (int x = left; x < right; x += 2)
                        { var color = art.GetPixel(x, y); if (color.A > 0.5f && color.R > 0.7f && color.B > 0.7f && color.G < 0.35f) pixels++; }
                        Check(pixels > 20, $"Missing team-color trim: {name}, state {cell}");
                    }
                }
            }

            void CheckCivilianIsometric()
            {
                foreach (string name in new[] { "house", "apartments", "clinic", "market", "library", "station", "community" })
                {
                    string id = "civilian-" + name;
                    var building = poses.Single(p => _catalog!.Units[p.UnitType].Id == id);
                    uint occupied = _catalog!.Units[building.UnitType].StateMask("garrisoned");
                    foreach (var (hp, state, part, column) in new[] {
                        (1000, 0u, "normal", 0), (1000, occupied, "garrison", 1),
                        (500, 0u, "damaged", 2), (500, occupied, "damaged-garrison", 3), (250, occupied, "nearly", 4) })
                        for (int turn = 0; turn < 4; turn++)
                        {
                            var facing = FacingDirection.FromQuarterTurns(turn);
                            var rotated = building with { Health = hp, QuarterTurns = turn, Presentation = building.Presentation with
                                { States = state, BodyFacing = facing, PreviousBodyFacing = facing } };
                            _sprites.Apply([rotated], 1, 0, rate);
                            var frame = SpriteFrame(id);
                            Check(_sprites.SpriteCount == 1 && Mathf.IsEqualApprox(frame.X, column * frame.Z) &&
                                Mathf.IsEqualApprox(frame.Y, turn / 4f), $"{id}.{part} selected the wrong state or direction.");
                            CheckAnchor(id, rotated, part);
                        }

                    var blue = building with { Health = 1000, Presentation = building.Presentation with { Owner = 1, States = occupied } };
                    var red = blue with { Id = new(1000), Presentation = blue.Presentation with { Owner = 2 } };
                    _sprites.Apply([blue, red], 1, 0, rate);
                    var instance = IsometricBatch(id).Multimesh;
                    var colors = instance.Buffer;
                    Check(instance.VisibleInstanceCount == 2 && colors[16] < colors[18] && colors[36] > colors[38],
                        $"{id} did not use the garrison owner's color.");
                }
            }

            void CheckDoor(EntityPose pose, string file, uint state, int rows, int firstRow = 0)
            {
                int door = _catalog.Units[pose.UnitType].AnimationIndex("door");
                for (int turn = 0; turn < 4; turn++)
                {
                    var facing = FacingDirection.FromQuarterTurns(turn);
                    var presentation = pose.Presentation with { States = state, BodyFacing = facing, PreviousBodyFacing = facing,
                        Body = new(door, tick, 12), Weapon = AnimationPlayback.None };
                    var turned = pose with { QuarterTurns = turn, Presentation = presentation };
                    _sprites.Apply([turned], 1, tick, rate);
                    var frame = SpriteFrame(file);
                    Check(Math.Abs(frame.Y - (firstRow + turn) / (float)rows) < 0.001f && Math.Abs(frame.X - frame.Z) < 0.001f,
                        $"{file} selected the wrong door frame at quarter turn {turn}, state {state}.");
                    _sprites.Apply([turned], 1, tick + 8, rate);
                    Check(SpriteFrame(file).X == 0, $"{file} door did not open at quarter turn {turn}, state {state}.");
                    if (_manifest.Settings.Presentation.Camera != OpenRTS.Content.GameCamera.Isometric) continue;
                    CheckAnchor(file, turned, state == 0 ? "body" : "heavy");
                    if (state != 0) continue;
                    _sprites.Apply([], 1, tick, rate, preview: new(default, turned.Presentation.Owner, turned.UnitType,
                        turned.Current, false) { QuarterTurns = turn });
                    CheckAnchor(file, turned, "body");
                }
            }

            void CheckAnchor(string file, EntityPose pose, string partId, string category = "buildings")
            {
                var pivot = SpriteScreenPivot(file, pose, partId, category);
                var ground = new Vector3(pose.Current.X, pose.Current.Elevation, pose.Current.Y) / WorldPosition.UnitsPerCell;
                float error = pivot.DistanceTo(_camera!.UnprojectPosition(ground));
                Check(error < 0.05f, $"{file}.{partId} at quarter turn {pose.QuarterTurns} missed its ground anchor by {error:F2} pixels.");
            }

            Vector2 SpriteScreenPivot(string file, EntityPose pose, string partId, string category = "buildings")
            {
                var model = _modelCatalog!.Models[_catalog!.Presentations[pose.UnitType].Model!];
                var part = model.Parts.Single(p => p.Id == partId);
                var sprite = IsometricBatch(file, category).Multimesh.Buffer;
                float x = (float)part.PivotX - 0.5f, z = (float)part.PivotY - 0.5f;
                var pivot = new Vector3(sprite[3] + x * sprite[0] + z * sprite[2],
                    sprite[7] + x * sprite[4] + z * sprite[6], sprite[11] + x * sprite[8] + z * sprite[10]);
                return _camera!.UnprojectPosition(pivot);
            }

            void CheckSelectionScale()
            {
                var selected = _selection.Ids.ToArray();
                var building = poses.Single(p => p.Id.Value == 2);
                var structure = _catalog!.Units[building.UnitType].WithRotation(building.QuarterTurns).Footprint!;
                var unit = poses.First(p => _catalog.Units[p.UnitType].Movable);
                var definition = _catalog.Units[unit.UnitType];
                var model = _modelCatalog!.Models[_catalog.Presentations[unit.UnitType].Model!];
                float radius = (float)model.Parts.Where(part => part.Parent < 0).Max(part => Math.Max(part.Width, part.Height)) / 2;
                CheckRing(building.Id, structure.HalfWidth / 1024f * MathF.Sqrt(2), structure.HalfHeight / 1024f * MathF.Sqrt(2), 1);
                CheckRing(unit.Id, Math.Max(radius, definition.Body.HalfWidth / 1024f), Math.Max(radius, definition.Body.HalfHeight / 1024f), 0.5f);
                _selection.Change(selected, OpenRTS.Godot.Controls.SelectionChange.Replace);
                UpdateSelection(1);

                void CheckRing(EntityId id, float width, float height, float scale)
                {
                    _selection.Change([id], OpenRTS.Godot.Controls.SelectionChange.Replace);
                    UpdateSelection(1);
                    Check(_selectionRing!.Multimesh.VisibleInstanceCount == 1 &&
                        Mathf.IsEqualApprox(_ringBuffer[0], (width + 0.2f) / 0.88f * scale) &&
                        Mathf.IsEqualApprox(_ringBuffer[10], (height + 0.2f) / 0.88f * scale), "Incorrect isometric selection ring size.");
                }
            }

            void CheckFactoryExit(int factoryType)
            {
                var catalog = _catalog!;
                int Type(string id) => catalog.Units.ToList().FindIndex(unit => unit.Id == id);
                int powerPlant = Type("power-plant"), apc = Type("apc-mg");
                var ground = catalog.Locomotion!.TerrainIndex("grass");
                var center = new WorldPosition(10752, 10752);
                for (int turn = 0; turn < 4; turn++)
                {
                    var grid = new NavigationGrid(20, 20, terrain: Enumerable.Repeat(ground, 400));
                    using var world = new SimulationWorld(catalog.Units, 20 * 1024, 20 * 1024,
                        navigationGrid: grid, players: [new(1, "Blue", 10000)]);
                    var factory = world.AddInitialMapUnit(factoryType, center, 1, isStart: false, quarterTurns: turn);
                    world.AddInitialUnit(powerPlant, new(5632, 4096), 1);
                    var (x, y) = turn switch { 1 => (-3328, 0), 2 => (0, -3328), 3 => (3328, 0), _ => (0, 3328) };
                    int apron = grid.CellAt(new(center.X + x / 2, center.Y + y / 2));
                    Check(!world.NavigationGrid!.IsBlocked(apron) && !world.NavigationGrid.CanStop(apron)
                        && world.NavigationGrid.IsBlocked(grid.CellAt(center)), "Factory footprint does not match the assembly hall and apron.");
                    world.Step([new(1, 0, 1, CommandKind.EnqueueTask, factory, Action: "apc")]);
                    while (world.Count == 2 && world.Tick < 300) world.Step();
                    Check(world.Count == 3, $"APC production did not finish at quarter turn {turn}.");
                    var poses = new EntityPose[world.Count];
                    world.CopyPoses(poses);
                    Check(poses.Single(p => p.UnitType == apc).Current == new WorldPosition(center.X + x, center.Y + y),
                        $"APC did not emerge from the center of the apron at quarter turn {turn}.");
                }
            }
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        MultiMeshInstance3D Batch(string name) => _sprites!.Node.GetChildren().OfType<MultiMeshInstance3D>().First(n => n.Name == "Sprite_" + name);
    }
    private async Task CaptureBuildings(string name)
    {
        string path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_capturePath!))!, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var image = GetViewport().GetTexture().GetImage(); if (image.SavePng(path) != Error.Ok) throw new IOException("Building capture failed.");
    }
    private async Task CaptureDoorOcclusion(EntityPose[] poses, long tick, int rate)
    {
        var building = poses.Single(p => p.Id.Value == 2); var actor = poses.Single(p => p.Id.Value == 12);
        var position = building.Current with { Y = building.Current.Y + 1024 };
        actor = actor with { Previous = position, Current = position, Presentation = actor.Presentation with { Body = new(_catalog!.Units[actor.UnitType].IdleAnimation, 0, 0), Weapon = AnimationPlayback.None } };
        _camera!.Size = 4; _camera.Position = new(building.Current.X / 1024f, 80, position.Y / 1024f);
        _footprintView!.Apply(null, default); _selection.Change([], OpenRTS.Godot.Controls.SelectionChange.Replace); UpdateSelection(1);
        int closed = 0, opened = 0;
        for (int phase = 0; phase < 2; phase++)
        {
            var view = building.Presentation with { Weapon = phase == 0 ? AnimationPlayback.None : new(_catalog.Units[building.UnitType].AnimationIndex("door"), tick - 23, 40) };
            _sprites!.Apply([building with { Presentation = view }, actor], 1, tick, rate);
            _label!.Text = phase == 0 ? "Дверь закрыта · перекрывает юнита" : "Дверь открыта · юнит виден в проёме";
            await CaptureBuildings(phase == 0 ? "buildings-door-closed.png" : "buildings-door-open.png");
            using var screenshot = GetViewport().GetTexture().GetImage(); int pixels = TeamPixels(screenshot, actor, true);
            if (phase == 0) closed = pixels; else opened = pixels;
        }
        if (opened <= closed + 6) throw new InvalidOperationException($"Door overlay failed to occlude/reveal unit: {closed}/{opened} blue pixels.");
        GD.Print($"OPENRTS_DOOR_OCCLUSION:PASS closed={closed},open={opened}");
    }
}
