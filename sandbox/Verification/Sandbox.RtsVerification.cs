using System;
using System.IO;
using System.Linq;
using Godot;
using OpenRTS.Core;
using OpenRTS.Core.Orders;
using OpenRTS.Godot.Controls;

public partial class Sandbox
{
    private int _rtsCheck;
    private void VerifyRtsControls(long tick)
    {
        try
        {
            if (_rtsCheck == 0)
            {
                Check(tick == 0 && _session!.IsPaused, "RTS test must begin paused");
                Click(1); Check(_selected.Value == 1 && _selection.Count == 1, "Single click");
                Click(2, shift: true); Check(_selection.Count == 2, "Shift add");
                Click(1, shift: true); Check(_selection.Count == 1 && _selected.Value == 2, "Shift toggle");
                var blue = _displayPoses.Take(_displayCount).Where(p => p.Presentation.Owner == 1).Select(Screen).ToArray();
                var start = new Vector2(blue.Min(p => p.X) - 30, blue.Min(p => p.Y) - 25);
                var end = new Vector2(blue.Max(p => p.X) + 30, blue.Max(p => p.Y) + 25);
                _UnhandledInput(new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.Left, Position = start });
                _Input(new InputEventMouseMotion { Position = end, Relative = end - start, ButtonMask = MouseButtonMask.Left });
                Check(_rtsOverlay!.Marquee is not null, "Drag marquee");
                _Input(new InputEventMouseButton { Pressed = false, ButtonIndex = MouseButton.Left, Position = end });
                Check(_selection.Count == 10 && Controlled().Length == 10, "Box must select only owned units");
                Click(6, shift: true); Check(_selection.Count == 10, "Shift cannot mix an enemy into an owned selection");
                var originals = _displayPoses.Take(_displayCount).ToArray();
                _displayPoses = [.. originals, originals[0] with { Id = new(100001), Current = originals[0].Current with { X = originals[0].Current.X + 2048 } }];
                _displayCount++; RefreshSelection(); Click(1, doubleClick: true);
                Check(_selection.Count == 2 && _selection.Contains(new(100001)), "Double click selects visible same-type units");
                Click(2, ctrl: true); Check(_selection.Count == 1 && _selected.Value == 2, "Ctrl-click type filter");
                _displayPoses = originals; _displayCount = originals.Length; RefreshSelection();
                Click(1); Click(2, shift: true); KeyInput(Key.Key1, ctrl: true);
                _selection.Change([], SelectionChange.Replace); KeyInput(Key.Key1);
                Check(_selection.Count == 2 && _selection.Contains(new(1)), "Control group store/recall");
                Click(6); Check(!CanIssue(UnitActionKind.Move) && !ActivateAction(UnitActionKind.AttackMove), "Enemy inspection cannot issue commands");
                Click(1, shift: true); Check(_selection.Count == 1 && _selected.Value == 1 && CanIssue(UnitActionKind.Move), "Shift mixed inspected enemies with owned units");
                Click(6); KeyInput(Key.Key1, ctrl: true); KeyInput(Key.Key1, shift: true);
                Check(_selection.Count == 2 && CanIssue(UnitActionKind.Move), "Enemy inspection overwrote/mixed an owned control group");
                Check(_session!.GetCommandLog().Length == 0, "Selection issued an authoritative order");
                Click(1); Click(2, shift: true); KeyInput(Key.M); Check(_armedAction == UnitActionKind.Move, "M arms move");
                _UnhandledInput(new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.Right, Position = end });
                Check(_armedAction is null, "RMB cancels target mode");
                KeyInput(Key.A); Check(_armedAction == UnitActionKind.AttackMove, "A arms attack-move"); KeyInput(Key.Escape);
                Check(_armedAction is null && _selection.Count == 2, "Escape cancels mode without deselecting");
                KeyInput(Key.A);
                _UnhandledInput(new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.Left, Position = Pixel(new(26 * 1024, 18 * 1024)) });
                _UnhandledInput(new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.Right, ShiftPressed = true, Position = Pixel(new(30 * 1024, 18 * 1024)) });
                _rtsCheck = 1; KeyInput(Key.N); return;
            }
            if (_rtsCheck == 1 && tick >= 1)
            {
                var log = _session!.GetCommandLog();
                Check(log.Length == 2 && log[0].Kind == CommandKind.AttackMove && log[0].Recipients!.Count == 2 &&
                    log[1].Kind == CommandKind.MoveSelected && log[1].Queued, "Input did not produce shared semantic actions");
                Check(_selectionPoses[new(1)].Order.QueuedCount == 1 && _selectionPoses[new(2)].Order.QueuedCount == 1, "Shift queue missing in the simulation");
                _pointer = Pixel(_selectionPoses[new(6)].Current); UpdateRtsCursor(); Check(_rtsOverlay!.Cursor == RtsCursor.Attack, "Enemy attack cursor");
                _pointer = Pixel(new(42 * 1024, 16 * 1024)); UpdateRtsCursor(); Check(_rtsOverlay.Cursor == RtsCursor.AdjustedMove, "Shore correction cursor");
                _pointer = new(-10000, -10000); UpdateRtsCursor(); Check(_rtsOverlay.Cursor == RtsCursor.Unavailable, "Out-of-map action cursor");
                _UnhandledInput(new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.Right, Position = _actionButtons[UnitActionKind.Stop].GetGlobalRect().GetCenter() });
                _actionButtons[UnitActionKind.Stop].EmitSignal(BaseButton.SignalName.Pressed);
                _rtsCheck = 2; KeyInput(Key.N); return;
            }
            if (_rtsCheck == 2 && tick >= 2)
            {
                Check(_selectionPoses[new(1)].Order.QueuedCount == 0 && _selectionPoses[new(1)].Previous == _selectionPoses[new(1)].Current, "UI Stop did not replace the queue");
                Check(_session!.GetCommandLog().Length == 3 && _session.GetCommandLog()[^1].Kind == CommandKind.Stop, "UI button bypassed action dispatch or mouse clicked through HUD");
                Check(_selectionRing!.Multimesh.VisibleInstanceCount == 2, "Multi-selection rings are not batched");
                _verificationDone = true; FinishRtsCapture();
            }
        }
        catch (Exception error) { GD.PushError(error.ToString()); _verificationDone = true; GetTree().Quit(1); }
        void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
        void Click(uint id, bool shift = false, bool ctrl = false, bool doubleClick = false)
        {
            var point = Screen(_selectionPoses[new(id)]);
            _UnhandledInput(new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.Left, Position = point, ShiftPressed = shift, CtrlPressed = ctrl, DoubleClick = doubleClick });
            _UnhandledInput(new InputEventMouseButton { Pressed = false, ButtonIndex = MouseButton.Left, Position = point, ShiftPressed = shift, CtrlPressed = ctrl });
        }
        Vector2 Pixel(WorldPosition p) => _camera!.UnprojectPosition(new(p.X / 1024f, 0, p.Y / 1024f));
        void KeyInput(Key key, bool ctrl = false, bool shift = false) => _UnhandledInput(new InputEventKey { Pressed = true, PhysicalKeycode = key, CtrlPressed = ctrl, ShiftPressed = shift });
    }
    private async void FinishRtsCapture()
    {
        try
        {
            if (_capturePath is not null && DisplayServer.GetName() != "headless")
            {
                _selection.Change(_displayPoses.Take(_displayCount).Where(p => p.Presentation.Owner == 1).Select(p => p.Id), SelectionChange.Replace);
                UpdateSelection(1); if (_label is not null) _label.Text = ShowcaseLabel(2);
                _pointer = _camera!.UnprojectPosition(new(42, 0, 16)); UpdateRtsCursor();
                var selectedPixels = _selection.Snapshot().Select(id => Screen(_selectionPoses[id])).ToArray();
                var a = new Vector2(selectedPixels.Min(p => p.X) - 25, selectedPixels.Min(p => p.Y) - 25);
                var b = new Vector2(selectedPixels.Max(p => p.X) + 25, selectedPixels.Max(p => p.Y) + 25);
                _rtsOverlay!.Marquee = new Rect2(a, b - a).Abs();
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                var path = Path.GetFullPath(_capturePath); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var image = GetViewport().GetTexture().GetImage();
                if (image.SavePng(path) != Error.Ok) throw new IOException("Could not capture RTS controls");
                _rtsOverlay.Marquee = null; _rtsOverlay.Pointer = GetViewport().GetVisibleRect().Size / 2;
                foreach (var cursor in new[] { RtsCursor.Select, RtsCursor.Move, RtsCursor.AdjustedMove, RtsCursor.Attack, RtsCursor.Unavailable })
                    for (int frame = 0; frame < 4; frame++)
                    {
                        _rtsOverlay.Cursor = cursor;
                        await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
                        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                        using var sample = GetViewport().GetTexture().GetImage();
                        if (sample.SavePng(Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + $"-cursor-{cursor}-{frame}.png")) != Error.Ok)
                            throw new IOException("Could not capture cursor animation");
                    }
            }
            GD.Print("OPENRTS_RTS_CONTROLS:PASS click, shift, double-click, box, groups, ownership, target modes, queued action, cursor and UI button");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }
}
