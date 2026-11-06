using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using OpenRTS.Core;

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
            _sprites.Apply(poses, 1, tick, rate, weapons);
            var radar = Batch("radar").Multimesh.Buffer; float angle = (float)((tick + 1) / (double)rate * 65 * Math.PI / 180);
            Check(Math.Abs(radar[0] - MathF.Cos(angle) * 1.45f) < 0.001f && Math.Abs(radar[8] - MathF.Sin(angle) * 1.45f) < 0.001f, "Radar did not rotate in simulation time");
            var power = Batch("power-plant").Multimesh; var colors = power.Buffer;
            Check(power.VisibleInstanceCount == 5 && colors[2 * 20 + 12] < 0.6f && colors[1 * 20 + 12] > 0.99f && colors[3 * 20 + 19] > 0.005f && colors[4 * 20 + 19] > 0.005f, "Normal/damaged/working/combined states are not distinct");
            bool openDoor = _sprites.Node.GetChildren().OfType<MultiMeshInstance3D>().Any(n => n.Multimesh.VisibleInstanceCount > 0 &&
                n.Multimesh.Mesh.SurfaceGetMaterial(0) is ShaderMaterial m && m.GetShaderParameter("sprite_texture").AsGodotObject() is Texture2D t && t.ResourcePath.EndsWith("door-strip.png", StringComparison.Ordinal) && m.GetShaderParameter("uv_rect").AsVector4().X > 0.66f);
            Check(openDoor, "Door overlay did not advance to the open frame");
            var layerA = poses.Single(p => p.Id.Value == 17); var layerB = poses.Single(p => p.Id.Value == 18) with { Previous = layerA.Current, Current = layerA.Current };
            _sprites.Apply([layerB, layerA], 1, tick, rate); var overlap = Batch("power-plant").Multimesh.Buffer;
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
                await CaptureDoorOcclusion(poses, tick, rate);
            }
            GD.Print($"OPENRTS_BUILDINGS:PASS footprint terrain, wrecks, sizes, states, radar, doors, stacking and 10000-building batches; nodes={nodes}");
            GD.Print("OPENRTS_SIMULATION:" + JsonSerializer.Serialize(report)); GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        MultiMeshInstance3D Batch(string name) => _sprites!.Node.GetChildren().OfType<MultiMeshInstance3D>().First(n => n.Name == "Sprite_" + name);
    }
    private async Task CaptureBuildings(string name)
    {
        string path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_capturePath!))!, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
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
