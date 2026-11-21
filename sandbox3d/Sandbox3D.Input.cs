using Godot;
using OpenRTS.Core;
using OpenRTS.Core.Orders;
using OpenRTS.Godot.Controls;

public partial class Sandbox3D
{
    private const float DragThresholdSquared = 36;
    private Vector2? _dragStart;
    private bool _dragging, _dragAdd, _moveArmed;

    public override void _UnhandledInput(InputEvent input)
    {
        if (_session is null || _failed) return;
        try
        {
            switch (input)
            {
                case InputEventMouseMotion motion:
                    if ((motion.ButtonMask & MouseButtonMask.Middle) != 0)
                        PlanarCamera.Drag(_camera, motion.Position, motion.Relative, ViewRect.GetCenter(), MapSize,
                            PickElevation(motion.Position - motion.Relative));
                    else
                        UpdateDrag(motion.Position);
                    break;
                case InputEventMouseButton button when HandleMouse(button):
                case InputEventKey { Pressed: true, Echo: false } key when HandleKey(key):
                    GetViewport().SetInputAsHandled();
                    break;
            }
        }
        catch (Exception error) { Fail(error); }
    }

    private bool HandleMouse(InputEventMouseButton button)
    {
        var pixel = button.Position;
        if (button.Pressed && button.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            float factor = button.ButtonIndex == MouseButton.WheelUp ? 0.8f : 1.25f;
            PlanarCamera.ZoomAt(_camera, pixel, factor, _baseCameraSize, ViewRect.GetCenter(), MapSize, PickElevation(pixel));
            return true;
        }
        if (button.ButtonIndex == MouseButton.Right && button.Pressed)
        {
            CancelDrag();
            var target = _moveArmed ? default : Pick(pixel, hostile: true);
            if (_selection.Count > 0 && target != default)
            {
                _session!.Issue(new UnitAction(UnitActionKind.Attack, new EntitySet(_selection.Snapshot()),
                    Target: target, Queued: button.ShiftPressed));
                _goal.Visible = false;
            }
            else if (IssueMove(pixel, button.ShiftPressed))
            {
                _moveArmed = false;
                UpdateStatus();
            }
            return true;
        }
        if (button.ButtonIndex != MouseButton.Left) return false;
        if (button.Pressed && _moveArmed)
        {
            if (IssueMove(pixel, button.ShiftPressed) && !button.ShiftPressed) _moveArmed = false;
            UpdateStatus();
            return true;
        }

        if (button.Pressed)
        {
            var picked = Pick(pixel);
            if ((button.DoubleClick || button.CtrlPressed || button.MetaPressed) && _actors.TryGetValue(picked, out var same))
            {
                ChangeSelection(ActorsIn(ViewRect).Where(id => _actors[id].Type == same.Type),
                    button.ShiftPressed ? SelectionChange.Add : SelectionChange.Replace);
                CancelDrag();
            }
            else
            {
                _dragStart = pixel;
                _dragAdd = button.ShiftPressed;
            }
        }
        else if (_dragStart.HasValue)
        {
            UpdateDrag(pixel);
            if (_dragging)
                ChangeSelection(ActorsIn(_overlay.Marquee!.Value), _dragAdd ? SelectionChange.Add : SelectionChange.Replace);
            else
            {
                var picked = Pick(pixel);
                ChangeSelection([picked], _dragAdd ? SelectionChange.Toggle : SelectionChange.Replace);
            }
            CancelDrag();
        }
        return true;
    }

    private void UpdateDrag(Vector2 pixel)
    {
        if (_dragStart is not { } start) return;
        _dragging |= start.DistanceSquaredTo(pixel) >= DragThresholdSquared;
        if (_dragging) _overlay.Marquee = new Rect2(start, pixel - start).Abs().Intersection(ViewRect);
    }

    private void CancelDrag()
    {
        _dragStart = null;
        _dragging = false;
        if (_overlay is not null) _overlay.Marquee = null;
    }

    private EntityId Pick(Vector2 pixel, bool hostile = false)
    {
        EntityId picked = default;
        float best = float.PositiveInfinity;
        if (!ViewRect.HasPoint(pixel)) return picked;
        foreach (var (id, actor) in _actors)
        {
            if ((actor.Owner != 1) != hostile) continue;
            if (_camera.IsPositionBehind(actor.PickCenter)) continue;
            var screen = _camera.UnprojectPosition(actor.PickCenter);
            float distance = screen.DistanceSquaredTo(pixel);
            float radius = actor.PickRadius(_camera, screen);
            if (distance > radius * radius || distance > best || (distance == best && picked.Value < id.Value)) continue;
            picked = id;
            best = distance;
        }
        return picked;
    }

