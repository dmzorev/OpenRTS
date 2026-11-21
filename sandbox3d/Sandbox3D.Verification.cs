using Godot;
using OpenRTS.Core;
using OpenRTS.Core.Orders;
using OpenRTS.Core.Presentation;
using OpenRTS.Godot.Controls;

public partial class Sandbox3D
{
    private enum CheckStage { Input, Queue, Detour, Group, Stop, Hold, Rocket }
    private static readonly WorldPosition FirstGoal = new(19 * CellSize + 384, 8 * CellSize + 512, TerrainScenario.PlateauElevation);
    private static readonly WorldPosition FinalGoal = new(10 * CellSize + 768, 11 * CellSize + 128, 960);
    private CheckStage _checkStage;
    private long _checkTick;
    private EntityId _scout, _secondScout, _rocketeer, _target;
    private WorldPosition _secondStart;
    private bool _climbed, _descended, _reachedFirstGoal;
    private bool _rocketRan, _rocketAimed;

    private void VerifyControls(FrameExchange.FrameLease frame)
    {
        if (frame.Tick < _checkTick) return;
        var session = _session!;
        if (_checkStage == CheckStage.Input)
        {
            Check(frame.Poses.Length == TerrainScenario.UnitCount && frame.Grid is { HasSlopedSurface: true },
                "Initial frame is missing the test squad or the raised terrain.");
            _scout = frame.Poses[0].Id;
            _secondScout = frame.Poses[1].Id;
            _secondStart = frame.Poses[1].Current;
            VerifyInfantry(frame.Poses[0], "SWAT");
            VerifyInfantry(frame.Poses[^3], "Sniper");
            _rocketeer = frame.Poses[^2].Id;
            _target = frame.Poses[^1].Id;
            VerifyRocketeerClips(frame.Poses[^2]);
            VerifyModularVehicles(frame.Poses[..^1]);
            VerifySelectionAndCamera();
            VerifyReliefPicking();
            Press(Key.M);
            Click(MouseButton.Left, Pixel(FirstGoal));
            Click(MouseButton.Right, Pixel(FinalGoal), shift: true);
            Advance(frame.Tick, CheckStage.Queue);
            return;
        }

        var scout = ActorPose(frame.Poses, _scout);
        var second = ActorPose(frame.Poses, _secondScout);
        switch (_checkStage)
        {
            case CheckStage.Queue:
                var log = session.GetCommandLog();
                Check(log.Length == 2 && log[0].Kind == CommandKind.Move && log[0].Position == FirstGoal &&
                    !log[0].Queued && log[1].Kind == CommandKind.Move && log[1].Position == FinalGoal && log[1].Queued &&
                    log.All(command => command.Entity == _scout), "Pointer orders did not preserve the selected ID, exact targets or queue flag.");
                Check(scout.Order.QueuedCount == 1 && second.Current == _secondStart, "Move affected an unselected unit or lost the queued target.");
                Advance(frame.Tick, CheckStage.Detour);
                break;
            case CheckStage.Detour:
                Check(second.Current == _secondStart, "An unselected scout moved during the detour.");
                Check(scout.Current.Elevation == frame.Grid!.SurfaceElevationAt(scout.Current), "The scout lost contact with the terrain.");
                int heightChange = scout.Current.Elevation - scout.Previous.Elevation;
                Check(Math.Abs(heightChange) <= TerrainScenario.RampRise / 8, "The scout jumped across a height discontinuity.");
                _climbed |= heightChange > 0;
                _descended |= _reachedFirstGoal && heightChange < 0;
                if (!_reachedFirstGoal && scout.Previous.X < TerrainScenario.PlateauStartX * CellSize &&
                    scout.Current.X >= TerrainScenario.PlateauStartX * CellSize)
                    Check(scout.Current.Y >= TerrainScenario.RampStartY * CellSize && scout.Current.Y < TerrainScenario.ReliefEndY * CellSize,
                        "The scout entered the plateau through the cliff instead of the ramp.");
                _reachedFirstGoal |= scout.Current == FirstGoal;
                if (scout.Current == FinalGoal)
                {
                    Check(_climbed && _reachedFirstGoal && _descended, "The route did not climb to the exact plateau point and descend to the ramp point.");
                    Press(Key.Key1);
                    Click(MouseButton.Right, Pixel(new WorldPosition(7 * CellSize, 7 * CellSize)));
                    Advance(frame.Tick, CheckStage.Group);
                    break;
                }
                Check(frame.Tick < 350, "The scout did not reach the second waypoint.");
                Advance(frame.Tick, CheckStage.Detour);
                break;
            case CheckStage.Group:
                var group = session.GetCommandLog()[^1];
                Check(group.Kind == CommandKind.MoveSelected && group.Recipients is { } recipients && recipients.Ids.SequenceEqual([_scout, _secondScout]) &&
                    scout.Order.Destination != second.Order.Destination, "The recalled group did not receive separate destinations.");
                Click(MouseButton.Right, Pixel(new WorldPosition(7 * CellSize, 11 * CellSize)), shift: true);
                Press(Key.S);
                Advance(frame.Tick, CheckStage.Stop);
                break;
            case CheckStage.Stop:
                Check(scout.Order.Phase == OrderPhase.Idle && second.Order.Phase == OrderPhase.Idle &&
                    scout.Order.QueuedCount == 0 && second.Order.QueuedCount == 0, "Stop did not clear the group orders.");
                Press(Key.H);
                Advance(frame.Tick, CheckStage.Hold);
                break;
            case CheckStage.Hold:
                Check(scout.Order.Phase == OrderPhase.Holding && second.Order.Phase == OrderPhase.Holding,
                    "Hold position did not reach both selected units.");
                _camera.Size = _baseCameraSize;
                ChangeSelection([_rocketeer], SelectionChange.Replace);
                CenterOnSelection();
                Click(MouseButton.Right, _camera.UnprojectPosition(_actors[_target].PickCenter));
                Advance(frame.Tick, CheckStage.Rocket);
                break;
            case CheckStage.Rocket:
                Check(session.GetCommandLog()[^1] is { Kind: CommandKind.AttackTarget } attack && attack.TargetEntity == _target,
                    "The target click did not issue an attack for the rocketeer.");
                var actor = _actors[_rocketeer];
                var animation = (AnimationPlayer)actor.Node.FindChild("AnimationPlayer", recursive: true, owned: false);
                _rocketRan |= animation.CurrentAnimation == "RpgRun";
                var weapon = frame.Weapons[ActorPose(frame.Poses, _rocketeer).WeaponOffset];
                _rocketAimed |= weapon.WindupEndTick > frame.Tick && animation.CurrentAnimation == "RpgFire";
                if (frame.Projectiles.IsEmpty)
                {
                    Check(frame.Tick < 340, "The rocketeer did not fire at the test target.");
                    Advance(frame.Tick, CheckStage.Rocket);
                    break;
                }
                Check(_rocketRan && _rocketAimed && weapon.Animation.IsActive(frame.Tick) &&
                    animation.CurrentAnimation == "RpgFire" && _rockets.VisibleInstanceCount == 1,
                    "The rocketeer skipped a movement, windup, firing, or projectile frame.");
                VerifyReplay(frame);
                GD.Print($"Sandbox3D verified: cameras, RTS controls, ramp detour, rocketeer run and attack, and replay parity at tick {frame.Tick}.");
                SetProcess(false);
                GetTree().Quit();
                break;
        }
    }

