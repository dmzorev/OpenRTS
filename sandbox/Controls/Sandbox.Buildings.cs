using System;
using System.Linq;
using Godot;
using OpenRTS.Core;

public partial class Sandbox
{
    private FootprintView? _footprintView;
    private OptionButton? _placeablePicker;
    private bool _placing;
    private int _placementTurns;
    private (WorldPosition Anchor, Vector2 Pixel, int OriginalTurns)? _placementDrag;
    private bool IsPlacingBuilding => _placing || _sitePlacement is not null;
    private string _placeableId = "power-plant";
    private int PlaceableType => _catalog!.Units.ToList().FindIndex(u => u.Id == _placeableId);
    private WorldPosition PlacementPoint(WorldPosition point, int? unitType = null)
    {
        var footprint = _catalog!.Units[unitType ?? PlaceableType].WithRotation(_placementTurns).Footprint!; int step = _displayGrid?.CellSize ?? 1024;
        int x = (int)Math.Round((point.X - footprint.HalfWidth) / (double)step, MidpointRounding.AwayFromZero) * step + footprint.HalfWidth;
        int y = (int)Math.Round((point.Y - footprint.HalfHeight) / (double)step, MidpointRounding.AwayFromZero) * step + footprint.HalfHeight;
        return new(Math.Clamp(x, 0, (int)_mapWidth * 1024), Math.Clamp(y, 0, (int)_mapDepth * 1024));
    }
    private void ResetPlacementRotation()
    {
        _placementTurns = 0;
        _placementDrag = null;
    }

    private bool RotatePlacement(Key key)
    {
        if (!IsPlacingBuilding || _placementDrag is not null || key is not (Key.Bracketleft or Key.Bracketright)) return false;
        _placementTurns = (_placementTurns + (key == Key.Bracketright ? 1 : 3)) % 4;
        UpdateFootprintPreview();
        return true;
    }

    private void UpdatePlacementDrag(Vector2 end)
    {
        if (_placementDrag is not { } drag) return;
        _placementTurns = drag.OriginalTurns;
        if (drag.Pixel.DistanceTo(end) >= 8 && GroundPoint(end) is { } world)
        {
            float x = world.X - drag.Anchor.X / 1024f;
            float y = world.Z - drag.Anchor.Y / 1024f;
            // Drag points toward the exit side, which starts on the south edge.
            _placementTurns = Math.Abs(x) > Math.Abs(y) ? (x > 0 ? 3 : 1) : (y > 0 ? 0 : 2);
        }
        UpdateFootprintPreview();
    }

    private void FinishPlacementDrag(InputEventMouseButton release)
    {
        if (_placementDrag is not { } drag) return;
        if (IsPlacingBuilding && _matchMenu?.Visible != true)
        {
            UpdatePlacementDrag(release.Position);
            if (!HandlePlacement(release, drag.Anchor)) HandleConstructionClick(release, drag.Anchor, null);
        }
        _placementDrag = null;
        UpdateFootprintPreview();
        UpdateRtsCursor();
    }

    private ConstructionProjectPose? BuildingPreview()
    {
        if (!IsPlacingBuilding || (_placementDrag?.Anchor ?? CommandPoint(_pointer)) is not { } point) return null;
        int type = _sitePlacement?.Type ?? PlaceableType;
        uint owner = _sitePlacement is not null || _productionPlacement is not null
            ? Selected()?.Presentation.Owner ?? _constructionViewer
            : _placeableId.StartsWith("nature-", StringComparison.Ordinal) ? 0u : 1u;
        return new(default, owner, type, PlacementPoint(point, type), false) { QuarterTurns = _placementTurns };
    }

