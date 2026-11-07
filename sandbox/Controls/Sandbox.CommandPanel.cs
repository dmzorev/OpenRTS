using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OpenRTS.Core;
using OpenRTS.Content;

public partial class Sandbox
{
    private ModelCatalog? _modelCatalog;
    private HBoxContainer? _typeRow, _tasksRow;
    private Container? _customRow;
    private ScrollContainer? _taskScroll;
    private readonly Dictionary<string, Button> _customButtons = new();
    private readonly Dictionary<string, (Button Button, ProgressBar Progress)> _taskButtons = new();
    private readonly Dictionary<string, ProgressBar> _cooldowns = new();
    private readonly Dictionary<int, Texture2D?> _icons = new();
    private int[] _panelTypes = []; private int _activeType = -1;
    private long _panelRevision = -1, _displayTick; private int _displayRate = 25;
    private WeaponPose[] _displayWeapons = []; private int _displayWeaponCount;
    private TaskPose[] _displayTasks = []; private int _displayTaskCount;
    private string? _armedCustom;
    private TaskPose? _productionPlacement;
    private string _taskSignature = "";
    private EntityId[] _activeRecipients = [];
    private long _panelTick = -1; private bool _panelDirty = true;
    private EntityId[] ActiveRecipients() => _activeRecipients;
    private Texture2D? UnitIcon(int type)
    {
        if (_icons.TryGetValue(type, out var icon)) return icon;
        var model = _catalog!.Presentations[type].Model;
        if (model is not null && _modelCatalog!.Models.TryGetValue(model, out var definition))
        {
            var frames = GD.Load<SpriteFrames>(definition.Parts.First(p => p.Parent < 0).Resource);
            icon = frames?.GetFrameTexture("stand", 0);
            if (_terminal is not null && icon is not null && DisplayServer.GetName() != "headless")
                icon = TerminalIcon(icon);
        }
        _icons[type] = icon; return icon;
    }
    private void AddCommandPanel(VBoxContainer column)
    {
        var row = new HBoxContainer(); column.AddChild(row);
        AddTypeStrip(row, 300);
        var actions = new ScrollContainer { CustomMinimumSize = new(700, 52), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalScrollMode = ScrollContainer.ScrollMode.Disabled }; row.AddChild(actions); _customRow = new(); actions.AddChild(_customRow);
        AddTaskStrip(column);
        AddSelectionExtras(column);
    }

    private void AddTypeStrip(Container parent, int width = 0)
    {
        var types = new ScrollContainer { CustomMinimumSize = new(width, 52), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalScrollMode = ScrollContainer.ScrollMode.Disabled };
        parent.AddChild(types); _typeRow = new(); types.AddChild(_typeRow);
    }

    private void AddTaskStrip(VBoxContainer column)
    {
        _taskScroll = new() { CustomMinimumSize = new(0, 44), HorizontalScrollMode = ScrollContainer.ScrollMode.Auto, VerticalScrollMode = ScrollContainer.ScrollMode.Disabled };
        column.AddChild(_taskScroll); _tasksRow = new(); _taskScroll.AddChild(_tasksRow); _taskScroll.Visible = false;
    }