    private IEnumerable<EntityId> ActorsIn(Rect2 box) =>
        _actors.Where(pair => pair.Value.Owner == 1 && !_camera.IsPositionBehind(pair.Value.PickCenter) &&
            box.HasPoint(_camera.UnprojectPosition(pair.Value.PickCenter))).Select(pair => pair.Key);

    private void ChangeSelection(IEnumerable<EntityId> ids, SelectionChange change)
    {
        _selection.Change(ids, change);
        _moveArmed = false;
        UpdateStatus();
    }

    private bool HandleKey(InputEventKey key)
    {
        var code = key.PhysicalKeycode == Key.None ? key.Keycode : key.PhysicalKeycode;
        bool control = key.CtrlPressed || key.MetaPressed;
        if (code is >= Key.Key1 and <= Key.Key9)
        {
            int group = (int)(code - Key.Key0);
            if (control) _selection.StoreGroup(group);
            else
            {
                _selection.RecallGroup(group, key.ShiftPressed, _actors.ContainsKey);
                _moveArmed = false;
                UpdateStatus();
            }
            return true;
        }
        switch (code)
        {
            case Key.A when control:
                ChangeSelection(_actors.Where(pair => pair.Value.Owner == 1).Select(pair => pair.Key), SelectionChange.Replace);
                break;
            case Key.Escape:
                if (_moveArmed || _dragStart.HasValue)
                {
                    _moveArmed = false;
                    CancelDrag();
                    UpdateStatus();
                }
                else ChangeSelection([], SelectionChange.Replace);
                break;
            case Key.Space:
                CenterOnSelection();
                break;
            case Key.M:
                _moveArmed = _selection.Count > 0;
                UpdateStatus();
                break;
            case Key.S:
            case Key.H:
                if (_selection.Count > 0)
                    _session!.Issue(new UnitAction(code == Key.S ? UnitActionKind.Stop : UnitActionKind.HoldPosition,
                        new EntitySet(_selection.Snapshot())));
                _goal.Visible = false;
                _moveArmed = false;
                UpdateStatus();
                break;
            default:
                return false;
        }
        return true;
    }

    private bool IssueMove(Vector2 pixel, bool queued)
    {
        if (_selection.Count == 0 || !ViewRect.HasPoint(pixel) ||
            _terrain.Pick(_camera, pixel) is not { } destination)
            return false;

        _session!.Issue(new UnitAction(UnitActionKind.Move, new EntitySet(_selection.Snapshot()), destination, Queued: queued));
        _goal.Position = Display(destination) + Vector3.Up * 0.05f;
        _goal.Quaternion = new Quaternion(Vector3.Up, _terrain.NormalAt(destination));
        _goal.Scale = new(0.35f, 0.4f, 0.35f);
        _goal.Visible = true;
        _goalTime = 1.5f;
        return true;
    }

    private float PickElevation(Vector2 pixel) => (_terrain.Pick(_camera, pixel)?.Elevation ?? 0) / (float)CellSize;

    private void CenterOnSelection()
    {
        var ids = _selection.Ids;
        if (ids.IsEmpty) return;
        Vector3 sum = Vector3.Zero;
        foreach (var id in ids) sum += _actors[id].Node.Position;
        var focus = sum / ids.Length;
        if (PlanarCamera.GroundPoint(_camera, ViewRect.GetCenter(), focus.Y) is { } center)
            PlanarCamera.Pan(_camera, focus - center, ViewRect.GetCenter(), MapSize);
    }

    private void PanCamera(double delta)
    {
        if (!GetWindow().HasFocus()) return;
        var direction = new Vector2(
            (Input.IsPhysicalKeyPressed(Key.Right) ? 1 : 0) - (Input.IsPhysicalKeyPressed(Key.Left) ? 1 : 0),
            (Input.IsPhysicalKeyPressed(Key.Down) ? 1 : 0) - (Input.IsPhysicalKeyPressed(Key.Up) ? 1 : 0));
        if (direction == Vector2.Zero && !_dragStart.HasValue && !Input.IsMouseButtonPressed(MouseButton.Middle))
        {
            const int edge = 12;
            var view = ViewRect;
            var mouse = GetViewport().GetMousePosition();
            if (view.HasPoint(mouse))
                direction = new(
                    mouse.X < view.Position.X + edge ? -1 : mouse.X >= view.End.X - edge ? 1 : 0,
                    mouse.Y < view.Position.Y + edge ? -1 : mouse.Y >= view.End.Y - edge ? 1 : 0);
        }
        if (direction == Vector2.Zero) return;
        var heading = new Basis(Vector3.Up, _camera.Rotation.Y);
        var offset = heading * new Vector3(direction.X, 0, direction.Y).Normalized() * _camera.Size * (float)delta * 0.7f;
        PlanarCamera.Pan(_camera, offset, ViewRect.GetCenter(), MapSize);
    }
}