    private void VerifyRocketeerClips(EntityPose pose)
    {
        var actor = _actors[pose.Id];
        var player = (AnimationPlayer)actor.Node.FindChild("AnimationPlayer", recursive: true, owned: false);
        foreach (var clip in new[] { "RpgIdle", "RpgRun", "RpgFire" })
            Check(player.HasAnimation(clip) && player.GetAnimation(clip).GetTrackCount() > 0,
                $"The rocketeer is missing {clip}.");
        Check(player.CurrentAnimation == "RpgIdle", "The rocketeer did not start in its idle clip.");
    }

    private void VerifyInfantry(EntityPose scout, string name)
    {
        var actor = _actors[scout.Id];
        var player = (AnimationPlayer)actor.Node.FindChild("AnimationPlayer", recursive: true, owned: false);
        foreach (string clip in new[] { "MixIdle", "MixRunLowReady", "MixRunRaised", "MixWalk", "MixAim", "MixFire", "UALJog", "StockRun" })
            Check(player.HasAnimation(clip) && player.GetAnimation(clip).GetTrackCount() > 0,
                $"The {name} model is missing animation {clip}.");
        Check(player.CurrentAnimation == "MixIdle", $"Stationary {name} did not start its idle animation.");
        actor.Apply(scout with { Previous = scout.Current with { X = scout.Current.X - 32 } }, 1, Vector3.Up);
        Check(player.CurrentAnimation == "MixRunLowReady" &&
            player.GetAnimation("MixRunLowReady").LoopMode == Animation.LoopModeEnum.Linear,
            $"Moving {name} did not start the low-ready rifle run loop.");
        actor.Apply(scout, 1, Vector3.Up);
        Check(player.CurrentAnimation == "MixIdle", $"Stopping {name} did not restore idle.");
    }

