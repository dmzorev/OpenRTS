using System;
using System.Linq;
using Godot;
using OpenRTS.Core;
using OpenRTS.Godot.Controls;

public partial class Sandbox
{
    private (string Action, int Type)? _sitePlacement;
    private uint _constructionViewer = 1;
    private EntityId _selectedProject;
    private long _visibleProjectsTick = -1;
    private uint _visibleProjectsOwner;
    private ConstructionProjectPose[] _visibleProjects = [];
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
            _visibleProjects = frame.ConstructionProjects.IsEmpty ? [] :
                frame.ConstructionProjects.ToArray().Where(p => p.Owner == _constructionViewer).ToArray();
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
            var footprint = _catalog!.Units[project.UnitType].Footprint!;
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
        _session!.Submit(CommandKind.CancelConstruction, owner: _constructionViewer, targetEntity: _selectedProject);
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
        if (project is { } plan && BuildersFor(plan.Owner, plan.Position) is { Length: > 0 } planners)
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

    private string ConstructionStatus()
    {
        if (Selected() is not { } selected || !selected.Construction.Active) return "";
        var progress = selected.Construction;
        return $"\nСтроится · {100.0 * progress.Work / progress.Required:0}%";
    }
}
