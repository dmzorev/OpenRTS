using System.Collections.Generic;
using System.Linq;
using Godot;
using OpenRTS.Core;
using OpenRTS.Core.Orders;

public partial class Sandbox
{
    private readonly List<EntityPose> _storedPoses = [];
    private readonly HashSet<EntityId> _loadedTransports = [];
    private ScrollContainer? _storedScroll;
    private HBoxContainer? _storedRow;
    private EntityPose[] _shownCargo = [];

    private void AddStoredPanel(VBoxContainer column)
    {
        _storedScroll = new() { Visible = false, CustomMinimumSize = new(0, 44), VerticalScrollMode = ScrollContainer.ScrollMode.Disabled };
        column.AddChild(_storedScroll);
        _storedRow = new();
        _storedScroll.AddChild(_storedRow);
    }

    private EntityId[] SelectedTransports() => _loadedTransports.Count == 0 ? [] : Controlled().Where(_loadedTransports.Contains).ToArray();

    private bool CanBoard(EntityId id, EntityPose carrier)
    {
        if (id == carrier.Id || !_selectionPoses.TryGetValue(id, out var pose) || !Controls(pose.Presentation.Owner)) return false;
        var type = _catalog!.Units[carrier.UnitType];
        var passenger = _catalog.Units[pose.UnitType].Passenger;
        return (type.Flight is null || carrier.Flight.Phase == FlightPhase.Landed) && passenger is not null && type.TransportFor(carrier.Upgrade.Completed) is { } transport && passenger.Slots <= transport.Slots &&
            transport.Accepts.Contains(passenger.Type) && !_loadedTransports.Contains(id) &&
            (carrier.Presentation.Owner == pose.Presentation.Owner || carrier.Presentation.Owner == 0 && !type.Movable);
    }

    private bool CanBoardSelected(EntityPose carrier)
    {
        if (_catalog!.Units[carrier.UnitType].Transport is null) return false;
        foreach (var id in _selection.Ids) if (CanBoard(id, carrier)) return true;
        return false;
    }

    private bool CanUnload(EntityPose carrier) =>
        _selection.Contains(carrier.Id) && Controls(carrier.Presentation.Owner) && _loadedTransports.Contains(carrier.Id);

    private void RefreshStoredPanel()
    {
        if (_storedRow is null || _catalog is null) return;
        var carriers = SelectedTransports();
        var cargo = _storedPoses.Where(pose => carriers.Contains(pose.Carrier)).OrderBy(pose => pose.Carrier.Value).ThenBy(pose => pose.Id.Value).ToArray();
        if (cargo.Length == _shownCargo.Length && cargo.Zip(_shownCargo).All(pair => pair.First.Id == pair.Second.Id &&
            pair.First.Carrier == pair.Second.Carrier && pair.First.Health == pair.Second.Health)) return;
        _shownCargo = cargo;
        _storedScroll!.Visible = cargo.Length > 0;
        ClearChildren(_storedRow);
        if (cargo.Length == 0) return;

        _storedRow.AddChild(new Label { Text = "Внутри" });
        var unload = IconButton("Вывести всех", null, 120);
        unload.TooltipText = "Клик: выбрать точку. Двойной клик: выгрузить на месте.";
        unload.GuiInput += input =>
        {
            if (input is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click) return;
            if (click.DoubleClick)
            {
                _armedAction = null;
                IssueAction(UnitActionKind.Unload, default, default, false);
            }
            else ActivateAction(UnitActionKind.UnloadAt);
        };
        _storedRow.AddChild(unload);
        foreach (var pose in cargo)
        {
            var type = _catalog.Units[pose.UnitType];
            var button = IconButton($"{pose.Health}/{type.MaxHealth}", UnitIcon(pose.UnitType));
            button.TooltipText = $"Вывести: {_catalog.Presentations[pose.UnitType].Name}";
            button.Pressed += () => _session!.Submit(CommandKind.Unload, entity: pose.Carrier, targetEntity: pose.Id);
            _storedRow.AddChild(button);
        }
    }

    private bool HandleTransportClick(InputEventMouseButton click, EntityPose? target)
    {
        if (_armedAction is not null || _armedCustom is not null || click.CtrlPressed || click.AltPressed ||
            click.ButtonIndex is not (MouseButton.Left or MouseButton.Right) || target is not { } carrier) return false;
        if (CanUnload(carrier))
        {
            _session!.Submit(CommandKind.Unload, entity: carrier.Id);
            return true;
        }
        return IssueAction(UnitActionKind.Enter, default, carrier.Id, click.ShiftPressed);
    }
}