    private void AddBuildingTools(VBoxContainer column)
    {
        _footprintView = new(); AddChild(_footprintView.Node);
        if (_skirmish) return;
        var row = new HBoxContainer(); column.AddChild(row);
        _placeablePicker = new() { FocusMode = Control.FocusModeEnum.None };
        foreach (string id in BuildingScenario.Placeable)
        { int type = _catalog!.Units.ToList().FindIndex(u => u.Id == id); _placeablePicker.AddItem(_catalog.Presentations[type].Name); }
        _placeablePicker.ItemSelected += index => { _placeableId = BuildingScenario.Placeable[(int)index]; ResetPlacementRotation(); }; row.AddChild(_placeablePicker);
        _placeablePicker.TooltipText = "Гражданские здания: выберите пехоту и кликните по зданию";
        void Button(string text, Action action) { var b = new Button { Text = text, FocusMode = Control.FocusModeEnum.None }; b.Pressed += action; row.AddChild(b); }
        Button("B · Разместить", () => { ResetPlacementRotation(); _placing = true; _sitePlacement = null; _productionPlacement = null; _armedCustom = null; _armedAction = null; });
        Button("Del · Убрать", () => RemoveBuilding(false)); Button("Backspace · Разрушить", () => RemoveBuilding(true));
        Button("Обломки: очистить", () => _sprites!.ClearWrecks());
    }
    private void RemoveBuilding(bool wreck)
    {
        foreach (var id in _selection.Ids)
            if (_selectionPoses.TryGetValue(id, out var pose) && _catalog!.Units[pose.UnitType].Footprint is not null)
                _session!.Submit(wreck ? CommandKind.Destroy : CommandKind.Remove, entity: id);
    }
    private bool HandleBuildingKey(Key key)
    {
        if (!_buildings || _skirmish) return false;
        if (key == Key.B) { ResetPlacementRotation(); _placing = true; _sitePlacement = null; _productionPlacement = null; _armedCustom = null; _armedAction = null; return true; }
        if (key == Key.Escape && _placing) { _placing = false; _productionPlacement = null; return true; }
        if (key is Key.Delete or Key.Backspace) { RemoveBuilding(key == Key.Backspace); return true; }
        if (Selected() is not { } selected || _catalog!.Units[selected.UnitType].Footprint is null) return false;
        var type = _catalog.Units[selected.UnitType];
        if (key == Key.G && type.Transport is null && type.States.Contains("garrisoned")) { _session!.Submit(CommandKind.ToggleVisualState, entity: selected.Id, states: type.StateMask("garrisoned")); return true; }
        if (key == Key.W && type.States.Contains("working")) { _session!.Submit(CommandKind.ToggleVisualState, entity: selected.Id, states: type.StateMask("working")); return true; }
        string? animation = key switch { Key.O => "door", Key.C => "construct", Key.F => "produce", _ => null };
        if (animation is null || type.AnimationIndex(animation) < 0) return false;
        if (animation == "produce" && type.States.Contains("working")) _session!.Submit(CommandKind.SetVisualStates, entity: selected.Id, states: selected.Presentation.States | type.StateMask("working"));
        _session!.Submit(CommandKind.PlayAnimation, entity: selected.Id, animation: type.AnimationIndex(animation), durationTicks: animation == "construct" ? 75 : 40, track: animation == "door" ? AnimationTrack.Weapon : AnimationTrack.Body);
        return true;
    }
    private bool HandlePlacement(InputEventMouseButton input, WorldPosition point)
    {
        if (!_placing) return false;
        if (input.ButtonIndex == MouseButton.Right) { _placing = false; _productionPlacement = null; return true; }
        if (input.ButtonIndex != MouseButton.Left) return true;
        var position = PlacementPoint(point); uint owner = _placeableId.StartsWith("nature-", StringComparison.Ordinal) ? 0u : 1u;
        if (_productionPlacement is { } task)
        { var producer = _catalog!.Units[_selectionPoses[task.Producer].UnitType];
            _session!.Submit(CommandKind.PlaceProduced, position, entity: task.Producer, action: producer.Products[task.Product].Id, taskId: task.Id, quarterTurns: _placementTurns); return true; }
        _session!.Submit(CommandKind.Spawn, position, unitType: PlaceableType, owner: owner, quarterTurns: _placementTurns);
        if (!input.ShiftPressed) _placing = false;
        return true;
    }
    private void UpdateFootprintPreview()
    {
        if (_footprintView is null) return;
        var selected = Selected(); var definition = selected is { } p ? _catalog!.Units[p.UnitType].WithRotation(p.QuarterTurns).Footprint : null;
        FootprintDefinition? preview = null; WorldPosition point = default; bool valid = true;
        if (BuildingPreview() is { } building)
        {
            point = building.Position;
            preview = _catalog!.Units[building.UnitType].WithRotation(building.QuarterTurns).Footprint;
            valid = _sitePlacement is not null ? CanPlanSite(building.UnitType, point) :
                point.X >= preview!.HalfWidth && point.Y >= preview.HalfHeight &&
                point.X + preview.HalfWidth <= _mapWidth * 1024 && point.Y + preview.HalfHeight <= _mapDepth * 1024;
        }
        var project = _visibleProjects.FirstOrDefault(p => p.Id == _selectedProject);
        if (project.Id.Value != 0) definition = _catalog!.Units[project.UnitType].WithRotation(project.QuarterTurns).Footprint;
        _footprintView.Apply(definition, project.Id.Value != 0 ? project.Position : selected?.Current ?? default, preview, point, valid);

        int radius = 0;
        if (selected is { } builder && !builder.Construction.Active && Controls(builder.Presentation.Owner))
        {
            var type = _catalog!.Units[builder.UnitType];
            if (!type.Movable) radius = type.ConstructionRange;
        }
        _footprintView.SetRange(selected?.Current ?? default, radius, _sitePlacement is not null);
    }
}
