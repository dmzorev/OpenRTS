using System;
using System.Linq;
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
        if (_skirmishCheck == 0)
        {
            CheckSkirmish(frame.Poses.Length == 12 && frame.Players.Length == 3, "Skirmish players/objects not initialized");
            CheckSkirmish(frame.Players.ToArray().Where(p => p.Id != 0).All(p => p.Credits == 5000), "Initial funds wrong");
            _selection.Change([new(1)], SelectionChange.Replace); RefreshSelection(); ActivateCustom("power", false, false);
            _selection.Change([new(4)], SelectionChange.Replace); RefreshSelection();
            CheckSkirmish(Controlled().Single().Value == 4 && CanIssue(UnitActionKind.Move), "Red player cannot be controlled");
            ActivateCustom("capture", false, false);
            var target = frame.Poses.ToArray().Single(p => p.Id.Value == 5);
            _pointer = Screen(target); UpdateRtsCursor();
            CheckSkirmish(_rtsOverlay!.CustomCursor == "capture", "Capture cursor unavailable");
            HandleCustomTarget(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true }, target.Current, target);
            _skirmishCheck = 1; _session!.StepOnce(); return;
        }
        if (frame.Tick < 1) return;
        CheckSkirmish(frame.Players.ToArray().Single(p => p.Id == 1).Credits == 4500 && frame.Players.ToArray().Single(p => p.Id == 2).Credits == 5000, "Production charged the wrong player");
        CheckSkirmish(frame.Poses.ToArray().Single(p => p.Id.Value == 4).Order.Kind == UnitActionKind.Capture, "Capture button did not issue a simulation action");
        _selection.Change([new(2), new(4)], SelectionChange.Replace); RefreshSelection();
        CheckSkirmish(Controlled().Length == 2 && CanIssue(UnitActionKind.Move), "Mixed player control failed");
        CheckSkirmish(!HandleBuildingKey(Key.B), "Skirmish allowed free editor construction");
        GD.Print("OPENRTS_SKIRMISH:players, credits, capture cursor/action and dual-player control passed");
        _verificationDone = true; GetTree().Quit();
    }
}
