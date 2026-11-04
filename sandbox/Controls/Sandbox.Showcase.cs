using System;
using Godot;
using System.Linq;
using OpenRTS.Core;
using OpenRTS.Core.Orders;

public partial class Sandbox
{
    private bool HandleShowcaseKey(Key key)
    {
        if (key is not (Key.F or Key.K or Key.V)) return false;
        if (Selected() is not { } unit) return true;
        if (unit.Presentation.Owner != 1) { _notice = "Чужим юнитам нельзя отдавать приказы."; return true; }
        var type = _catalog!.Units[unit.UnitType];
        if (key == Key.F)
            _session!.Submit(CommandKind.PlayAnimation, entity: unit.Id, animation: type.AnimationIndex(type.Weapons.Count > 0 ? type.Weapons[0].Animation : "fire"), durationTicks: 12);
        else
        {
            string state = key == Key.K ? "damaged" : "swimming";
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
            selection = (_selection.Count > 1 ? $"Выбрано: {_selection.Count} · " : "") + (pose.Presentation.Owner == 1 ? "Синяя команда" : "Красная команда") + " · " + _catalog!.Presentations[pose.UnitType].Name;
            var type = _catalog.Units[pose.UnitType];
            if (_displayGrid is { } grid && _catalog.Locomotion is { } locomotion)
            {
                int terrain = grid.TerrainAt(grid.CellAt(pose.Current));
                string ground = locomotion.Terrains[terrain] switch
                { "road" => "бетон", "grass" => "трава", "mud" => "грязь", "water" => "вода", "forest" => "лес", "swamp" => "болото", _ => "горы" };
                string phase = pose.Order.Phase switch { OpenRTS.Core.Orders.OrderPhase.Moving => "идёт", OpenRTS.Core.Orders.OrderPhase.Approaching => "сближается",
                    OpenRTS.Core.Orders.OrderPhase.Engaging => "бой", OpenRTS.Core.Orders.OrderPhase.Holding => "держит позицию", _ => "ожидает" };
                string armor = type.Armor switch { ArmorKind.Living => "живой", ArmorKind.Mechanical => "техника", _ => "здание" };
                selection += $"\nHP {pose.Health}/{type.MaxHealth} · {armor} · {phase} · очередь {pose.Order.QueuedCount}\nГрунт: {ground} · скорость {type.Locomotor.Rules[terrain].Speed * 100.0 / 1024:0.#}%";
            }
            selection += "\n" + string.Join(" · ", type.Weapons.Select(m => $"{WeaponName(m.Weapon.Id)} ({m.Weapon.Range / 1024.0:0.#})"));
            for (int i = 0; i < type.States.Count; i++) if ((pose.Presentation.States & (1u << i)) != 0)
                selection += type.States[i] == "damaged" ? " · повреждён" : " · в воде";
        }
        return $"OPENRTS  /  ПОЛИГОН\n\n{selection}\n{_count} юнитов · тик {tick} · ×{_playbackSpeed:0.###}{(_paused ? " · ПАУЗА" : "")}" +
            (_notice.Length == 0 ? "" : "\n" + _notice);
    }
    private static string WeaponName(string id) => id switch { "rifle" => "автомат", "cannon" => "пушка", "missile" => "ракеты", "grenade" => "граната", "repair" => "ремонт", _ => id };
    private void AddShowcaseFooter()
    {
        var layer = new CanvasLayer(); AddChild(layer);
        var panel = new PanelContainer { AnchorTop = 1, AnchorBottom = 1, AnchorRight = 1,
            OffsetTop = -116, OffsetBottom = -18, OffsetLeft = 24, OffsetRight = -24, MouseFilter = Control.MouseFilterEnum.Stop };
        layer.AddChild(panel);
        _rtsPanels.Add(panel);
        var margin = new MarginContainer(); foreach (string side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 10);
        panel.AddChild(margin); var column = new VBoxContainer(); margin.AddChild(column);
        var row = new HBoxContainer(); column.AddChild(row);
        foreach (var action in new[] { (UnitActionKind.Move, "M · Идти"), (UnitActionKind.AttackMove, "A · Атаковать / идти"),
            (UnitActionKind.Repair, "E · Ремонт"), (UnitActionKind.Stop, "S · Стоп"), (UnitActionKind.HoldPosition, "H · Удерживать") })
        {
            var button = new Button { Text = action.Item2, FocusMode = Control.FocusModeEnum.None };
            var kind = action.Item1; button.Pressed += () => ActivateAction(kind, Input.IsPhysicalKeyPressed(Key.Shift));
            _actionButtons.Add(kind, button); row.AddChild(button);
        }
        var label = new Label { Text = "ЛКМ / двойной клик / рамка — выбор · Shift — добавить / очередь · Ctrl+клик — тип · Ctrl+1…9 / 1…9 — группы\nПКМ — идти / атаковать · A/M/E + ЛКМ — цель · Esc/ПКМ — отмена режима · стрелки/СКМ/колесо — камера · P/N/+/− — время" };
        label.AddThemeFontSizeOverride("font_size", 12); column.AddChild(label);
    }
}
