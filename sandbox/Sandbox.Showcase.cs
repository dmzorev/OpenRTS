using System;
using Godot;
using OpenRTS.Core;

public partial class Sandbox
{
    private void CreateSelectionRing()
    {
        _selectionRing = new MeshInstance3D { Name = "Selection", Mesh = new PlaneMesh { Size = new(2, 2),
            Material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/selection.gdshader") } } };
        AddChild(_selectionRing);
    }
    private void UpdateSelection(float alpha)
    {
        if (_selectionRing is null) return;
        _selectionRing.Visible = false;
        for (int i = 0; i < _displayCount; i++) if (_displayPoses[i].Id == _selected)
        {
            var p = _displayPoses[i];
            _selectionRing.Position = new((p.Previous.X + ((long)p.Current.X - p.Previous.X) * alpha) / 1024,
                0.015f, (p.Previous.Y + ((long)p.Current.Y - p.Previous.Y) * alpha) / 1024);
            float size = _catalog!.Units[p.UnitType].Id is "rifleman" or "rocketeer" ? 0.65f : 1;
            _selectionRing.Scale = new(size, 1, size); _selectionRing.Visible = true; return;
        }
    }
    private EntityPose? Selected()
    {
        for (int i = 0; i < _displayCount; i++) if (_displayPoses[i].Id == _selected) return _displayPoses[i];
        return null;
    }
    private EntityPose? Pick(WorldPosition position)
    {
        EntityPose? selected = null; long best = 1300L * 1300;
        for (int i = 0; i < _displayCount; i++)
        {
            var p = _displayPoses[i]; long x = (long)p.Current.X - position.X, y = (long)p.Current.Y - position.Y, distance = x * x + y * y;
            if (distance < best) { best = distance; selected = p; }
        }
        return selected;
    }
    private void HandleShowcaseClick(InputEventMouseButton click)
    {
        if (MousePoint(click.Position) is not { } point) return;
        var picked = Pick(point);
        if (click.ButtonIndex == MouseButton.Left) { _selected = picked?.Id ?? default; return; }
        if (Selected() is not { } unit) return;
        _notice = "";
        if (click.ShiftPressed || picked is { } target && target.Id != unit.Id && target.Presentation.Owner != unit.Presentation.Owner)
            _session!.Submit(CommandKind.Aim, point, unit.Id, targetEntity: picked?.Id ?? default);
        else
        {
            _session!.Submit(CommandKind.Move, point, unit.Id);
        }
    }
    private bool HandleShowcaseKey(Key key)
    {
        if (key is not (Key.F or Key.H or Key.V or Key.Space)) return false;
        if (Selected() is not { } unit) return true;
        var type = _catalog!.Units[unit.UnitType];
        if (key == Key.Space) _session!.Submit(CommandKind.ClearAim, entity: unit.Id);
        else if (key == Key.F)
            _session!.Submit(CommandKind.PlayAnimation, entity: unit.Id, animation: type.AnimationIndex("fire"), durationTicks: 12);
        else
        {
            string state = key == Key.H ? "damaged" : "swimming";
            if (!type.States.Contains(state)) { _notice = "Это состояние недоступно выбранному юниту."; return true; }
            _session!.Submit(CommandKind.ToggleVisualState, entity: unit.Id, states: type.StateMask(state));
        }
        return true;
    }
    private string ShowcaseLabel(long tick)
    {
        string selection = "Выберите юнита";
        if (Selected() is { } pose)
        {
            selection = (pose.Presentation.Owner == 1 ? "Синяя команда" : "Красная команда") + " · " + _catalog!.Presentations[pose.UnitType].Name;
            var type = _catalog.Units[pose.UnitType];
            if (_displayGrid is { } grid && _catalog.Locomotion is { } locomotion)
            {
                int terrain = grid.TerrainAt(grid.CellAt(pose.Current));
                string ground = locomotion.Terrains[terrain] switch
                { "road" => "бетон", "grass" => "трава", "mud" => "грязь", "water" => "вода", "forest" => "лес", "swamp" => "болото", _ => "горы" };
                selection += $"\nГрунт: {ground} · скорость {type.Locomotor.Rules[terrain].Speed * 100.0 / 1024:0.#}%";
            }
            for (int i = 0; i < type.States.Count; i++) if ((pose.Presentation.States & (1u << i)) != 0)
                selection += type.States[i] == "damaged" ? " · повреждён" : " · в воде";
        }
        return $"OPENRTS  /  ПОЛИГОН\n\n{selection}\n{_count} юнитов · тик {tick} · ×{_playbackSpeed:0.###}{(_paused ? " · ПАУЗА" : "")}" +
            (_notice.Length == 0 ? "" : "\n" + _notice);
    }
    private void AddShowcaseFooter()
    {
        var layer = new CanvasLayer(); AddChild(layer);
        var panel = new PanelContainer { AnchorTop = 1, AnchorBottom = 1, AnchorRight = 1,
            OffsetTop = -84, OffsetBottom = -18, OffsetLeft = 24, OffsetRight = -24 };
        layer.AddChild(panel);
        var margin = new MarginContainer(); foreach (string side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 10);
        panel.AddChild(margin);
        var label = new Label { Text = "ЛКМ — выбрать · ПКМ — идти / цель · Shift+ПКМ — навести оружие · F — огонь · H — повреждение · V — вода\nКолесо / WASD / СКМ — камера · P — пауза · N — шаг · +/− — темп · R — заново · Пробел — сбросить наведение" };
        label.AddThemeFontSizeOverride("font_size", 14); margin.AddChild(label);
    }
}
