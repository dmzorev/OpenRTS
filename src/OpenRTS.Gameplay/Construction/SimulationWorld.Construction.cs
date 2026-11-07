using OpenRTS.Core.Orders;
using OpenRTS.Core.Simulation;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private const int MaximumPlayerProjects = 256;
    private const int ClearanceTimeout = 250;
    private readonly List<ConstructionProject> _siteReservations = [];
    private readonly List<int> _siteOccupants = [];
    private readonly List<EntityId> _activeBuilders = [];
    private readonly List<EntityId> _siteCandidates = [];
    private readonly List<(WorldPosition Position, BodyShape Body)> _evacuationGoals = [];

    private bool IsUnderConstruction(Frent.Entity actor) => _hasBuilders && actor.Has<ConstructionState>();

    private void ApplyConstruction(WorldCommand command)
    {
        if (command.Kind == CommandKind.CancelConstruction)
        {
            if (_players.TryGetValue(command.Owner, out var player) && player.Projects.TryGetValue(command.TargetEntity.Value, out var project))
                RemoveProject(project, cancelled: true);
            else RejectAction();
            return;
        }

        var recipients = Recipients(command);
        if (command.Kind == CommandKind.BuildSite)
        {
            foreach (int index in recipients) IssueOrder(index, new(UnitActionKind.Build, default, command.TargetEntity), command.Queued);
            return;
        }

        ConstructionProject? plan = null;
        foreach (int index in recipients)
        {
            var actor = _entityHandles[index];
            var identity = actor.Get<EntityIdentity>();
            var type = _types[identity.Type];
            uint owner = actor.Get<OwnershipState>().Owner;
            if (owner == 0 || IsUnderConstruction(actor)) continue;
            int product = ProductIndex(identity.Type, command.Action);
            if (product < 0 || !type.Products[product].OnSite) continue;
            if (plan is null)
            {
                int target = _productTypes[identity.Type][product];
                var recipe = _builds[identity.Type][product];
                var position = GroundPosition(command.Position, command.Position.Elevation);
                if (!SiteTerrainAllows(target, position) ||
                    !type.Movable && DistanceSquared(actor.Get<SpatialState>().Position, position) > (long)type.ConstructionRange * type.ConstructionRange)
                    continue;

                var projects = Player(owner).Projects;
                var replaced = projects.Values.Where(p => SitesOverlap(target, position, p.UnitType, p.Position)).ToArray();
                if (projects.Count - replaced.Length >= MaximumPlayerProjects) continue;
                var id = AllocateEntityId();
                foreach (var old in replaced) RemoveProject(old, cancelled: true);
                plan = new(id, owner, target, position, recipe);
                projects.Add(id.Value, plan);
            }
            if (owner == plan.Owner && _productTypes[identity.Type][product] == plan.UnitType)
                IssueOrder(index, new(UnitActionKind.Build, default, plan.Id), command.Queued);
        }
        if (plan is null) RejectAction();
    }

    private bool TryBuildTarget(int index, EntityId id, out ConstructionProject? project, out int foundation)
    {
        var actor = _entityHandles[index];
        uint owner = actor.Get<OwnershipState>().Owner;
        foundation = -1;
        project = null;
        var type = TypeOf(actor);
        if (owner == 0 || IsUnderConstruction(actor) || type.ConstructionRange == 0) return false;
        if (Player(owner).Projects.TryGetValue(id.Value, out project))
            return type.Movable || BuildInRange(actor, project.UnitType, project.Position);
        if (!_indices.TryGetValue(id, out foundation)) return false;
        var target = _entityHandles[foundation];
        return IsUnderConstruction(target) && target.Get<OwnershipState>().Owner == owner &&
            (type.Movable || BuildInRange(actor, target.Get<EntityIdentity>().Type, target.Get<SpatialState>().Position));
    }

    private bool BuildInRange(Frent.Entity builder, int target, WorldPosition site)
    {
        var type = TypeOf(builder);
        var position = builder.Get<SpatialState>().Position;
        long distance = type.Movable
            ? ObjectGeometry.DistanceSquared(type, position, _types[target], site, out _, out _)
            : DistanceSquared(position, site);
        return distance <= (long)type.ConstructionRange * type.ConstructionRange;
    }

    private void UpdateBuildOrder(int index, UnitOrder order)
    {
        var builder = _entityHandles[index];
        ref var state = ref builder.Get<OrderState>();
        if (!TryBuildTarget(index, order.Target, out var project, out int foundation))
        {
            state.HasOrder = false;
            state.OrderPhase = OrderPhase.Idle;
            Halt(index);
            return;
        }
        _activeBuilders.Add(builder.Get<EntityIdentity>().Id);
        int target = project?.UnitType ?? _entityHandles[foundation].Get<EntityIdentity>().Type;
        var site = project?.Position ?? _entityHandles[foundation].Get<SpatialState>().Position;
        var position = builder.Get<SpatialState>().Position;
        var type = TypeOf(builder);
        if (project is { ReservedAt: > 0 } && SiteTouches(target, site, position, position, type.Body))
        {
            state.OrderPhase = OrderPhase.Waiting;
            return; // Preserve the movement assigned by evacuation.
        }
        if (BuildInRange(builder, target, site))
        {
            Halt(index);
            state.OrderPhase = OrderPhase.Engaging;
            return;
        }
        if (type.Movable && !builder.Get<MotionState>().Moving && (state.OrderStarted == Tick || (Tick + order.Target.Value) % 8 == 0))
        {
            var footprint = _types[target].Footprint!;
            foreach (var point in SiteExits(footprint, site, position, type.Body).OrderBy(p => DistanceSquared(position, p)))
                if (StopAllowed(builder.Get<EntityIdentity>().Type, point) && SetTarget(index, point) && builder.Get<MotionState>().Moving) break;
        }
        state.OrderPhase = builder.Get<MotionState>().Moving ? OrderPhase.Approaching : OrderPhase.Waiting;
    }

    private void UpdateConstruction()
    {
        // A clearing site releases its temporary reservation when nobody is actively starting it.
        for (int i = _siteReservations.Count - 1; i >= 0; i--)
        {
            var project = _siteReservations[i];
            if (project.LastAttempt >= Tick - 1) continue;
            project.ReservedAt = 0;
            _siteReservations.RemoveAt(i);
        }
        foreach (var id in _activeBuilders)
        {
            if (!_indices.TryGetValue(id, out int index)) continue;
            var actor = _entityHandles[index];
            var orders = actor.Get<OrderState>();
            if (!orders.HasOrder || orders.ActiveOrder.Kind != UnitActionKind.Build || IsStored(actor) ||
                !TryBuildTarget(index, orders.ActiveOrder.Target, out var project, out int foundation)) continue;

            int target = project?.UnitType ?? _entityHandles[foundation].Get<EntityIdentity>().Type;
            var position = project?.Position ?? _entityHandles[foundation].Get<SpatialState>().Position;
            if (!BuildInRange(actor, target, position)) continue;
            // Low power slows fixed builders, just like the legacy production queues.
            int work = ReadConditions(TypeOf(actor), actor.Get<OwnershipState>().Owner).HasFlag(GameplayConditions.LowPower) ? 1 : 2;
            if (project is not null)
            {
                if (project.LastAttempt == Tick) continue;
                project.LastAttempt = Tick;
                if (!TryStartConstruction(project, out foundation)) continue;
            }
            if (_entityHandles[foundation].Get<ConstructionState>().StartedAt < Tick) AdvanceConstruction(foundation, work);
        }
    }

    private bool TryStartConstruction(ConstructionProject project, out int foundation)
    {
        foundation = -1;
        if (!SiteTerrainAllows(project.UnitType, project.Position) ||
            _siteReservations.Any(p => p != project && SitesOverlap(project.UnitType, project.Position, p.UnitType, p.Position)))
        {
            RemoveProject(project, cancelled: true);
            return false;
        }

        _siteOccupants.Clear();
        var footprint = _types[project.UnitType].Footprint!;
        var center = project.Position;
        (_motion ?? _combatIndex)!.Query(new(center.X - footprint.HalfWidth, center.Y - footprint.HalfHeight),
            new(center.X + footprint.HalfWidth, center.Y + footprint.HalfHeight), 0, _siteCandidates);
        foreach (var id in _siteCandidates)
        {
            int i = _indices[id];
            var actor = _entityHandles[i];
            if (IsStored(actor)) continue;
            int typeIndex = actor.Get<EntityIdentity>().Type;
            var type = _types[typeIndex];
            var position = actor.Get<SpatialState>().Position;
            bool intersects = type.Footprint is not null
                ? SitesOverlap(project.UnitType, project.Position, typeIndex, position)
                : SiteTouches(project.UnitType, project.Position, position, position, type.Body);
            if (!intersects) continue;
            uint owner = actor.Get<OwnershipState>().Owner;
            if (!type.Movable || owner != 0 && owner != project.Owner)
            {
                RemoveProject(project, cancelled: true);
                return false;
            }
            _siteOccupants.Add(i);
        }

        if (_siteOccupants.Count > 0)
        {
            if (project.ReservedAt == 0)
            {
                if (Player(project.Owner).Credits < project.Recipe.Cost) return false;
                project.ReservedAt = Tick;
                _siteReservations.Add(project);
                _siteOccupants.Sort((a, b) => _entityHandles[a].Get<EntityIdentity>().Id.Value.CompareTo(_entityHandles[b].Get<EntityIdentity>().Id.Value));
                _evacuationGoals.Clear();
                foreach (int index in _siteOccupants) Evacuate(index, project);
            }
            else if (Tick - project.ReservedAt >= ClearanceTimeout) RemoveProject(project, cancelled: true);
            return false;
        }
        if (!PrepareFootprint(project.UnitType, project.Position, out var grid))
        {
            RemoveProject(project, cancelled: true);
            return false;
        }
        if (!Spend(project.Owner, project.Recipe.Cost))
        {
            if (project.ReservedAt > 0)
            {
                project.ReservedAt = 0;
                _siteReservations.Remove(project);
            }
            return false;
        }
        var typeDefinition = _types[project.UnitType];
        int health = Math.Max(1, (int)((long)typeDefinition.MaxHealth * project.Recipe.InitialHealthPercent / 100));
        Spawn(project.UnitType, project.Position, project.Owner, siteId: project.Id, construction: new(project.Recipe.Ticks * 2, health, Tick));
        foundation = _indices[project.Id];
        RemoveProject(project, cancelled: false);
        if (grid is not null) CommitGrid(grid);
        _events.Add(new(Tick, WorldEventKind.ConstructionStarted, project.Id, project.Position, project.UnitType, Owner: project.Owner));
        return true;
    }

    private void AdvanceConstruction(int index, int work)
    {
        var actor = _entityHandles[index];
        var type = TypeOf(actor);
        ref var site = ref actor.Get<ConstructionState>();
        int before = site.Work;
        site.Work = Math.Min(site.Required, before + work);
        long healing = type.MaxHealth - site.StartingHealth;
        int increment = (int)(healing * site.Work / site.Required - healing * before / site.Required);
        ref var health = ref actor.Get<HealthState>().Current;
        health = (int)Math.Min(type.MaxHealth, (long)health + increment);
        if (site.Work < site.Required) return;
        actor.Remove<ConstructionState>();
        if (type.Economy is { Income: > 0 } economy) actor.Add(new IncomeState(checked(Tick + economy.Interval)));
        _powerDirty = true;
        var identity = actor.Get<EntityIdentity>();
        _events.Add(new(Tick, WorldEventKind.ConstructionCompleted, identity.Id, actor.Get<SpatialState>().Position, identity.Type, Owner: actor.Get<OwnershipState>().Owner));
    }

    private void RemoveProject(ConstructionProject project, bool cancelled)
    {
        Player(project.Owner).Projects.Remove(project.Id.Value);
        if (project.ReservedAt > 0) _siteReservations.Remove(project);
        if (cancelled)
        {
            ReleaseEntityId(project.Id);
            _events.Add(new(Tick, WorldEventKind.ConstructionCancelled, project.Id, project.Position, project.UnitType, Owner: project.Owner));
        }
    }

    private bool SiteTerrainAllows(int typeIndex, WorldPosition position)
    {
        var type = _types[typeIndex];
        var footprint = type.Footprint!;
        if (!FootprintFits(footprint, position)) return false;
        return _baseGrid is { } grid ? footprint.AllowsConstruction(grid, position, type.Locomotor) : type.Locomotor.Rules[0].CanStop;
    }

    private bool SitesOverlap(int typeA, WorldPosition a, int typeB, WorldPosition b)
    {
        var first = _types[typeA].Footprint!;
        var second = _types[typeB].Footprint!;
        if (Math.Abs(a.X - b.X) >= first.HalfWidth + second.HalfWidth ||
            Math.Abs(a.Y - b.Y) >= first.HalfHeight + second.HalfHeight) return false;
        foreach (var firstCell in first.Cells)
        {
            var left = first.Bounds(firstCell, a);
            foreach (var secondCell in second.Cells)
            {
                var right = second.Bounds(secondCell, b);
                if (left.MinX < right.MaxX && right.MinX < left.MaxX && left.MinY < right.MaxY && right.MinY < left.MaxY) return true;
            }
        }
        return false;
    }

    private bool SiteTouches(int type, WorldPosition site, WorldPosition from, WorldPosition to, BodyShape body)
    {
        var footprint = _types[type].Footprint!;
        var bounds = new WorldBox(site.X - footprint.HalfWidth, site.Y - footprint.HalfHeight, site.X + footprint.HalfWidth, site.Y + footprint.HalfHeight);
        if (!BodyGeometry.SweptBox(from, to, bounds, body)) return false;
        foreach (var cell in footprint.Cells)
            if (BodyGeometry.SweptBox(from, to, footprint.Bounds(cell, site), body)) return true;
        return false;
    }

    private bool SiteMovementClear(WorldPosition from, WorldPosition to, BodyShape body)
    {
        foreach (var site in _siteReservations)
            if (SiteTouches(site.UnitType, site.Position, from, to, body) && !SiteTouches(site.UnitType, site.Position, from, from, body))
                return false;
        return true;
    }

    private bool SiteStopClear(WorldPosition point, BodyShape body)
    {
        foreach (var site in _siteReservations)
            if (SiteTouches(site.UnitType, site.Position, point, point, body)) return false;
        return true;
    }

    private IEnumerable<WorldPosition> SiteExits(FootprintDefinition footprint, WorldPosition site, WorldPosition from, BodyShape body)
    {
        int left = site.X - footprint.HalfWidth - body.HalfWidth - 1;
        int right = site.X + footprint.HalfWidth + body.HalfWidth + 1;
        int top = site.Y - footprint.HalfHeight - body.HalfHeight - 1;
        int bottom = site.Y + footprint.HalfHeight + body.HalfHeight + 1;
        int x = Math.Clamp(from.X, left, right), y = Math.Clamp(from.Y, top, bottom);
        WorldPosition[] candidates = [new(left, y), new(right, y), new(x, top), new(x, bottom)];
        return candidates.Where(p => p.X >= body.HalfWidth && p.Y >= body.HalfHeight &&
            p.X <= Width - body.HalfWidth && p.Y <= Depth - body.HalfHeight);
    }

    private void Evacuate(int index, ConstructionProject project)
    {
        var actor = _entityHandles[index];
        var identity = actor.Get<EntityIdentity>();
        var type = _types[identity.Type];
        var position = actor.Get<SpatialState>().Position;
        var footprint = _types[project.UnitType].Footprint!;
        foreach (var point in SiteExits(footprint, project.Position, position, type.Body).OrderBy(p => DistanceSquared(position, p)))
        {
            if (!StopAllowed(identity.Type, point) ||
                !(_motion ?? _combatIndex)!.ClearPlacement(point, out _, type.Body) ||
                _evacuationGoals.Any(goal => BodyGeometry.Overlap(point, point, type.Body, goal.Position, goal.Position, goal.Body)) ||
                _navigation is not null && !TerrainGeometry.Clear(_navigation.Grid, position, point, type.Body, type.Locomotor) ||
                !SetTarget(index, point)) continue;
            _evacuationGoals.Add((point, type.Body));
            ref var orders = ref actor.Get<OrderState>();
            if (orders.HasOrder && orders.ActiveOrder.Kind == UnitActionKind.Build && orders.ActiveOrder.Target == project.Id) return;
            CancelWindups(index);
            orders.ActiveOrder = new(UnitActionKind.Move, point);
            orders.HasOrder = true;
            orders.OrderPhase = OrderPhase.Moving;
            orders.CombatTarget = default;
            orders.PendingOrders?.Clear();
            return;
        }
    }

    public int ConstructionProjectCount => _players.Values.Sum(p => p.Projects.Count);

    public void CopyConstructionProjects(Span<ConstructionProjectPose> destination)
    {
        ThrowIfDisposed();
        if (_failed) throw new InvalidOperationException("An interrupted world cannot publish a frame.");
        if (destination.Length < ConstructionProjectCount) throw new ArgumentException("Destination is too small.", nameof(destination));
        int at = 0;
        foreach (var player in _players.Values)
            foreach (var project in player.Projects.Values)
                destination[at++] = new(project.Id, project.Owner, project.UnitType, project.Position, project.ReservedAt > 0);
    }

    private void WriteConstructionProjects(BinaryWriter writer)
    {
        writer.Write(ConstructionProjectCount);
        foreach (var player in _players.Values)
            foreach (var project in player.Projects.Values)
            {
                writer.Write(project.Id.Value);
                writer.Write(project.Owner);
                writer.Write(project.UnitType);
                writer.Write(project.Position.X);
                writer.Write(project.Position.Y);
                writer.Write(project.Position.Elevation);
                project.Recipe.Write(writer);
                writer.Write(project.ReservedAt);
                writer.Write(project.LastAttempt);
            }
    }
}
