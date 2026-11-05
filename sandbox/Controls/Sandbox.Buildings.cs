using System;
using System.Linq;
using Godot;
using OpenRTS.Core;

public partial class Sandbox
{
    private FootprintView? _footprintView;
    private OptionButton? _placeablePicker;
    private bool _placing;
    private string _placeableId = "power-plant";
    private int PlaceableType => _catalog!.Units.ToList().FindIndex(u => u.Id == _placeableId);
    private WorldPosition PlacementPoint(WorldPosition point)
    {
        var footprint = _catalog!.Units[PlaceableType].Footprint!; int step = _displayGrid?.CellSize ?? 1024;
        int x = (int)Math.Round((point.X - footprint.HalfWidth) / (double)step, MidpointRounding.AwayFromZero) * step + footprint.HalfWidth;
        int y = (int)Math.Round((point.Y - footprint.HalfHeight) / (double)step, MidpointRounding.AwayFromZero) * step + footprint.HalfHeight;
        return new(Math.Clamp(x, 0, (int)_mapWidth * 1024), Math.Clamp(y, 0, (int)_mapDepth * 1024));
    }
    private void AddBuildingTools(VBoxContainer column)
    {
        _footprintView = new(); AddChild(_footprintView.Node);
        var row = new HBoxContainer(); column.AddChild(row);
        _placeablePicker = new() { FocusMode = Control.FocusModeEnum.None };
        foreach (string id in BuildingScenario.Placeable)
        { int type = _catalog!.Units.ToList().FindIndex(u => u.Id == id); _placeablePicker.AddItem(_catalog.Presentations[type].Name); }
        _placeablePicker.ItemSelected += index => _placeableId = BuildingScenario.Placeable[(int)index]; row.AddChild(_placeablePicker);
        void Button(string text, Action action) { var b = new Button { Text = text, FocusMode = Control.FocusModeEnum.None }; b.Pressed += action; row.AddChild(b); }
        Button("B · Разместить", () => { _placing = true; _productionPlacement = null; _armedCustom = null; _armedAction = null; });
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
        if (!_buildings) return false;
        if (key == Key.B) { _placing = true; _productionPlacement = null; _armedCustom = null; _armedAction = null; return true; }
        if (key == Key.Escape && _placing) { _placing = false; _productionPlacement = null; return true; }
        if (key is Key.Delete or Key.Backspace) { RemoveBuilding(key == Key.Backspace); return true; }
        if (Selected() is not { } selected || _catalog!.Units[selected.UnitType].Footprint is null) return false;
        var type = _catalog.Units[selected.UnitType];
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
            _session!.Submit(CommandKind.PlaceProduced, position, entity: task.Producer, action: producer.Products[task.Product].Id, taskId: task.Id); return true; }
        _session!.Submit(CommandKind.Spawn, position, unitType: PlaceableType, owner: owner);
        if (!input.ShiftPressed) _placing = false;
        return true;
    }
    private void UpdateFootprintPreview()
    {
        if (_footprintView is null) return;
        var selected = Selected(); var definition = selected is { } p ? _catalog!.Units[p.UnitType].Footprint : null;
        FootprintDefinition? preview = null; WorldPosition point = default; bool valid = true;
        if (_placing && CommandPoint(_pointer) is { } target)
        {
            point = PlacementPoint(target); preview = _catalog!.Units[PlaceableType].Footprint;
            valid = point.X >= preview!.HalfWidth && point.Y >= preview.HalfHeight && point.X + preview.HalfWidth <= _mapWidth * 1024 && point.Y + preview.HalfHeight <= _mapDepth * 1024;
        }
        _footprintView.Apply(definition, selected?.Current ?? default, preview, point, valid);
    }
}