    private void VerifyModularVehicles(ReadOnlySpan<EntityPose> poses)
    {
        int verified = 0;
        foreach (var vehicle in poses)
        {
            if (!_models[vehicle.UnitType].ModularVehicle) continue;
            var actor = _actors[vehicle.Id];
            var turret = (Node3D)actor.Node.FindChild("TurretSocket", recursive: true, owned: false);
            var animation = (AnimationPlayer)actor.Node.FindChild("AnimationPlayer", recursive: true, owned: false);
            var north = FacingDirection.North;
            var east = FacingDirection.FromQuarterTurns(1);
            if (_models[vehicle.UnitType].Crane)
                Check(Mathf.IsEqualApprox(turret.Rotation.Y, Mathf.Pi), "The idle builder did not park its boom over the rear deck.");
            var aiming = vehicle with { Previous = vehicle.Current with { Y = vehicle.Current.Y + 32 }, WeaponCount = 1,
                Presentation = vehicle.Presentation with { BodyFacing = north, PreviousBodyFacing = north,
                    WeaponFacing = east, PreviousWeaponFacing = east } };
            actor.Apply(aiming, 1, _terrain.NormalAt(vehicle.Current));
            Check(turret.GetChildCount() == 1 && (turret.GlobalBasis * Vector3.Forward).Dot(Vector3.Right) > 0.999f,
                "Vehicle turret did not point east independently of the north-facing hull.");
            Check(animation.CurrentAnimation == "Drive" && animation.GetAnimation("Drive").GetTrackCount() > 0,
                "Moving the vehicle did not start its running gear animation.");
            actor.Apply(vehicle, 1, _terrain.NormalAt(vehicle.Current));
            verified++;
        }
        Check(verified == 3, "The 3D fixture must exercise the medium tank, tracked APC and builder truck.");
    }

    private void VerifySelectionAndCamera()
    {
        Vector2 ScoutPixel(EntityId id) => _camera.UnprojectPosition(_actors[id].PickCenter);
        var first = ScoutPixel(_scout);
        var second = ScoutPixel(_secondScout);
        Click(MouseButton.Left, first);
        Check(_selection.Count == 1 && _selection.Contains(_scout), "Single click missed the scout.");
        Click(MouseButton.Left, second, shift: true);
        Check(_selection.Count == 2 && _selection.Contains(_secondScout), "Shift-click did not add the second scout.");
        Click(MouseButton.Left, second, shift: true);
        Check(_selection.Count == 1 && _selection.Contains(_scout), "Shift-click did not remove the selected scout.");
        Click(MouseButton.Left, second, shift: true);
        Press(Key.Key1, control: true);
        Click(MouseButton.Left, first, doubleClick: true);
        Check(_selection.Count == 3, "Double-click did not select only the visible units of the same type.");
        var from = new Vector2(Math.Max(first.X, second.X) + 8, Math.Max(first.Y, second.Y) + 8);
        var to = new Vector2(Math.Min(first.X, second.X) - 8, Math.Min(first.Y, second.Y) - 8);
        Mouse(MouseButton.Left, from, true);
        Motion(to, to - from, MouseButtonMask.Left);
        Check(_overlay.Marquee is { } box && box.HasPoint(first) && box.HasPoint(second), "Dragging did not show the selection rectangle.");
        Mouse(MouseButton.Left, to, false);
        Check(_overlay.Marquee is null && _selection.Ids.SequenceEqual([_scout, _secondScout]),
            $"Marquee expected {_scout.Value}, {_secondScout.Value}, selected {string.Join(", ", _selection.Snapshot().Select(id => id.Value))}; rectangle {_overlay.Marquee}.");
        Press(Key.Escape);
        Check(_selection.Count == 0, "Escape did not clear the selection.");
        Press(Key.Key1);
        Check(_selection.Ids.SequenceEqual([_scout, _secondScout]), "Control group recall failed.");
        Press(Key.A, control: true);
        Check(_selection.Count == TerrainScenario.UnitCount - 1, "Select all missed a friendly unit or selected the target.");
        Click(MouseButton.Left, first);
        Press(Key.Space);
        var centerPixel = ViewRect.GetCenter();
        var centered = PlanarCamera.GroundPoint(_camera, centerPixel)!.Value;
        Check(centered.DistanceTo(_actors[_scout].Node.Position) < 0.004f, "Center camera did not focus on the selected scout.");
        Motion(centerPixel, new Vector2(-180, 12), MouseButtonMask.Middle);
        Check(PlanarCamera.GroundPoint(_camera, centerPixel)!.Value.X > centered.X + 1, "Middle-button panning did not move the camera.");
        var cursor = centerPixel + new Vector2(80, 40);
        var anchor = PlanarCamera.GroundPoint(_camera, cursor)!.Value;
        float size = _camera.Size;
        Mouse(MouseButton.WheelUp, cursor, true);
        Check(_camera.Size < size && PlanarCamera.GroundPoint(_camera, cursor)!.Value.DistanceTo(anchor) < 0.004f,
            "Zoom moved the point under the cursor.");
    }

