using System;
using System.Linq;
using System.Collections.Generic;
using Godot;
using OpenRTS.Core;
using OpenRTS.Core.Orders;
using OpenRTS.Godot.Controls;

public partial class Sandbox
{
    private (string Action, int Type)? _sitePlacement;
    private uint _constructionViewer = 1;
    private EntityId _selectedProject;
    private long _visibleProjectsTick = -1;
    private uint _visibleProjectsOwner;
    private ConstructionProjectPose[] _visibleProjects = [];
    private readonly List<ConstructionProjectPose> _visibleProjectBuffer = [];
    private NavigationGrid? _displayBaseGrid;
    private Button? _cancelProject;

    private void UpdateConstructionView(FrameExchange.FrameLease frame)
    {
        _displayBaseGrid = frame.BaseGrid;
        if (Selected() is { } selected && Controls(selected.Presentation.Owner))
            _constructionViewer = selected.Presentation.Owner;

        if (_visibleProjectsTick != frame.Tick || _visibleProjectsOwner != _constructionViewer)
        {
            _visibleProjectsTick = frame.Tick;
            _visibleProjectsOwner = _constructionViewer;
            _visibleProjectBuffer.Clear();
            foreach (var project in frame.ConstructionProjects)
                if (project.Owner == _constructionViewer) _visibleProjectBuffer.Add(project);
            foreach (var pose in frame.Poses)
                if (pose.Upgrade.Payer == _constructionViewer && pose.Upgrade is { Active: false, Project: { } variant })
                    _visibleProjectBuffer.Add(UpgradeGhost(pose, variant, _constructionViewer));
            _visibleProjects = _visibleProjectBuffer.ToArray();
        }
        if (_selection.Count > 0 || !_visibleProjects.Any(p => p.Id == _selectedProject)) _selectedProject = default;
        RefreshProjectButton();
    }

    private void RefreshProjectButton()
    {
        if (_cancelProject is null) return;
        _cancelProject.Visible = _selectedProject.Value != 0;
    }

    private ConstructionProjectPose? PickProject(WorldPosition point)
    {
        foreach (var project in _visibleProjects)
        {
            var type = _catalog!.Units[project.UnitType];
            if (type.Footprint is not { } footprint)
            {
                if (ObjectGeometry.WithinRadius(type, project.Position, point, 600)) return project;
                continue;
            }
            foreach (var cell in footprint.Cells)
            {
                var box = footprint.Bounds(cell, project.Position);
                if (point.X >= box.MinX && point.X < box.MaxX && point.Y >= box.MinY && point.Y < box.MaxY) return project;
            }
        }
        return null;
    }

    private void SelectProject(ConstructionProjectPose project)
    {
        _selection.Change([], SelectionChange.Replace);
        _selectedProject = project.Id;
        _notice = $"{_catalog!.Presentations[project.UnitType].Name} · {(project.Clearing ? "площадка освобождается" : "проект")}";
        RefreshSelection();
        RefreshProjectButton();
    }

    private void CancelProject()
    {
        if (_selectedProject.Value == 0) return;
        var project = _visibleProjects.FirstOrDefault(p => p.Id == _selectedProject);
        if (project.Upgrade is not null) CancelUpgrade(project.Id, project.Owner);
        else _session!.Submit(CommandKind.CancelConstruction, owner: _constructionViewer, targetEntity: _selectedProject);
        _selectedProject = default;
        RefreshProjectButton();
    }

    private bool CanPlanSite(int type, WorldPosition point)
    {
        var target = _catalog!.Units[type];
        if (_displayBaseGrid is { } grid && !target.Footprint!.AllowsConstruction(grid, point, target.Locomotor)) return false;
        foreach (var id in ActiveRecipients())
        {
            var builder = _selectionPoses[id];
            var definition = _catalog.Units[builder.UnitType];
            if (!builder.Construction.Active && (definition.Movable || WithinConstructionRadius(builder, point, definition.ConstructionRange)))
                return true;
        }
        return false;
    }

    private static bool WithinConstructionRadius(EntityPose builder, WorldPosition point, int radius)
    {
        long dx = point.X - (long)builder.Current.X, dy = point.Y - (long)builder.Current.Y;
        return dx * dx + dy * dy <= (long)radius * radius;
    }

