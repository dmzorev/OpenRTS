using System.Runtime.InteropServices;
using Frent;
using OpenRTS.Core.Simulation;
using OpenRTS.Core.Visibility;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private readonly FogOfWar? _fog;
    private List<VisionSource> _visionSources = [], _nextVisionSources = [];
    private readonly Dictionary<uint, HashSet<EntityId>> _commandVision = [];
    public FogOfWar? Fog => _fog;
    public ReadOnlySpan<VisionSource> VisionSources => CollectionsMarshal.AsSpan(_visionSources);

    /// <summary>Refresh on the simulation owner thread, including after initial placement.</summary>
    public void SynchronizeVisibility(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_failed) throw new InvalidOperationException("Incomplete world.");
        if (_fog is null) return;
        _fog.AdvanceTo(Tick);
        _nextVisionSources.Clear();
        for (int i = 0; i < Count; i++)
        {
            var e = _entityHandles[i]; var type = TypeOf(e);
            if (type.VisionRadius == 0 || !IsOnMap(e) || IsConstructing(e)) continue;
            var p = e.Get<SpatialState>().Position;
            _nextVisionSources.Add(new(e.Get<EntityIdentity>().Id, e.Get<OwnershipState>().Owner, p.X, p.Y, type.VisionRadius));
        }
        if (CollectionsMarshal.AsSpan(_visionSources).SequenceEqual(CollectionsMarshal.AsSpan(_nextVisionSources))) return;
        (_visionSources, _nextVisionSources) = (_nextVisionSources, _visionSources);
        _fog.Update(CollectionsMarshal.AsSpan(_visionSources), cancellationToken);
    }
    public bool IsVisible(uint viewer, EntityId id)
    {
        ThrowIfDisposed();
        if (!_indices.TryGetValue(id, out int index)) return _fog is not null && _fog.PlayerSlot(viewer) >= 0 && _construction?.OwnsProject(viewer, id) == true;
        if (_fog is null) return true;
        if (_fog.PlayerSlot(viewer) < 0) return false;
        var e = _entityHandles[index];
        if (e.Get<OwnershipState>().Owner == viewer) return true;
        if (!IsOnMap(e)) return false;
        var p = e.Get<SpatialState>().Position;
        var footprint = TypeOf(e).Footprint;
        if (footprint is not null && footprint.TargetBounds.Count > 0)
        {
            foreach (var bounds in footprint.TargetBounds)
            {
                var b = bounds.Translate(p);
                int left = Math.Max(0, b.MinX) / _fog.CellSize, top = Math.Max(0, b.MinY) / _fog.CellSize;
                int right = (Math.Min(Width, b.MaxX) - 1) / _fog.CellSize, bottom = (Math.Min(Depth, b.MaxY) - 1) / _fog.CellSize;
                for (int y = top; y <= bottom; y++) for (int x = left; x <= right; x++)
                    if (_fog.At(viewer, y * _fog.Columns + x) == CellVisibility.Visible) return true;
            }
            return false;
        }
        return _fog.At(viewer, p) == CellVisibility.Visible;
    }
    internal bool WasVisible(uint viewer, EntityId id) => _commandVision.TryGetValue(viewer, out var ids) && ids.Contains(id);

    private void CaptureCommandVision()
    {
        if (_fog is null) return;
        SynchronizeVisibility();
        foreach (uint p in _fog.Players)
        {
            if (!_commandVision.TryGetValue(p, out var ids)) _commandVision.Add(p, ids = []);
            ids.Clear();
            if (_construction is not null)
            {
                var projects = new ConstructionProjectPose[_construction.ProjectCount]; _construction.CopyProjects(projects);
                foreach (var project in projects) if (project.Owner == p) ids.Add(project.Id);
            }
            for (int i = 0; i < Count; i++)
            {
                var id = _entityHandles[i].Get<EntityIdentity>().Id;
                if (IsVisible(p, id)) ids.Add(id);
            }
        }
    }
    private bool CommandTargetVisible(WorldCommand command)
    {
        if (_fog is null || command.TargetEntity.Value == 0) return true;
        if (command.Issuer != 0) return _commandVision.TryGetValue(command.Issuer, out var ids) && ids.Contains(command.TargetEntity);
        // Unstamped direct actions still use the recipients' knowledge.
        if (command.Recipients is { } recipients)
        {
            foreach (var id in recipients.Ids)
                if (TryGetGameplayOwner(id, out uint owner) && (!_commandVision.TryGetValue(owner, out var seen) || !seen.Contains(command.TargetEntity))) return false;
            return true;
        }
        if (command.Kind == CommandKind.CancelConstruction && command.Owner != 0)
            return _commandVision.TryGetValue(command.Owner, out var projects) && projects.Contains(command.TargetEntity);
        return TryGetGameplayOwner(command.Entity, out uint player) && _commandVision.TryGetValue(player, out var known) && known.Contains(command.TargetEntity);
    }
    private void ForgetHiddenTargets()
    {
        if (_fog is null) return;
        for (int i = 0; i < Count; i++)
        {
            var e = _entityHandles[i]; uint owner = e.Get<OwnershipState>().Owner;
            ref var orders = ref e.Get<OrderState>(); ref var visual = ref e.Get<PresentationState>();
            if (orders.HasOrder && orders.ActiveOrder.Target.Value != 0 && !IsVisible(owner, orders.ActiveOrder.Target))
            { orders.HasOrder = false; orders.CombatTarget = default; orders.OrderPhase = OrderPhase.Idle; Halt(i); CancelWindups(i); }
            if (orders.CombatTarget.Value != 0 && !IsVisible(owner, orders.CombatTarget)) { orders.CombatTarget = default; CancelWindups(i); }
            if (visual.AimEntity.Value != 0 && !IsVisible(owner, visual.AimEntity)) { visual.HasAim = false; visual.AimEntity = default; }
        }
    }
    private void ValidateVisionOwner(uint owner)
    {
        if (_fog is not null && owner != 0 && _fog.PlayerSlot(owner) < 0) throw new ArgumentException("Fog requires an explicitly configured player.", nameof(owner));
    }
}