    private void VerifyReliefPicking()
    {
        var cliff = new WorldPosition(19 * CellSize + 384, TerrainScenario.ReliefEndY * CellSize, CellSize);
        Check(_terrain.Pick(_camera, Pixel(cliff)) is null, "The ray picked terrain behind a visible cliff face.");
        Click(MouseButton.Right, Pixel(cliff));
        var cursor = Pixel(FirstGoal);
        Check(_terrain.Pick(_camera, cursor) == FirstGoal, "The ray missed the exact raised target before zoom.");
        Mouse(MouseButton.WheelUp, cursor, true);
        Check(_terrain.Pick(_camera, cursor) == FirstGoal, "Zoom lost the raised point beneath the cursor.");
        Check(_terrain.Pick(_camera, Pixel(FinalGoal)) == FinalGoal, "The ramp ray did not recover the sub-cell target and its height.");
    }

    private void VerifyReplay(FrameExchange.FrameLease frame)
    {
        using var world = _scenario.CreateWorld();
        var commands = _session!.GetCommandLog();
        int start = 0;
        for (long tick = 1; tick <= frame.Tick; tick++)
        {
            int end = start;
            while (end < commands.Length && commands[end].Tick == tick) end++;
            world.Step(commands.AsSpan(start, end - start));
            start = end;
        }
        var poses = new EntityPose[world.Count];
        world.CopyPoses(poses);
        Check(start == commands.Length && frame.Poses.SequenceEqual(poses), "The same journal produced different poses in the independent simulation.");
    }

    private void Advance(long tick, CheckStage stage)
    {
        _checkStage = stage;
        _checkTick = tick + 1;
        _session!.StepOnce();
    }

    private Vector2 Pixel(WorldPosition point) => _camera.UnprojectPosition(Display(point));

    private static EntityPose ActorPose(ReadOnlySpan<EntityPose> poses, EntityId id)
    {
        foreach (var pose in poses)
            if (pose.Id == id) return pose;
        throw new InvalidOperationException("Verified actor disappeared.");
    }

    private void Click(MouseButton button, Vector2 pixel, bool shift = false, bool doubleClick = false)
    {
        Mouse(button, pixel, true, shift, doubleClick);
        Mouse(button, pixel, false, shift);
    }

    private void Mouse(MouseButton button, Vector2 pixel, bool down, bool shift = false, bool doubleClick = false)
    {
        using var input = new InputEventMouseButton
        { ButtonIndex = button, Position = pixel, Pressed = down, ShiftPressed = shift, DoubleClick = doubleClick };
        GetViewport().PushInput(input, inLocalCoords: true);
    }

    private void Motion(Vector2 pixel, Vector2 relative, MouseButtonMask buttons)
    {
        using var input = new InputEventMouseMotion { Position = pixel, Relative = relative, ButtonMask = buttons };
        GetViewport().PushInput(input, inLocalCoords: true);
    }

    private void Press(Key key, bool control = false)
    {
        using var input = new InputEventKey { PhysicalKeycode = key, Pressed = true, CtrlPressed = control };
        GetViewport().PushInput(input, inLocalCoords: true);
    }

    private static void Check(bool success, string error)
    {
        if (!success) throw new InvalidOperationException(error);
    }
}