    private void AddSelectionExtras(VBoxContainer column)
    {
        AddStoredPanel(column);
        _cancelProject = new Button { Text = "Отменить проект · Delete", FocusMode = Control.FocusModeEnum.None, Visible = false };
        _cancelProject.Pressed += CancelProject;
        column.AddChild(_cancelProject);
    }
    private static void ClearChildren(Node node) { foreach (var child in node.GetChildren()) { node.RemoveChild(child); child.QueueFree(); } }
    private Button IconButton(string text, Texture2D? icon, int width = 84)
    {
        var button = new Button { Text = text, Icon = icon, ExpandIcon = true, CustomMinimumSize = new(width, 42), FocusMode = Control.FocusModeEnum.None };
        if (_terminal is not null) button.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        button.AddThemeConstantOverride("icon_max_width", 30); button.AddThemeFontSizeOverride("font_size", 12); return button;
    }
    private void RefreshCommandSelection()
    {
        if (_typeRow is null || _catalog is null) return;
        if (_panelRevision == _selection.Revision) return;
        var types = _selection.Ids.ToArray().Where(_selectionPoses.ContainsKey).Select(id => _selectionPoses[id].UnitType).Distinct()
            .OrderByDescending(t => _catalog.Presentations[t].Actions?.Count > 0).ThenBy(t => _catalog.Units[t].Id, StringComparer.Ordinal).ToArray();
        bool changed = _panelRevision != _selection.Revision || !types.SequenceEqual(_panelTypes);
        if (!changed) return;
        _panelRevision = _selection.Revision; _panelTypes = types; _activeType = types.FirstOrDefault(-1); _armedCustom = null;
        RebuildCommandPanel();
    }
    private void RebuildCommandPanel()
    {
        if (_typeRow is null || _customRow is null) return;
        _sitePlacement = null;
        _panelDirty = true; _activeRecipients = Controlled().Where(id => _selectionPoses[id].UnitType == _activeType).ToArray();
        ClearChildren(_typeRow); ClearChildren(_customRow); if (_tasksRow is not null) ClearChildren(_tasksRow); if (_taskScroll is not null) _taskScroll.Visible = false; _customButtons.Clear(); _cooldowns.Clear(); _taskSignature = "";
        foreach (int type in _panelTypes)
        {
            int count = _selection.Ids.ToArray().Count(id => _selectionPoses.TryGetValue(id, out var p) && p.UnitType == type);
            var button = IconButton(count.ToString(), UnitIcon(type), 58);
            button.TooltipText = _catalog!.Presentations[type].Name + " · Tab / Shift+Tab";
            button.Modulate = type == _activeType ? ActiveButtonColor : Colors.White;
            button.Pressed += () => { _activeType = type; _armedCustom = null; RebuildCommandPanel(); }; _typeRow.AddChild(button);
        }
        if (_activeType < 0) return;
        foreach (var action in _catalog!.Presentations[_activeType].Actions ?? [])
        {
            var type = _catalog.Units[_activeType]; var product = type.Products.FirstOrDefault(p => p.Id == action.Id);
            int target = product is null ? _activeType : _catalog.Units.ToList().FindIndex(u => u.Id == product.Unit);
            var icon = action.Icon is null ? UnitIcon(target) : GD.Load<Texture2D>(action.Icon);
            var button = IconButton(action.Label, icon, _terminal is null ? 120 : 64);
            if (_terminal is not null)
            {
                button.IconAlignment = HorizontalAlignment.Center;
                button.VerticalIconAlignment = VerticalAlignment.Top;
                button.CustomMinimumSize = new(64, 60);
                button.AddThemeConstantOverride("icon_max_width", 38);
                button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            }
            var progress = new ProgressBar { ShowPercentage = false, MouseFilter = Control.MouseFilterEnum.Ignore, AnchorTop = 1, AnchorBottom = 1, AnchorRight = 1, OffsetTop = -4 };
            button.AddChild(progress); _cooldowns.Add(action.Id, progress);
            button.GuiInput += input => { if (input is InputEventMouseButton { Pressed: true } click && click.ButtonIndex is MouseButton.Left or MouseButton.Right)
                { ActivateCustom(action.Id, click.ButtonIndex == MouseButton.Right, click.ShiftPressed); button.AcceptEvent(); } };
            _customRow.AddChild(button); _customButtons.Add(action.Id, button);
        }
    }
    private bool HandleCustomKey(InputEventKey input, Key code)
    {
        if (_activeType < 0 || !ActiveRecipients().Any()) return false;
        foreach (var action in _catalog!.Presentations[_activeType].Actions ?? [])
        {
            if (action.Hotkey.Length == 0) continue;
            if (!Enum.TryParse<Key>(action.Hotkey.Length == 1 && char.IsDigit(action.Hotkey[0]) ? "Key" + action.Hotkey : action.Hotkey, true, out var key))
                throw new InvalidOperationException("Unknown custom hotkey: " + action.Hotkey);
            if (key == code) { ActivateCustom(action.Id, false, input.ShiftPressed); return true; }
        }
        return false;
    }
    private bool CycleCommandType(bool previous)
    {
        if (_panelTypes.Length == 0) return false;
        int current = Array.IndexOf(_panelTypes, _activeType); _activeType = _panelTypes[(current + (previous ? _panelTypes.Length - 1 : 1)) % _panelTypes.Length];
        _armedCustom = null; RebuildCommandPanel(); return true;
    }
    private WeaponPose? AbilityPose(EntityId id, string action)
    {
        var pose = _selectionPoses[id]; var type = _catalog!.Units[pose.UnitType]; var skill = type.Abilities.First(a => a.Id == action);
        if (skill.Kind == AbilityKind.Capture) return null;
        int mount = 0; while (mount < type.Weapons.Count && type.Weapons[mount].Id != skill.Mount) mount++;
        int offset = pose.WeaponOffset + mount;
        return offset >= 0 && offset < _displayWeaponCount && _displayWeapons[offset].Entity == id ? _displayWeapons[offset] : null;
    }
    private void ActivateCustom(string id, bool right, bool shift)
    {
        _panelDirty = true;
        var ids = ActiveRecipients().Where(id => !_selectionPoses[id].Construction.Active).ToArray();
        if (ids.Length == 0 || _session is null) return;
        var type = _catalog!.Units[_activeType]; var ability = type.Abilities.FirstOrDefault(a => a.Id == id);
        _armedAction = null; _armedCustom = null; _placing = false; _productionPlacement = null; _sitePlacement = null; _pointerDown = false;
        if (ability is not null)
        {
            if (right && ability.Kind == AbilityKind.Capture) { _notice = "Захват применяется вручную."; return; }
            if (right)
            { bool enabled = !ids.All(e => AbilityPose(e, id)?.AutoCast == true); _session.Submit(CommandKind.SetAutoCast, recipients: new(ids), action: id, enabled: enabled); }
            else _armedCustom = id;
        }
        else
        {
            var product = type.Products.First(p => p.Id == id);
            if (product.OnSite)
            {
                if (!right)
                {
                    int target = _catalog.Units.ToList().FindIndex(u => u.Id == product.Unit);
                    _sitePlacement = (id, target);
                    _notice = "Выберите место строительства. Shift сохраняет режим и добавляет приказ в очередь.";
                }
                return;
            }
            var ready = ReadyProduct(id);
            if (!right && ready is { } task)
            { _productionPlacement = task; _placeableId = _catalog.Units[task.UnitType].Id; _placing = true; _armedCustom = null; }
            else _session.Submit(CommandKind.EnqueueTask, recipients: new(ids), action: id, amount: shift ? 5 : 1, repeat: right);
        }
        _notice = ""; UpdateRtsCursor();
    }
    private TaskPose? ReadyProduct(string action)
    {
        var ids = ActiveRecipients().ToHashSet();
        for (int i = 0; i < _displayTaskCount; i++)
        {
            var task = _displayTasks[i]; if (!ids.Contains(task.Producer) || !task.Ready || _catalog!.Units[task.UnitType].Footprint is null) continue;
            var producer = _catalog.Units[_selectionPoses[task.Producer].UnitType];
            if (producer.Products[task.Product].Id == action) return task;
        }
        return null;
    }
    private bool HandleCustomTarget(InputEventMouseButton click, WorldPosition point, EntityPose? picked)
    {
        if (_armedCustom is not { } action) return false;
        _panelDirty = true;
        if (click.ButtonIndex == MouseButton.Right) { _armedCustom = null; return true; }
        if (click.ButtonIndex != MouseButton.Left || ActiveRecipients() is not { Length: > 0 } ids) return true;
        var type = _catalog!.Units[_activeType]; var ability = type.Abilities.First(a => a.Id == action);
        bool target = picked is { } p && AbilityTarget(ability, type, p);
        if (!target && !ability.GroundTarget) { _notice = "Выберите допустимую цель способности."; return true; }
        _session!.Submit(CommandKind.UseAbility, point, recipients: new(ids), targetEntity: target ? picked!.Value.Id : default, action: action);
        _armedCustom = null; return true;
    }
    private bool ContextCapture(EntityPose target)
    {
        foreach (var id in Controlled())
        {
            var pose = _selectionPoses[id]; if (pose.Presentation.Owner == target.Presentation.Owner) continue;
            var skill = _catalog!.Units[pose.UnitType].Abilities.FirstOrDefault(a => a.Kind == AbilityKind.Capture);
            if (skill is null) continue;
            _session!.Submit(CommandKind.UseAbility, recipients: new([id]), targetEntity: target.Id, action: skill.Id);
            return true;
        }
        return false;
    }
    private bool ContextRepair(EntityPose target)
    {
        foreach (var id in Controlled())
        {
            var unit = _selectionPoses[id]; var type = _catalog!.Units[unit.UnitType];
            var ability = type.Abilities.FirstOrDefault(a => a.Kind == AbilityKind.Weapon && type.Weapons.First(m => m.Id == a.Mount).Weapon.Effect == WeaponEffect.Repair);
            if (ability is null) continue;
            var recipients = Controlled().Where(e => _selectionPoses[e].UnitType == unit.UnitType && _selectionPoses[e].Presentation.Owner == target.Presentation.Owner);
            _session!.Submit(CommandKind.UseAbility, target.Current, targetEntity: target.Id, recipients: new(recipients), action: ability.Id); return true;
        }
        return false;
    }
    private void UpdateCommandPanel(FrameExchange.FrameLease frame)
    {
        if (_panelTick == frame.Tick && !_panelDirty) return;
        _panelTick = frame.Tick; _panelDirty = false;
        RefreshStoredPanel();
        _displayTick = frame.Tick; _displayRate = frame.TickRate;
        if (_displayWeapons.Length < frame.Weapons.Length) Array.Resize(ref _displayWeapons, frame.Weapons.Length);
        frame.Weapons.CopyTo(_displayWeapons); _displayWeaponCount = frame.Weapons.Length;
        if (_displayTasks.Length < frame.Tasks.Length) Array.Resize(ref _displayTasks, frame.Tasks.Length);
        frame.Tasks.CopyTo(_displayTasks); _displayTaskCount = frame.Tasks.Length;
        if (_productionPlacement is { } placement)
        { bool present = false; foreach (var task in frame.Tasks) if (task.Id == placement.Id) { present = true; break; }
            if (!present) { _productionPlacement = null; _placing = false; } }
        if (_activeType < 0 || _tasksRow is null || _catalog is null) return;
        var ids = ActiveRecipients(); var type = _catalog.Units[_activeType];
        foreach (var action in _catalog.Presentations[_activeType].Actions ?? [])
        {
            var button = _customButtons[action.Id]; string status = ""; bool auto = false; double progress = 0;
            if (type.Abilities.FirstOrDefault(a => a.Id == action.Id) is { } ability)
            {
                if (ability.Kind == AbilityKind.Capture)
                {
                    var casts = ids.Select(id => _selectionPoses[id].Order).Where(o => o.Kind == UnitActionKind.Capture && o.CaptureDuration > 0).ToArray();
                    progress = casts.Length == 0 ? 0 : casts.Max(o => 100.0 * o.CaptureProgress / o.CaptureDuration);
                    button.Text = _terminal is null ? $"{action.Hotkey} · {action.Label} {progress:0}%"
                        : $"{action.Hotkey} · {progress:0}%";
                    button.TooltipText = $"{action.Label} [{action.Hotkey}]\nЛКМ/хоткей → чужой или нейтральный объект. Сохраняет HP.";
                    button.Disabled = ids.Length == 0 || ids.All(id => _selectionPoses[id].Construction.Active); _cooldowns[action.Id].Value = progress;
                    button.Modulate = _armedCustom == action.Id ? ActiveButtonColor : Colors.White;
                    continue;
                }
                var states = ids.Select(id => AbilityPose(id, action.Id)).Where(p => p is not null).Select(p => p!.Value).ToArray();
                long remaining = states.Length == 0 ? 0 : states.Min(p => Math.Max(0, p.ReadyTick - frame.Tick)); auto = states.Length > 0 && states.All(p => p.AutoCast);
                var weapon = type.Weapons.First(m => m.Id == ability.Mount).Weapon;
                progress = 100.0 * remaining / weapon.CooldownTicks;
                status = remaining > 0 ? $" {remaining / (double)frame.TickRate:0.0}s" : "";
                if (states.Any(p => p.AutoCast) && !auto) status += " ±авто";
                button.TooltipText = $"{action.Label} [{action.Hotkey}]\nЛКМ/хоткей — выбрать цель. ПКМ — автоприменение. Один исполнитель на приказ.";
            }
            else
            {
                var product = type.Products.First(p => p.Id == action.Id); var target = _catalog.Units.First(u => u.Id == product.Unit); var build = product.Override ?? target.Build!;
                bool ready = !product.OnSite && ReadyProduct(action.Id) is not null;
                status = _terminal is not null && product.OnSite ? " · Проект"
                    : ready ? (_terminal is null ? " ГОТОВО" : " ✓") : $" · {build.Ticks / (double)frame.TickRate:0.#} с";
                button.TooltipText = product.OnSite
                    ? $"{action.Label} [{action.Hotkey}] · {build.Cost:N0} кр. · {build.Ticks / (double)frame.TickRate:0.#} с\nЛКМ → разместить проект. Shift — добавить в очередь и продолжить размещение."
                    : $"{action.Label} [{action.Hotkey}] · {build.Cost:N0} кр. · {build.Ticks / (double)frame.TickRate:0.#} с\nShift — 5; ПКМ — повтор; готовое здание: ЛКМ → разместить.";
            }
            button.Text = (_terminal is null ? $"{action.Hotkey} · {action.Label}" : action.Hotkey) + status + (auto ? " ↻" : "");
            bool armed = _armedCustom == action.Id || _sitePlacement?.Action == action.Id ||
                _productionPlacement is { } placementTask && ids.Contains(placementTask.Producer) && type.Products[placementTask.Product].Id == action.Id;
            button.Disabled = ids.Length == 0 || ids.All(id => _selectionPoses[id].Construction.Active); button.Modulate = armed ? ActiveButtonColor : auto ? new(0.55f, 1, 0.65f) : Colors.White;
            _cooldowns[action.Id].Value = Math.Clamp(progress, 0, 100);
        }
        var selected = ids.ToHashSet();
        var tasks = _displayTasks.Take(_displayTaskCount).Where(t => selected.Contains(t.Producer)).Select(t =>
        { var producer = _catalog.Units[_selectionPoses[t.Producer].UnitType]; return (Task: t, Action: producer.Products[t.Product].Id, Queue: producer.TaskQueues[t.Queue].Id); }).ToArray();
        string signature = string.Join(';', tasks.Select(t => $"{t.Task.Id}:{t.Task.Ready}"));
        if (signature != _taskSignature)
        {
            _taskSignature = signature; ClearChildren(_tasksRow); _taskButtons.Clear();
            if (_terminal is not null && tasks.Length > 0)
                _tasksRow.AddChild(new Label { Text = "Очередь" });
            foreach (var group in tasks.GroupBy(t => t.Queue + ":" + t.Action + ":" + t.Task.Repeat))
            {
                var first = group.First(); string action = first.Action, queue = first.Queue; bool repeat = first.Task.Repeat;
                var button = IconButton("", UnitIcon(first.Task.UnitType), 100);
                var bar = new ProgressBar { ShowPercentage = false, MouseFilter = Control.MouseFilterEnum.Ignore, AnchorTop = 1, AnchorBottom = 1, AnchorRight = 1, OffsetTop = -4 }; button.AddChild(bar);
                button.GuiInput += input => { if (input is InputEventMouseButton { Pressed: true } click && click.ButtonIndex is MouseButton.Left or MouseButton.Right && ActiveRecipients() is { Length: > 0 } recipients)
                    { _session!.Submit(CommandKind.CancelTasks, recipients: new(recipients), action: action, queue: queue, repeat: repeat, amount: click.ButtonIndex == MouseButton.Right ? int.MaxValue : click.ShiftPressed ? 5 : 1); button.AcceptEvent(); } };
                _tasksRow.AddChild(button); _taskButtons.Add(group.Key, (button, bar));
            }
        }
        foreach (var group in tasks.GroupBy(t => t.Queue + ":" + t.Action + ":" + t.Task.Repeat))
        {
            var (button, bar) = _taskButtons[group.Key]; var first = group.First(); bool ready = group.Any(t => t.Task.Ready);
            button.Text = $"×{group.Count()}{(first.Task.Repeat ? " ∞" : "")}{(ready ? " ✓" : "")}";
            button.TooltipText = _catalog.Presentations[first.Task.UnitType].Name + $" · {first.Queue}\nЛКМ: −1 · Shift: −5 · ПКМ: вся стопка";
            bar.Value = group.Max(t => 100.0 * t.Task.Progress / t.Task.Duration);
        }
        _taskScroll!.Visible = tasks.Length > 0;
    }
}
