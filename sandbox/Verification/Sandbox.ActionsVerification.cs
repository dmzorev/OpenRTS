using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using OpenRTS.Core;
using OpenRTS.Content;
using DeclarativeContent;

public partial class Sandbox
{
    private int _actionsCheck; private bool _checkingActions; private EntityId _firstSite, _secondSite;
    private async void VerifyActions(FrameExchange.FrameLease lease)
    {
        if (_checkingActions) return; _checkingActions = true;
        try
        {
            long tick = lease.Tick; if (_label is not null) _label.Text = ShowcaseLabel(tick); var tasks = lease.Tasks.ToArray(); var poses = lease.Poses.ToArray();
            if (_actionsCheck == 0)
            {
                Check(tick == 0 && _session!.IsPaused, "Action fixture must start paused");
                SelectTypes(12, 16, 3, 1); Check(_panelTypes.Length == 4 && _catalog!.Presentations[_activeType].Actions!.Count > 0, "Custom types did not sort first");
                int first = _activeType; HandleRtsKey(new InputEventKey(), Key.Tab); Check(_activeType != first, "Tab did not cycle types");
                HandleRtsKey(new InputEventKey { ShiftPressed = true }, Key.Tab); Check(_activeType == first, "Shift+Tab did not cycle backwards");
                _typeRow!.GetChildren().OfType<Button>().Last().EmitSignal(BaseButton.SignalName.Pressed); Check(_activeType == _panelTypes[^1], "Clicking a type did not switch its panel");
                SelectTypes(12);
                var engineerPoint = Pixel(poses.Single(p => p.Id.Value == 16).Current);
                HandleShowcaseClick(new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.Left, Position = engineerPoint });
                HandleShowcaseClick(new InputEventMouseButton { Pressed = false, ButtonIndex = MouseButton.Left, Position = engineerPoint });
                Check(_activeType == poses.Single(p => p.Id.Value == 16).UnitType && HandleCustomKey(new InputEventKey(), Key.Q) && _armedCustom == "repair", "A same-frame selection left the previous action panel active");
                SelectTypes(12);
                var original = _catalog!;
                var source = File.ReadAllText(ProjectSettings.GlobalizePath("res://content/units.yaml")).Replace("Cursor: grenade,", "Hotkey: A, Cursor: grenade,");
                var weapon = WeaponCatalog.Load([new("weapons", File.ReadAllText(ProjectSettings.GlobalizePath("res://content/weapons.yaml")))]);
                _catalog = UnitCatalog.Load([new("units", source), new("buildings", File.ReadAllText(ProjectSettings.GlobalizePath("res://content/buildings.yaml")))], original.Locomotion, weapon);
                Check(_catalog.Summary == original.Summary, "Hotkey changed gameplay content");
                _panelRevision = -1; RefreshSelection();
                HandleRtsKey(new InputEventKey(), Key.A); Check(_armedCustom == "grenade" && _armedAction is null, "Custom A lost to standard attack-move");
                _catalog = original; _panelRevision = -1; RefreshSelection();
                ClickAction("grenade", right: true); HandleRtsKey(new InputEventKey(), Key.Q);
                _pointer = Pixel(new(11 * 1024, 32 * 1024)); UpdateRtsCursor(); Check(_rtsOverlay!.CustomCursor == "grenade", "Custom cursor was not registered");
                HandleShowcaseClick(new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.Left, Position = _pointer });
                _actionsCheck = 1; Steps(5); return;
            }
            if (_actionsCheck == 1 && tick >= 5)
            {
                var weapon = AbilityPose(new(12), "grenade");
                Check(weapon is { AutoCast: true } && weapon.Value.ReadyTick > tick && _cooldowns["grenade"].Value > 0, "Cooldown/autocast feedback missing");
                HandleRtsKey(new InputEventKey(), Key.Q); _pointer = Pixel(new(11 * 1024, 32 * 1024)); UpdateRtsCursor();
                await CaptureActions("actions-grenade.png"); _armedCustom = null;
                SelectTypes(2); ClickAction("rifleman", shift: true); ClickAction("rocketeer", right: true, shift: true);
                HandleShowcaseClick(new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.Right, Position = Pixel(new(47 * 1024, 18 * 1024)) });
                _actionsCheck = 2; Steps(1); return;
            }
            if (_actionsCheck == 2 && tick >= 6)
            {
                Check(tasks.Count(t => t.Producer.Value == 2) == 10 && tasks.Count(t => t.Producer.Value == 2 && t.Repeat) == 5 && _taskButtons.Count == 2, "Shift/renewed orders did not form two stacks");
                Check(poses.Single(p => p.Id.Value == 2).HasRally, "RMB did not set building rally");
                await CaptureActions("actions-production-queue.png");
                ClickStack("rifleman", false, false); ClickStack("rifleman", false, true); ClickStack("rocketeer", true, false);
                ClickAction("rifleman"); SelectTypes(5); ClickAction("engineer"); SelectTypes(16); ClickAction("site-power");
                var shift = new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, ShiftPressed = true };
                HandleConstructionClick(shift, new(23 * 1024, 30 * 1024), null);
                Check(_sitePlacement is not null, "Shift left construction placement mode");
                HandleConstructionClick(shift, new(23 * 1024, 28 * 1024), null);
                Check(_sitePlacement is not null, "Second Shift click left placement mode");
                _actionsCheck = 3; Steps(1); return;
            }
            if (_actionsCheck == 3 && tick >= 7)
            {
                Check(tasks.Count(t => t.Producer.Value == 2) == 1 && tasks.Count(t => t.Producer.Value == 5) == 1,
                    "Stack removal or engineer queue failed");
                _firstSite = poses.Single(p => p.Construction.Active).Id;
                _secondSite = lease.ConstructionProjects.ToArray().Single().Id;
                Check(_firstSite.Value < _secondSite.Value && poses.Single(p => p.Id.Value == 16).Order.QueuedCount == 1,
                    "The engineer did not keep the second Shift placement in the queue");
                _actionsCheck = 4; Steps(130); return;
            }
            if (_actionsCheck == 4 && tick >= 137)
            {
                var rally = poses.Single(p => p.Id.Value == 2).Rally;
                Check(poses.Any(p => p.Id.Value > 28 && p.Order.Destination == rally), "Unit production/rally failed");
                Check(!poses.Single(p => p.Id == _firstSite).Construction.Active &&
                    (poses.Any(p => p.Id == _secondSite && p.Construction.Active) ||
                        lease.ConstructionProjects.ToArray().Any(p => p.Id == _secondSite)), "The second site did not follow the first");
                await CaptureActions("actions-building-started.png");
                _actionsCheck = 6; Steps(160); return;
            }
            if (_actionsCheck == 6 && tick >= 297)
            {
                Check(!poses.Single(p => p.Id == _secondSite).Construction.Active, "The second site did not complete");
                await CaptureActions("actions-building-complete.png");
                var commands = _session!.GetCommandLog();
                Check(commands.Count(c => c.Kind == CommandKind.UseAbility) == 1 &&
                    commands.Count(c => c.Kind == CommandKind.PlanConstruction && c.Queued) == 2,
                    "UI actions did not share the command journal");
                SpawnTransportFixture();
                _actionsCheck = 7; Steps(1); return;
            }
            if (_actionsCheck == 7 && tick >= 298)
            {
                for (int i = 0; i < 2; i++)
                {
                    var site = TransportSite(i);
                    var carrier = poses.Single(p => p.Current == site);
                    var passenger = poses.Single(p => p.Current.X == site.X - 768 && p.Current.Y == site.Y);
                    SelectTypes(passenger.Id.Value);
                    _pointer = Pixel(site); UpdateRtsCursor();
                    Check(_rtsOverlay!.Cursor == OpenRTS.Godot.Controls.RtsCursor.Enter, $"Transport cursor {_rtsOverlay.Cursor}; eligible={CanBoard(passenger.Id, carrier)}, armed={_armedAction}/{_armedCustom}, picked={Pick(site)?.Id}, pixel={_pointer}, panel={_rtsPanels.Any(p => p.GetGlobalRect().HasPoint(_pointer))}");
                    HandleShowcaseClick(new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.Left, Position = _pointer });
                }
                _actionsCheck = 8; Steps(1); return;
            }
            if (_actionsCheck == 8 && tick >= 299)
            {
                Check(_storedPoses.Count == 2 && _loadedTransports.Count == 2, "Contextual click did not board both passengers");
                SelectTypes(_loadedTransports.Select(id => id.Value).ToArray());
                RefreshStoredPanel();
                var button = _storedRow!.GetChildren().OfType<Button>().Single(b => b.Text == "Вывести всех");
                Check(_storedRow.GetChildren().OfType<Button>().Count() == 3, "Stored panel must show passengers across selected carriers");
                button.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.Left });
                Check(_armedAction == UnitActionKind.UnloadAt, "Single click did not arm targeted unload");
                button.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.Left, DoubleClick = true });
                _actionsCheck = 9; Steps(1); return;
            }
            if (_actionsCheck == 9 && tick >= 300)
            {
                Check(_storedPoses.Count == 0 && _session!.GetCommandLog().Last().Recipients?.Count == 2, "Double click did not unload all selected carriers");
                GD.Print("OPENRTS_ACTIONS:PASS command panel, production, contextual boarding and multi-transport unload");
                _verificationDone = true; GetTree().Quit();
            }
        }
        catch (Exception error) { _verificationDone = true; GD.PushError(error.ToString()); GetTree().Quit(1); }
        finally { _checkingActions = false; }
        static WorldPosition TransportSite(int i) => new((30 + i * 5) * 1024, 32 * 1024);
        void SpawnTransportFixture()
        {
            int apc = _catalog!.Units.ToList().FindIndex(u => u.Id == "apc-mg");
            int infantry = _catalog.Units.ToList().FindIndex(u => u.Id == "rifleman");
            for (int i = 0; i < 2; i++)
            {
                var site = TransportSite(i);
                _session!.Submit(CommandKind.Spawn, site, unitType: apc, owner: 1);
                _session.Submit(CommandKind.Spawn, site with { X = site.X - 768 }, unitType: infantry, owner: 1);
            }
        }
        void SelectTypes(params uint[] ids) { _selection.Change(ids.Select(id => new EntityId(id)), OpenRTS.Godot.Controls.SelectionChange.Replace); RefreshSelection(); }
        void ClickAction(string action, bool right = false, bool shift = false) => _customButtons[action].EmitSignal(Control.SignalName.GuiInput,
            new InputEventMouseButton { Pressed = true, ButtonIndex = right ? MouseButton.Right : MouseButton.Left, ShiftPressed = shift });
        void ClickStack(string action, bool right, bool shift) => _taskButtons.First(p => p.Key.Contains(":" + action + ":", StringComparison.Ordinal)).Value.Button.EmitSignal(Control.SignalName.GuiInput,
            new InputEventMouseButton { Pressed = true, ButtonIndex = right ? MouseButton.Right : MouseButton.Left, ShiftPressed = shift });
        void Steps(int count) { for (int i = 0; i < count; i++) _session!.StepOnce(); }
        Vector2 Pixel(WorldPosition p) => _camera!.UnprojectPosition(new(p.X / 1024f, 0, p.Y / 1024f));
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
    private async Task CaptureActions(string name)
    {
        if (_capturePath is null || DisplayServer.GetName() == "headless") return;
        string path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_capturePath))!, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        using var image = GetViewport().GetTexture().GetImage(); if (image.SavePng(path) != Error.Ok) throw new IOException("Action panel capture failed.");
    }
}