    private EntityId[] BuildersFor(uint owner, WorldPosition position) => Controlled().Where(id =>
    {
        var pose = _selectionPoses[id];
        var type = _catalog!.Units[pose.UnitType];
        return pose.Presentation.Owner == owner && !pose.Construction.Active && type.ConstructionRange > 0 &&
            (type.Movable || WithinConstructionRadius(pose, position, type.ConstructionRange));
    }).ToArray();

    private bool HandleConstructionClick(InputEventMouseButton click, WorldPosition point, EntityPose? picked)
    {
        if (_sitePlacement is { } placement)
        {
            if (click.ButtonIndex == MouseButton.Right) { _sitePlacement = null; _panelDirty = true; return true; }
            if (click.ButtonIndex != MouseButton.Left) return true;
            var position = PlacementPoint(point, placement.Type);
            if (!CanPlanSite(placement.Type, position))
            {
                _notice = "Для проекта нужна подходящая площадка в радиусе строительства.";
                return true;
            }
            uint owner = Selected()?.Presentation.Owner ?? _constructionViewer;
            var ids = ActiveRecipients().Where(id => _selectionPoses[id].Presentation.Owner == owner).ToArray();
            if (ids.Length == 0) return true;
            _session!.Submit(CommandKind.PlanConstruction, position, recipients: new(ids), queued: click.ShiftPressed, action: placement.Action);
            _notice = "";
            if (!click.ShiftPressed) { _sitePlacement = null; _panelDirty = true; }
            return true;
        }
        if (_armedAction is not null || _armedCustom is not null || click.ButtonIndex != MouseButton.Right || click.CtrlPressed || click.AltPressed) return false;

        var project = PickProject(point);
        if (project is { } plan && plan.Upgrade is null && BuildersFor(plan.Owner, plan.Position) is { Length: > 0 } planners)
        {
            _session!.Submit(CommandKind.BuildSite, recipients: new(planners), targetEntity: plan.Id, queued: click.ShiftPressed);
            return true;
        }
        if (picked is { } building && building.Construction.Active && BuildersFor(building.Presentation.Owner, building.Current) is { Length: > 0 } builders)
        {
            _session!.Submit(CommandKind.BuildSite, recipients: new(builders), targetEntity: building.Id, queued: click.ShiftPressed);
            return true;
        }
        return false;
    }

    private bool HarvestTarget(EntityPose pose) => _catalog!.Units[pose.UnitType].Supply?.Role switch
    {
        SupplyRole.Mine => pose.Presentation.Owner == 0 || Ally(pose),
        SupplyRole.Depot => Ally(pose) && !pose.Construction.Active,
        _ => false
    };

    private AbilityDefinition? ContextSupplyAction(EntityPose pose)
    {
        if (_activeType < 0 || _activeRecipients.Length == 0) return null;
        return _catalog!.Units[_activeType].Abilities.FirstOrDefault(a =>
            a.Kind == AbilityKind.Harvest && HarvestTarget(pose));
    }

    private bool ContextSupply(EntityPose target)
    {
        if (ContextSupplyAction(target) is not { } ability || ActiveRecipients() is not { Length: > 0 } ids)
            return false;
        _session!.Submit(CommandKind.UseAbility, recipients: new(ids), targetEntity: target.Id, action: ability.Id);
        return true;
    }

    private string ConstructionStatus()
    {
        if (Selected() is not { } selected) return "";
        var supply = selected.Supply;
        var upgrade = selected.Upgrade;
        string progress = upgrade.Project is { } variant ? $"\n{UpgradeLabel(variant)} · {100 * upgrade.Work / upgrade.Required}%" : "";
        if (_catalog!.Units[selected.UnitType].Supply?.Role == SupplyRole.Truck)
        {
            string phase = !supply.Automatic ? "автодобыча выкл." : selected.Order.Phase switch
            {
                OrderPhase.Engaging => "обслуживание",
                OrderPhase.Waiting => "ожидание",
                OrderPhase.Approaching => "в пути",
                _ => "автодобыча"
            };
            return $"\nГруз {supply.Amount}/{supply.Capacity} · {phase}";
        }
        if (_catalog.Units[selected.UnitType].Supply?.Role == SupplyRole.Mine)
            return $"\nРуда {supply.Amount}/{supply.Capacity}{progress}";
        if (!selected.Construction.Active) return progress;
        var construction = selected.Construction;
        return $"\nСтроится · {100.0 * construction.Work / construction.Required:0}%";
    }
}
