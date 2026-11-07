using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OpenRTS.Core;
using OpenRTS.Core.Orders;
using OpenRTS.Godot.Controls;

public partial class Sandbox
{
    private UnitActionKind? _armedAction;
    private bool _queueArmed, _pointerDown, _dragAdd;
    private Vector2 _pointer, _dragStart;
    private RtsOverlay? _rtsOverlay;
    private readonly Dictionary<EntityId, EntityPose> _selectionPoses = new();
    private readonly Dictionary<UnitActionKind, Button> _actionButtons = new();
    private readonly Dictionary<Locomotor, DestinationResolver> _cursorResolvers = new();
    private NavigationGrid? _cursorGrid;
    private float[] _ringBuffer = [];
    private long _availabilityRevision = -1;
    private bool _canMove, _canAttack, _canAttackMove, _canRepair, _canControl;
    private readonly HashSet<(Locomotor Profile, int Component)> _cursorProfiles = [];
    private readonly List<Control> _rtsPanels = [];

    private void CreateSelectionRing()
    {
        _selectionRing = new() { Name = "Selection", Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true, Mesh = new PlaneMesh { Size = new(2, 2), Material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/selection.gdshader") } } } };
        AddChild(_selectionRing);
        var layer = new CanvasLayer { Layer = 20 }; AddChild(layer);
        _rtsOverlay = new(); layer.AddChild(_rtsOverlay);
        _rtsOverlay.RegisterCursor("ability", new Color(1, 0.7f, 0.2f));
        _rtsOverlay.RegisterCursor("grenade", new Color(1, 0.55f, 0.12f), 18);
        _rtsOverlay.RegisterCursor("build", new Color(0.35f, 0.95f, 1), 20);
        _rtsOverlay.RegisterCursor("capture", new Color(0.95f, 0.85f, 0.2f), 20);
        _rtsOverlay.RegisterCursor("repair", new Color(0.2f, 1, 0.65f));
        if (DisplayServer.GetName() != "headless") Input.MouseMode = Input.MouseModeEnum.Hidden;
    }
    private void RefreshSelection()
    {
        _selectionPoses.Clear(); _selectionPoses.EnsureCapacity(_displayCount);
        for (int i = 0; i < _displayCount; i++) _selectionPoses.Add(_displayPoses[i].Id, _displayPoses[i]);
        _selection.Prune(_selectionPoses.ContainsKey);
        _availabilityRevision = -1;
        RefreshCommandSelection();
        foreach (var (kind, button) in _actionButtons)
        { button.Disabled = !CanIssue(kind); button.Modulate = _armedAction == kind ? ActiveButtonColor : Colors.White; }
    }
    private EntityPose? Selected() => _selectionPoses.TryGetValue(_activeRecipients.Length > 0 ? _activeRecipients[0] : _selection.Primary, out var pose) ? pose : null;
    private EntityPose? Pick(WorldPosition point)
    {
        EntityPose? selected = null; long best = 1100L * 1100; bool selectedFootprint = false;
        for (int i = 0; i < _displayCount; i++)
        {
            var pose = _displayPoses[i]; var type = _catalog!.Units[pose.UnitType]; bool footprint = type.Footprint is not null;
            if (footprint)
            {
                bool inside = type.Footprint!.Cells.Any(c => { var b = type.Footprint.Bounds(c, pose.Current); return point.X >= b.MinX && point.X < b.MaxX && point.Y >= b.MinY && point.Y < b.MaxY; });
                if (!inside || selected is not null && !selectedFootprint || selectedFootprint && selected!.Value.Id.Value > pose.Id.Value) continue;
                selected = pose; selectedFootprint = true; continue;
            }
            if (!ObjectGeometry.WithinRadius(type, pose.Current, point, 600)) continue;
            long x = (long)pose.Current.X - point.X, y = (long)pose.Current.Y - point.Y, distance = x * x + y * y;
            if (selectedFootprint || distance < best || distance == best && selected is { } old && pose.Id.Value < old.Id.Value)
            { best = distance; selected = pose; selectedFootprint = false; }
        }
        return selected;
    }
    private Vector2 Screen(EntityPose pose) => _camera!.UnprojectPosition(new(pose.Current.X / 1024f, 0, pose.Current.Y / 1024f));
    private bool OnScreen(EntityPose pose) => Playfield().HasPoint(Screen(pose));
    private bool Controls(uint owner) => owner == 1 || _mapMatch is not null && owner is >= 2 and <= MapScenario.MaximumPlayers;
    private uint CommandOwner => Selected()?.Presentation.Owner ?? 1;
    private bool Ally(EntityPose pose) => pose.Presentation.Owner != 0 && pose.Presentation.Owner == CommandOwner;
    private bool Enemy(EntityPose pose) => pose.Presentation.Owner != 0 && pose.Presentation.Owner != CommandOwner && _catalog!.Units[pose.UnitType].Destruction != DestructionPolicy.Invulnerable;
    private bool CapturableTarget(EntityPose pose) => pose.Presentation.Owner != CommandOwner && _catalog!.Units[pose.UnitType].Capturable is not null;
    private bool AbilityTarget(AbilityDefinition ability, UnitDefinition type, EntityPose target)
    {
        if (ability.Kind == AbilityKind.Capture) return CapturableTarget(target);
        var weapon = type.Weapons.First(m => m.Id == ability.Mount).Weapon;
        return weapon.Effect == WeaponEffect.Repair ? Ally(target) && target.Health < _catalog!.Units[target.UnitType].MaxHealth && weapon.Modifier(_catalog.Units[target.UnitType].Armor) > 0 : Enemy(target);
    }
    private EntityId[] Controlled() => _selection.Snapshot().Where(id => _selectionPoses.TryGetValue(id, out var pose) && Controls(pose.Presentation.Owner)).ToArray();
    private bool HasAttackMount(EntityPose pose, UnitDefinition type) =>
        type.Weapons.Any(m => m.Weapon.Effect == WeaponEffect.Damage) ||
        type.Transport is { FireFromInside: true } && _loadedTransports.Contains(pose.Id) &&
        _storedPoses.Any(cargo => cargo.Carrier == pose.Id && _catalog!.Units[cargo.UnitType].Weapons.Any(m => m.FireFromTransport && m.Weapon.Effect == WeaponEffect.Damage));

    private bool CanIssue(UnitActionKind kind)
    {
        if (_catalog is null) return false;
        if (_availabilityRevision != _selection.Revision)
        {
            _canMove = _canAttack = _canAttackMove = _canRepair = false; _canControl = _selection.Count > 0;
            foreach (var id in _selection.Ids)
            {
                if (!_selectionPoses.TryGetValue(id, out var pose) || !Controls(pose.Presentation.Owner)) { _canControl = false; break; }
                if (pose.Construction.Active) continue;
                var type = _catalog.Units[pose.UnitType];
                _canMove |= type.Movable || type.TaskQueues.Count > 0;
                bool damage = HasAttackMount(pose, type);
                _canAttack |= damage; _canAttackMove |= damage && type.Movable;
                _canRepair |= type.Weapons.Any(m => m.Weapon.Effect == WeaponEffect.Repair);
            }
            _availabilityRevision = _selection.Revision;
        }
        if (kind == UnitActionKind.Enter) return Controlled().Any(id => _catalog.Units[_selectionPoses[id].UnitType].Passenger is not null);
        if (kind is UnitActionKind.Unload or UnitActionKind.UnloadAt) return SelectedTransports().Length > 0;
        return _canControl && kind switch { UnitActionKind.Attack => _canAttack, UnitActionKind.AttackMove => _canAttackMove, UnitActionKind.Repair => _canRepair, UnitActionKind.Move => _canMove, _ => true };
    }
    public bool ActivateAction(UnitActionKind kind, bool queued = false)
    {
        _armedCustom = null; _panelDirty = true; _placing = false; _productionPlacement = null; _sitePlacement = null;
        if (!CanIssue(kind)) { _notice = "Действие недоступно текущему выбору."; return false; }
        if (kind is UnitActionKind.Move or UnitActionKind.AttackMove or UnitActionKind.Attack or UnitActionKind.Repair or UnitActionKind.Enter or UnitActionKind.UnloadAt)
        { _armedAction = kind; _queueArmed = queued; _pointerDown = false; if (_rtsOverlay is not null) _rtsOverlay.Marquee = null; }
        else IssueAction(kind, default, default, queued);
        UpdateRtsCursor(); return true;
    }
    private bool IssueAction(UnitActionKind kind, WorldPosition point, EntityId target, bool queued)
    {
        if (_session is null) return false;
        if (kind == UnitActionKind.Enter)
        {
            if (!_selectionPoses.TryGetValue(target, out var carrier)) return false;
            var boarding = Controlled().Where(id => CanBoard(id, carrier)).ToArray();
            if (boarding.Length == 0) return false;
            _session.Issue(new(kind, new EntitySet(boarding), Target: target, Queued: queued));
            return true;
        }
        if (kind is UnitActionKind.Unload or UnitActionKind.UnloadAt)
        {
            var carriers = SelectedTransports();
            if (carriers.Length == 0) return false;
            _session.Issue(new(kind, new EntitySet(carriers), point, Queued: queued));
            return true;
        }
        if (!CanIssue(kind)) { _notice = "Нельзя отдать этот приказ выбранным юнитам."; return false; }
        var ids = Controlled().Where(id =>
        {
            var type = _catalog!.Units[_selectionPoses[id].UnitType];
            return kind switch {
                UnitActionKind.Move => type.Movable || type.TaskQueues.Count > 0,
                UnitActionKind.AttackMove => type.Movable && HasAttackMount(_selectionPoses[id], type),
                UnitActionKind.Attack => HasAttackMount(_selectionPoses[id], type),
                UnitActionKind.Repair => type.Weapons.Any(m => m.Weapon.Effect == WeaponEffect.Repair), _ => true };
        });
        if (kind == UnitActionKind.Attack && (!_selectionPoses.TryGetValue(target, out var attacked) || !Enemy(attacked)))
        { _notice = "Атака: выберите вражеский юнит."; return false; }
        if (kind == UnitActionKind.Repair && (!_selectionPoses.TryGetValue(target, out var repaired) || !Ally(repaired) || repaired.Health >= _catalog!.Units[repaired.UnitType].MaxHealth || _catalog.Units[repaired.UnitType].Armor == ArmorKind.Living))
        { _notice = "Ремонт: выберите повреждённую союзную технику или здание."; return false; }
        var recipients = ids.Where(id => target.Value == 0 || !_selectionPoses.TryGetValue(target, out var victim) ||
            (kind == UnitActionKind.Repair ? _selectionPoses[id].Presentation.Owner == victim.Presentation.Owner : kind == UnitActionKind.Attack ? _selectionPoses[id].Presentation.Owner != victim.Presentation.Owner : true)).ToArray();
        if (recipients.Length == 0) return false;
        _session.Issue(new(kind, new EntitySet(recipients), point, target, queued)); _notice = ""; return true;
    }
    private WorldPosition? CommandPoint(Vector2 pixel)
    {
        if (_terminal is not null && (!Playfield().HasPoint(pixel) || OverRtsPanel(pixel))) return null;
        if (GroundPoint(pixel) is not { } ground || ground.X < 0 || ground.Z < 0 || ground.X > _mapWidth || ground.Z > _mapDepth) return null;
        return MousePoint(pixel);
    }
    private void HandleShowcaseClick(InputEventMouseButton click)
    {
        _pointer = click.Position;
        if (OverRtsPanel(click.Position)) return;
        if (!click.Pressed)
        { if (click.ButtonIndex == MouseButton.Left && _pointerDown) FinishSelection(click.Position); return; }
        if (_sitePlacement is not null && click.ButtonIndex == MouseButton.Right) { _sitePlacement = null; _panelDirty = true; return; }
        if (_buildings && _placing && click.ButtonIndex == MouseButton.Right) { _placing = false; return; }
        if (click.ButtonIndex == MouseButton.Right && (_armedAction is not null || _armedCustom is not null || _pointerDown))
        { _armedAction = null; _armedCustom = null; _panelDirty = true; _pointerDown = false; if (_rtsOverlay is not null) _rtsOverlay.Marquee = null; UpdateRtsCursor(); return; }
        if (CommandPoint(click.Position) is not { } point) { _notice = "Точка вне карты."; return; }
        if (HandlePlacement(click, point)) return;
        var picked = Pick(point);
        if (HandleConstructionClick(click, point, picked)) return;
        if (HandleCustomTarget(click, point, picked)) return;
        if (click.ButtonIndex == MouseButton.Left && _armedAction is { } armed)
        {
            var kind = armed == UnitActionKind.AttackMove && picked is { } armedEnemy && Enemy(armedEnemy) ? UnitActionKind.Attack : armed;
            if (IssueAction(kind, point, kind is UnitActionKind.Attack or UnitActionKind.Repair or UnitActionKind.Enter ? picked?.Id ?? default : default, click.ShiftPressed || _queueArmed) && !click.ShiftPressed)
                _armedAction = null;
            UpdateRtsCursor(); return;
        }
        if (HandleTransportClick(click, picked)) return;
        if (click.ButtonIndex == MouseButton.Left)
        {
            if ((click.DoubleClick || click.CtrlPressed) && picked is { } same)
            {
                var ids = _displayPoses.Take(_displayCount).Where(p => p.UnitType == same.UnitType && p.Presentation.Owner == same.Presentation.Owner && OnScreen(p)).Select(p => p.Id);
                if (!click.ShiftPressed || Controls(same.Presentation.Owner)) _selection.Change(ids, click.ShiftPressed && CanIssue(UnitActionKind.Stop) ? SelectionChange.Add : SelectionChange.Replace);
                _pointerDown = false; RefreshSelection(); return;
            }
            _pointerDown = true; _dragStart = click.Position; _dragAdd = click.ShiftPressed;
            return;
        }
        if (!CanIssue(UnitActionKind.Stop)) { _notice = "Выберите свои юниты."; return; }
        if (click.AltPressed && Selected() is { } unit)
        { _session!.Submit(CommandKind.Aim, point, unit.Id, targetEntity: picked?.Id ?? default); return; }
        if (picked is { } capturable && CapturableTarget(capturable) && ContextCapture(capturable)) return;
        bool enemy = picked is { } enemyPose && Enemy(enemyPose);
        bool repair = !enemy && picked is { } ally && Ally(ally) && ally.Health < _catalog!.Units[ally.UnitType].MaxHealth && _catalog.Units[ally.UnitType].Armor != ArmorKind.Living && CanIssue(UnitActionKind.Repair) && !click.CtrlPressed;
        if (repair && ContextRepair(picked!.Value)) return;
        IssueAction(repair ? UnitActionKind.Repair : enemy && !click.CtrlPressed ? UnitActionKind.Attack : UnitActionKind.Move, point, picked?.Id ?? default, click.ShiftPressed);
    }
    public override void _Input(InputEvent input)
    {
        if (!_showcase) return;
        if (input is InputEventMouseMotion pointer) _pointer = pointer.Position;
        else if (input is InputEventMouseButton button) _pointer = button.Position;
        if (_rtsOverlay is not null) _rtsOverlay.Pointer = _pointer;
        if (!_pointerDown) return;
        if (input is InputEventMouseMotion move)
        {
            _pointer = move.Position;
            if (_rtsOverlay is not null && _pointer.DistanceTo(_dragStart) >= 6) _rtsOverlay.Marquee = new Rect2(_dragStart, _pointer - _dragStart).Abs();
        }
        else if (input is InputEventMouseButton { Pressed: false, ButtonIndex: MouseButton.Left } release)
        { FinishSelection(release.Position); GetViewport().SetInputAsHandled(); }
    }
    private void FinishSelection(Vector2 end)
    {
        _pointerDown = false; _pointer = end; if (_rtsOverlay is not null) _rtsOverlay.Marquee = null;
        _selectedProject = default; RefreshProjectButton();
        if (_dragStart.DistanceTo(end) >= 6)
        {
            var box = new Rect2(_dragStart, end - _dragStart).Abs();
            var ids = _displayPoses.Take(_displayCount).Where(p => Controls(p.Presentation.Owner) && OnScreen(p) && box.HasPoint(Screen(p))).Select(p => p.Id);
            _selection.Change(ids, _dragAdd && CanIssue(UnitActionKind.Stop) ? SelectionChange.Add : SelectionChange.Replace);
        }
        else if (CommandPoint(end) is { } point)
        {
            var picked = Pick(point);
            if (!_dragAdd && PickProject(point) is { } project && (picked is null || _catalog!.Units[picked.Value.UnitType].Footprint is not null))
            {
                SelectProject(project);
                UpdateSelection(1);
                UpdateRtsCursor();
                return;
            }
            if (!_dragAdd || picked is { } controlled && Controls(controlled.Presentation.Owner))
                _selection.Change(picked is { } pose ? [pose.Id] : [], _dragAdd && CanIssue(UnitActionKind.Stop) ? SelectionChange.Toggle : SelectionChange.Replace);
        }
        RefreshSelection(); UpdateSelection(1); UpdateRtsCursor();
    }
    private bool HasTargetingMode() => _sitePlacement is not null || _placing || _armedAction is not null || _armedCustom is not null || _pointerDown;

    private bool HandleRtsKey(InputEventKey key, Key code)
    {
        if (code is Key.Delete or Key.Backspace && _selectedProject.Value != 0) { CancelProject(); return true; }
        if (code == Key.Escape && _sitePlacement is not null) { _sitePlacement = null; _panelDirty = true; return true; }
        if (code == Key.Escape && _placing) { _placing = false; _productionPlacement = null; _panelDirty = true; return true; }
        if (code == Key.Escape && _selectedProject.Value != 0) { _selectedProject = default; RefreshProjectButton(); return true; }
        if (HandleCustomKey(key, code)) return true;
        if (code == Key.Tab) return CycleCommandType(key.ShiftPressed);
        if (HandleBuildingKey(code)) return true;
        if (code == Key.Escape)
        { if (_armedAction is not null || _armedCustom is not null || _pointerDown) { _armedAction = null; _armedCustom = null; _panelDirty = true; _pointerDown = false; } else _selection.Change([], SelectionChange.Replace);
            if (_rtsOverlay is not null) _rtsOverlay.Marquee = null; RefreshSelection(); UpdateRtsCursor(); return true; }
        if (code >= Key.Key1 && code <= Key.Key9)
        {
            int group = (int)(code - Key.Key0);
            if (key.CtrlPressed) { if (CanIssue(UnitActionKind.Stop)) _selection.StoreGroup(group); else _notice = "Контрольные группы доступны для своих юнитов."; }
            else _selection.RecallGroup(group, key.ShiftPressed && CanIssue(UnitActionKind.Stop), _selectionPoses.ContainsKey);
            RefreshSelection(); return true;
        }
        if (code == Key.A && key.CtrlPressed)
        { _selection.Change(_displayPoses.Take(_displayCount).Where(p => Controls(p.Presentation.Owner)).Select(p => p.Id), SelectionChange.Replace); RefreshSelection(); return true; }
        if (code == Key.Space)
        {
            var poses = _selection.Snapshot().Where(_selectionPoses.ContainsKey).Select(id => _selectionPoses[id]).ToArray();
            if (poses.Length > 0)
                CenterCamera(new((float)poses.Average(p => p.Current.X) / 1024, (float)poses.Average(p => p.Current.Y) / 1024));
            return true;
        }
        var action = code switch { Key.A => CanIssue(UnitActionKind.AttackMove) ? UnitActionKind.AttackMove : UnitActionKind.Attack, Key.M => UnitActionKind.Move, Key.Enter => UnitActionKind.Enter, Key.S => UnitActionKind.Stop, Key.H => UnitActionKind.HoldPosition, _ => (UnitActionKind?)null };
        return action is { } kind && ActivateAction(kind, key.ShiftPressed);
    }
    private void UpdateRtsCursor()
    {
        if (_rtsOverlay is null) return;
        _rtsOverlay.Pointer = _pointer;
        var cursor = RtsCursor.Default;
        if (CommandPoint(_pointer) is { } point)
        {
            var hovered = Pick(point);
            if (_armedAction is not null && !CanIssue(_armedAction.Value)) cursor = RtsCursor.Unavailable;
            else if (_armedAction == UnitActionKind.UnloadAt) cursor = RtsCursor.Unload;
            else if (_armedAction == UnitActionKind.Enter)
                cursor = hovered is { } entry && CanBoardSelected(entry) ? RtsCursor.Enter : RtsCursor.Unavailable;
            else if (_armedAction is null && _armedCustom is null && !Input.IsPhysicalKeyPressed(Key.Ctrl) && !Input.IsPhysicalKeyPressed(Key.Alt) && hovered is { } host && CanUnload(host))
                cursor = RtsCursor.Unload;
            else if (_armedAction is null && _armedCustom is null && !Input.IsPhysicalKeyPressed(Key.Ctrl) && !Input.IsPhysicalKeyPressed(Key.Alt) && hovered is { } transport && CanBoardSelected(transport))
                cursor = RtsCursor.Enter;
            else if (_armedAction == UnitActionKind.Repair || _armedAction is null && CanIssue(UnitActionKind.Repair) && hovered is { } candidate && Ally(candidate) && candidate.Health < _catalog!.Units[candidate.UnitType].MaxHealth && _catalog.Units[candidate.UnitType].Armor != ArmorKind.Living)
            { cursor = hovered is { } ally && Ally(ally) && ally.Health < _catalog!.Units[ally.UnitType].MaxHealth && _catalog.Units[ally.UnitType].Armor != ArmorKind.Living ? RtsCursor.Repair : RtsCursor.Unavailable; }
            else if (CanIssue(UnitActionKind.Stop))
            {
                bool attack = _armedAction is UnitActionKind.AttackMove or UnitActionKind.Attack || _armedAction is null && hovered is { } enemyHover && Enemy(enemyHover) && !Input.IsPhysicalKeyPressed(Key.Ctrl);
                if (attack) cursor = CanIssue(UnitActionKind.Attack) && (_armedAction != UnitActionKind.Attack || hovered is { } attackHover && Enemy(attackHover)) ? RtsCursor.Attack : RtsCursor.Unavailable;
                else if (_armedAction is null && hovered is { } allyHover && Ally(allyHover)) cursor = RtsCursor.Select;
                else cursor = CanIssue(UnitActionKind.Move) ? RtsCursor.Move : RtsCursor.Unavailable;
                if (cursor is RtsCursor.Move or RtsCursor.Attack && _displayGrid is { } grid && Selected() is { } primary)
                {
                    if (!ReferenceEquals(grid, _cursorGrid)) { _cursorResolvers.Clear(); _cursorGrid = grid; }
                    if (hovered is null || cursor == RtsCursor.Move)
                    {
                        _cursorProfiles.Clear();
                        foreach (var id in _selection.Ids)
                        {
                            var pose = _selectionPoses[id]; var type = _catalog!.Units[pose.UnitType];
                            if (!type.Movable) continue;
                            var profile = type.Locomotor;
                            if (!_cursorResolvers.TryGetValue(profile, out var resolver)) { resolver = new(grid, profile, grid.HasGeometry || grid.CellSize < 514 || profile.Body != BodyShape.Default || profile.MaximumStep != int.MaxValue || profile.MaximumSlope != int.MaxValue || profile.MaximumDepth != int.MaxValue); _cursorResolvers.Add(profile, resolver); }
                            if (!_cursorProfiles.Add((profile, resolver.ComponentAt(pose.Current)))) continue;
                            if (!resolver.HasStoppingTerrain(pose.Current)) { cursor = RtsCursor.Unavailable; break; }
                            if (!resolver.CanStop(point) || !resolver.CanReach(pose.Current, point)) cursor = RtsCursor.AdjustedMove;
                        }
                    }
                }
            }
            else if (hovered is { } pose) cursor = Controls(pose.Presentation.Owner) ? RtsCursor.Select : RtsCursor.Inspect;
        }
        else if (_armedAction is not null || CanIssue(UnitActionKind.Move)) cursor = RtsCursor.Unavailable;
        _rtsOverlay.CustomCursor = null;
        if (_armedCustom is { } custom && _activeType >= 0)
        {
            var binding = _catalog!.Presentations[_activeType].Actions!.First(a => a.Id == custom);
            var ability = _catalog.Units[_activeType].Abilities.First(a => a.Id == custom);
            var hovered = CommandPoint(_pointer) is { } p ? Pick(p) : null;
            bool valid = CommandPoint(_pointer) is not null && (ability.GroundTarget || hovered is { } target && AbilityTarget(ability, _catalog.Units[_activeType], target));
            if (valid) _rtsOverlay.CustomCursor = binding.Cursor; else cursor = RtsCursor.Unavailable;
        }
        if (cursor is not (RtsCursor.Enter or RtsCursor.Unload) && _armedCustom is null && _armedAction is null && CommandPoint(_pointer) is { } capturePoint && Pick(capturePoint) is { } captureHover && CapturableTarget(captureHover) && Controlled().Any(id => _catalog!.Units[_selectionPoses[id].UnitType].Abilities.Any(a => a.Kind == AbilityKind.Capture))) _rtsOverlay.CustomCursor = "capture";
        if (_sitePlacement is { } placement)
        {
            if (CommandPoint(_pointer) is { } buildPoint && CanPlanSite(placement.Type, PlacementPoint(buildPoint, placement.Type)))
                _rtsOverlay.CustomCursor = "build";
            else cursor = RtsCursor.Unavailable;
        }
        else if (_armedAction is null && _armedCustom is null && !Input.IsPhysicalKeyPressed(Key.Ctrl) &&
            !Input.IsPhysicalKeyPressed(Key.Alt) && CommandPoint(_pointer) is { } workPoint)
        {
            var ghost = PickProject(workPoint);
            var site = Pick(workPoint);
            if (ghost is { } plan && BuildersFor(plan.Owner, plan.Position).Length > 0 ||
                site is { } unfinished && unfinished.Construction.Active && BuildersFor(unfinished.Presentation.Owner, unfinished.Current).Length > 0)
                _rtsOverlay.CustomCursor = "build";
        }
        _rtsOverlay.Rally = ActiveRecipients().FirstOrDefault(id => _selectionPoses[id].HasRally) is var rally && rally.Value != 0 ? Screen(_selectionPoses[rally] with { Current = _selectionPoses[rally].Rally }) : null;
        if (OverRtsPanel(_pointer))
        { _rtsOverlay.CustomCursor = null; cursor = _actionButtons.Values.Any(button => button.Disabled && button.GetGlobalRect().HasPoint(_pointer)) ? RtsCursor.Unavailable : RtsCursor.Default; }
        _rtsOverlay.Cursor = cursor;
    }
    private void UpdateSelection(float alpha)
    {
        if (_selectionRing is null) return;
        int count = _selection.Count;
        if (_ringBuffer.Length / 16 < count)
        { Array.Resize(ref _ringBuffer, Math.Max(16, Math.Max(count, _ringBuffer.Length / 8)) * 16); _selectionRing.Multimesh.InstanceCount = _ringBuffer.Length / 16; }
        int ring = 0;
        for (int i = 0; i < _displayCount; i++) if (_selection.Contains(_displayPoses[i].Id))
        {
            var p = _displayPoses[i]; int at = ring++ * 16;
            var type = _catalog!.Units[p.UnitType];
            _ringBuffer[at] = ((type.Footprint?.HalfWidth ?? type.Body.HalfWidth) / 1024f + 0.2f) / 0.88f;
            _ringBuffer[at + 10] = ((type.Footprint?.HalfHeight ?? type.Body.HalfHeight) / 1024f + 0.2f) / 0.88f; _ringBuffer[at + 5] = 1;
            _ringBuffer[at + 3] = (p.Previous.X + ((long)p.Current.X - p.Previous.X) * alpha) / 1024;
            _ringBuffer[at + 7] = 0.015f; _ringBuffer[at + 11] = (p.Previous.Y + ((long)p.Current.Y - p.Previous.Y) * alpha) / 1024;
            var color = p.Presentation.Owner switch
            {
                1 => new Color(0.6f, 1, 0.6f),
                3 or 4 => TeamMarkerColor(p.Presentation.Owner),
                _ => new Color(1, 0.35f, 0.6f)
            };
            _ringBuffer[at + 12] = color.R; _ringBuffer[at + 13] = color.G;
            _ringBuffer[at + 14] = color.B; _ringBuffer[at + 15] = 1;
        }
        _selectionRing.Visible = ring > 0; _selectionRing.Multimesh.VisibleInstanceCount = ring;
        if (ring > 0) _selectionRing.Multimesh.Buffer = _ringBuffer;
    }
}
