using System;
using System.Linq;
using System.IO;
using Godot;
using OpenRTS.Core;
using OpenRTS.Core.Orders;
using OpenRTS.Godot.Controls;

public partial class Sandbox
{
    private static void CheckSkirmish(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private int _skirmishCheck;
    private void VerifySkirmish(FrameExchange.FrameLease frame)
    {
        if (_skirmishCheck == 6) return;
        if (_skirmishCheck == 0)
        {
            CheckSkirmish(frame.Poses.Length == 12 && frame.Players.Length == 3, "Skirmish players/objects not initialized");
            CheckSkirmish(frame.Players.ToArray().Where(p => p.Id != 0).All(p => p.Credits == 5000), "Initial funds wrong");
            CheckSkirmish(_rtsOverlay!.HealthBars.Count == 1, "Selected base has no world health bar");
            var window = new Rect2I(-1200, 100, 960, 600);
            CheckSkirmish(WindowEdgeDirection(new(-241, 400), window) == Vector2I.Right &&
                WindowEdgeDirection(new(-200, 750), window) == new Vector2I(1, 1) &&
                WindowEdgeDirection(new(-1300, 50), window) == new Vector2I(-1, -1) &&
                WindowEdgeDirection(new(-500, 550), window) == Vector2I.Zero, "Edge scroll ignored window bounds or an outside pointer");
            var radarCenter = _minimap!.GetGlobalRect().GetCenter();
            foreach (bool pressed in new[] { true, false })
                GetViewport().PushInput(new InputEventMouseButton { Position = radarCenter, ButtonIndex = MouseButton.Left, Pressed = pressed }, inLocalCoords: true);
            var mapCenter = GroundPoint(Playfield().GetCenter())!.Value;
            CheckSkirmish(Mathf.IsEqualApprox(mapCenter.X, _mapWidth / 2) && Mathf.IsEqualApprox(mapCenter.Z, _mapDepth / 2),
                $"Minimap click at {radarCenter} in {_minimap.GetGlobalRect()} moved the camera to {mapCenter}");
            ToggleMatchMenu();
            CheckSkirmish(_matchMenu!.Visible && _session!.IsPaused, "Match menu did not pause input and playback");
            ToggleMatchMenu();
            CheckSkirmish(!_matchMenu.Visible && _session!.IsPaused, "Closing menu lost the previous pause state");
            _selection.Change([new(1)], SelectionChange.Replace); RefreshSelection(); ActivateCustom("truck", false, false);
            _selection.Change([new(4)], SelectionChange.Replace); RefreshSelection();
            CheckSkirmish(Controlled().Single().Value == 4 && CanIssue(UnitActionKind.Move), "Red player cannot be controlled");
            ActivateCustom("capture", false, false);
            var target = frame.Poses.ToArray().Single(p => p.Id.Value == 7);
            CenterCamera(new Vector2(target.Current.X, target.Current.Y) / WorldPosition.UnitsPerCell);
            var before = GroundPoint(Playfield().GetCenter())!.Value;
            var panel = _customButtons["capture"];
            ToggleTerminalLayout();
            CheckSkirmish(!_rightHud && _armedCustom == "capture" && Controlled().Single().Value == 4 &&
                ReferenceEquals(panel, _customButtons["capture"]), "Layout switch reset selection, targeting or actions");
            CheckSkirmish(GroundPoint(Playfield().GetCenter())!.Value.DistanceTo(before) < 0.001f, "Layout switch moved the world focus");
            CheckSkirmish(CommandPoint(Playfield().End + new Vector2(-10, 10)) is null, "HUD accepts world targeting");
            ToggleTerminalLayout();
            CheckSkirmish(_rightHud && !DiagnosticsVisible, "Sidebar or hidden diagnostics did not restore");
            _pointer = Screen(target); UpdateRtsCursor();
            CheckSkirmish(_rtsOverlay!.CustomCursor == "capture", "Capture cursor unavailable");
            HandleCustomTarget(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true }, target.Current, target);
            _skirmishCheck = 1; _session!.StepOnce(); return;
        }
        if (_skirmishCheck == 1)
        {
            if (frame.Tick < 1) return;
            CheckSkirmish(frame.Players.ToArray().Single(p => p.Id == 1).Credits == 4650 && frame.Players.ToArray().Single(p => p.Id == 2).Credits == 5000, "Production charged the wrong player");
            CheckSkirmish(frame.Poses.ToArray().Single(p => p.Id.Value == 4).Order.Kind == UnitActionKind.Capture, "Capture button did not issue a simulation action");
            _selection.Change([new(2), new(4)], SelectionChange.Replace); RefreshSelection();
            CheckSkirmish(Controlled().Length == 2 && CanIssue(UnitActionKind.Move), "Mixed player control failed");
            CheckSkirmish(!HandleBuildingKey(Key.B), "Skirmish allowed free editor construction");

            void Place(int builder, Key key, params WorldPosition[] sites)
            {
                _selection.Change([new((uint)builder)], SelectionChange.Replace);
                RefreshSelection();
                UpdateConstructionView(frame);
                CheckSkirmish(HandleRtsKey(new InputEventKey(), key) && _sitePlacement is not null, "Construction hotkey did not arm placement");
                var placement = _sitePlacement;
                ToggleTerminalLayout();
                CheckSkirmish(_sitePlacement == placement, "Layout switch reset construction placement");
                ToggleTerminalLayout();
                foreach (var site in sites)
                {
                    HandleConstructionClick(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, ShiftPressed = true }, site, null);
                    CheckSkirmish(_sitePlacement is not null, "Shift did not keep construction placement armed");
                }
            }
            Place(2, Key.E, new(24 * 1024 + 512, 25 * 1024), new(27 * 1024 + 512, 30 * 1024));
            Place(4, Key.T, new WorldPosition(55 * 1024 + 512, 37 * 1024));
            _skirmishCheck = 2; _session!.StepOnce(); return;
        }
        if (_skirmishCheck == 2)
        {
            if (frame.Tick < 2) return;
            CheckSkirmish(frame.ConstructionProjects.Length == 3 && _visibleProjects.Length == 1 && _visibleProjects[0].Owner == 2, "Red project visibility is wrong");
            var engineer = frame.Poses.ToArray().Single(p => p.Id.Value == 2);
            CheckSkirmish(engineer.Order.Kind == UnitActionKind.Build && engineer.Order.QueuedCount == 1, "Shift did not queue construction orders");
            _selection.Change([new(1)], SelectionChange.Replace); RefreshSelection();
            UpdateConstructionView(frame);
            CheckSkirmish(_visibleProjects.Length == 2 && _visibleProjects.All(p => p.Owner == 1), "Blue can see an enemy project");
            UpdateFootprintPreview();
            CheckSkirmish(!_footprintView!.Node.GetNode<MeshInstance3D>("ConstructionRange").Visible, "Supply Center still has a construction radius");
            var project = _visibleProjects[0];
            CheckSkirmish(PickProject(project.Position)?.Id == project.Id, "Construction ghost is not selectable");
            _sprites!.Apply(frame.Poses, 1, frame.Tick, frame.TickRate, frame.Weapons);
            int sprites = _sprites.SpriteCount;
            _sprites.Apply(frame.Poses, 1, frame.Tick, frame.TickRate, frame.Weapons, _visibleProjects);
            CheckSkirmish(_sprites.SpriteCount > sprites, "Construction ghosts did not enter sprite batches");
            SelectProject(project);
            CheckSkirmish(_cancelProject!.Visible && HandleRtsKey(new InputEventKey(), Key.Delete), "Selected project has no cancel action");
            _skirmishCheck = 3; _session!.StepOnce(); return;
        }
        var mine = frame.Poses.ToArray().Single(p => p.Id.Value == 5);
        if (_skirmishCheck == 3)
        {
            if (frame.Tick < 3) return;
            CheckSkirmish(frame.ConstructionProjects.Length == 2 && _visibleProjects.Length == 1, "Cancel action did not remove the selected project");
            _selection.Change([new(2)], SelectionChange.Replace);
            RefreshSelection();
            ActivateCustom("claim-mine", false, false);
            CenterCamera(new Vector2(mine.Current.X, mine.Current.Y) / WorldPosition.UnitsPerCell);
            _pointer = Screen(mine);
            UpdateRtsCursor();
            CheckSkirmish(MinePreview() is { Id.Value: 5 } && _rtsOverlay!.CustomCursor == "build", "Mine preview or cursor is missing");
            HandleCustomTarget(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true }, mine.Current, mine);
            _skirmishCheck = 4;
            _session!.StepOnce();
            return;
        }
        if (_skirmishCheck == 4)
        {
            if (frame.Tick < 4) return;
            CheckSkirmish(mine.Supply is { ProjectOwner: 1, ProjectWork: 0 } && _visibleProjects.Any(p => p.Id == mine.Id),
                "Clicking the mine did not create an owner-visible blueprint on the existing entity");
            _skirmishCheck = 5;
            for (int i = 4; i < 650; i++) _session!.StepOnce();
            return;
        }
        if (frame.Tick < 650) return;
        CheckSkirmish(mine.Presentation.Owner == 1 && mine.Supply.ProjectRequired == 0 &&
            frame.Poses.ToArray().Any(p => _catalog!.Units[p.UnitType].Supply?.Role == SupplyRole.Truck),
            "Mine upgrade or truck production did not complete in the live match");
        GD.Print("OPENRTS_SKIRMISH:players, capture, construction, supply truck, mine blueprint, layout parity and camera focus passed");
        _verificationDone = true;
        if (_capturePath is null || DisplayServer.GetName() == "headless") GetTree().Quit();
        else CaptureTerminal();
    }

    private async void CaptureTerminal()
    {
        try
        {
            _selection.Change([new(1)], SelectionChange.Replace);
            RefreshSelection();
            var yard = Selected()!.Value.Current;
            CenterCamera(new Vector2(yard.X, yard.Y) / WorldPosition.UnitsPerCell);
            var directory = Path.GetFullPath(_capturePath!);
            Directory.CreateDirectory(directory);
            _verificationDone = false;
            _skirmishCheck = 6;
            foreach (var name in new[] { "terminal-right.png", "terminal-bottom.png" })
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var image = GetViewport().GetTexture().GetImage();
                if (image.SavePng(Path.Combine(directory, name)) != Error.Ok)
                    throw new IOException("Failed to capture terminal HUD.");
                if (_rightHud) ToggleTerminalLayout();
            }
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GetTree().Quit(1);
        }
    }
}
