using Frent;
using OpenRTS.Core.Orders;
using OpenRTS.Core.Simulation;

namespace OpenRTS.Core.Construction;

internal sealed class ConstructionSystem
{
    private const int MaximumPlayerProjects = 256;
    private const int ClearanceTimeout = 250;
    private readonly SortedDictionary<uint, SortedDictionary<uint, ConstructionProject>> _constructionProjects = new();
    private readonly List<ConstructionProject> _siteReservations = [];
    private readonly List<Entity> _siteOccupants = [];
    private readonly List<EntityId> _activeBuilders = [];
    private readonly List<EntityId> _idleBuilders = [];
    private readonly List<EntityId> _siteCandidates = [];
    private readonly List<(WorldPosition Position, BodyShape Body)> _evacuationGoals = [];
    private readonly IConstructionWorld _world;
    private readonly IConstructionMechanic _mechanic;
    private readonly Action<Entity> _clearFoundation;
    private int _foundationCount;

    internal ConstructionSystem(IConstructionWorld world, IConstructionMechanic mechanic)
    {
        _world = world;
        _mechanic = mechanic;
        _clearFoundation = ClearFoundation;
    }

    internal bool IsFoundation(Entity entity) => _mechanic.IsFoundation(entity);
    internal bool HasReservations => _siteReservations.Count != 0;
    internal void BeginOrders()
    {
        _activeBuilders.Clear();
        _idleBuilders.Clear();
    }

    internal void AddIdleBuilder(EntityId id)
    {
        if (_foundationCount > 0) _idleBuilders.Add(id);
    }

    internal void BeforeRemove(Entity actor)
    {
        if (IsFoundation(actor)) _foundationCount--;
    }

    internal static void Apply(ConstructionSystem? system, IConstructionWorld world, WorldCommand command)
    {
        if (command.Kind == CommandKind.CancelConstruction)
        {
            if (system?.Cancel(command.Owner, command.TargetEntity) != true)
                world.Emit(new(world.Tick, WorldEventKind.ActionUnavailable, default));
            return;
        }

        var recipients = world.Recipients(command);
        if (command.Kind == CommandKind.BuildSite)
        {
            foreach (var actor in recipients)
                world.IssueOrder(actor.Get<EntityIdentity>().Id, new(UnitActionKind.Build, default, command.TargetEntity), command.Queued);
            return;
        }

        if (system?.Plan(command, recipients) != true)
            world.Emit(new(world.Tick, WorldEventKind.ActionUnavailable, default));
    }

    private bool Cancel(uint owner, EntityId target)
    {
        if (!_constructionProjects.TryGetValue(owner, out var projects) || !projects.TryGetValue(target.Value, out var project))
            return false;
        RemoveProject(project, cancelled: true);
        return true;
    }

    private bool Plan(WorldCommand command, IEnumerable<Entity> recipients)
    {
        ConstructionProject? plan = null;
        foreach (var actor in recipients)
        {
            var identity = actor.Get<EntityIdentity>();
            var type = _world.TypeOf(identity.Type);
            uint owner = actor.Get<OwnershipState>().Owner;
            if (owner == 0 || IsFoundation(actor) || command.Queued && !_world.CanQueueOrder(actor)) continue;
            if (!_world.TryGetRecipe(actor, command.Action, out int target, out var recipe)) continue;
            if (plan is null)
            {
                var position = _world.GroundPosition(command.Position);
                if (!_world.SiteTerrainAllows(target, position) ||
                    !type.Movable && ObjectGeometry.Squared(actor.Get<SpatialState>().Position, position) > (long)type.ConstructionRange * type.ConstructionRange)
                    continue;

                if (!_constructionProjects.TryGetValue(owner, out var projects))
                    _constructionProjects.Add(owner, projects = new());
                var replaced = projects.Values.Where(p => SitesOverlap(target, position, p.UnitType, p.Position)).ToArray();
                if (projects.Count - replaced.Length >= MaximumPlayerProjects) continue;
                var id = _world.ReserveEntityId();
                foreach (var old in replaced) RemoveProject(old, cancelled: true);
                plan = new(id, owner, target, position, recipe);
                projects.Add(id.Value, plan);
            }
            if (owner == plan.Owner && target == plan.UnitType)
                _world.IssueOrder(identity.Id, new(UnitActionKind.Build, default, plan.Id), command.Queued);
        }
        return plan is not null;
    }

    internal bool CanBuild(Entity actor, EntityId id) => TryBuildTarget(actor, id, out _, out _);

    private UnitDefinition TypeOf(Entity actor) => _world.TypeOf(actor.Get<EntityIdentity>().Type);

    private bool TryBuildTarget(Entity actor, EntityId id, out ConstructionProject? project, out Entity foundation)
    {
        uint owner = actor.Get<OwnershipState>().Owner;
        foundation = default;
        project = null;
        var type = TypeOf(actor);
        if (owner == 0 || IsFoundation(actor) || type.ConstructionRange == 0) return false;
        if (_constructionProjects.TryGetValue(owner, out var projects) && projects.TryGetValue(id.Value, out project))
            return type.Movable || BuildInRange(actor, project.UnitType, project.Position);
        if (!_world.TryGetEntity(id, out foundation)) return false;
        return IsFoundation(foundation) && foundation.Get<OwnershipState>().Owner == owner &&
            (type.Movable || BuildInRange(actor, foundation.Get<EntityIdentity>().Type, foundation.Get<SpatialState>().Position));
    }

    private bool BuildInRange(Entity builder, int target, WorldPosition site)
    {
        var type = TypeOf(builder);
        var position = builder.Get<SpatialState>().Position;
        long distance = type.Movable
            ? ObjectGeometry.DistanceSquared(type, position, _world.TypeOf(target), site, out _, out _)
            : ObjectGeometry.Squared(position, site);
        return distance <= (long)type.ConstructionRange * type.ConstructionRange;
    }

    internal void UpdateOrder(Entity builder, UnitOrder order)
    {
        var identity = builder.Get<EntityIdentity>();
        ref var state = ref builder.Get<OrderState>();
        if (!TryBuildTarget(builder, order.Target, out var project, out var foundation))
        {
            state.HasOrder = false;
            state.OrderPhase = OrderPhase.Idle;
            _world.Halt(identity.Id);
            return;
        }
        _activeBuilders.Add(identity.Id);
        int target = project?.UnitType ?? foundation.Get<EntityIdentity>().Type;
        var site = project?.Position ?? foundation.Get<SpatialState>().Position;
        var position = builder.Get<SpatialState>().Position;
        var type = TypeOf(builder);
        if (project is { ReservedAt: > 0 } && SiteTouches(target, site, position, position, type.Body))
        {
            state.OrderPhase = OrderPhase.Waiting;
            return; // Preserve the movement assigned by evacuation.
        }
        if (BuildInRange(builder, target, site))
        {
            _world.Halt(identity.Id);
            state.OrderPhase = OrderPhase.Engaging;
            return;
        }
        if (type.Movable && !builder.Get<MotionState>().Moving && (state.OrderStarted == _world.Tick || (_world.Tick + order.Target.Value) % 8 == 0))
        {
            var footprint = _world.TypeOf(target).Footprint!;
            foreach (var point in ConstructionGeometry.Exits(footprint, site, position, type.Body, _world.Width, _world.Depth).OrderBy(p => ObjectGeometry.Squared(position, p)))
                if (_world.StopAllowed(identity.Type, point) && _world.TrySetTarget(identity.Id, point) && builder.Get<MotionState>().Moving) break;
        }
        state.OrderPhase = builder.Get<MotionState>().Moving ? OrderPhase.Approaching : OrderPhase.Waiting;
    }

    internal void Advance()
    {
        // A clearing site releases its temporary reservation when nobody is actively starting it.
        for (int i = _siteReservations.Count - 1; i >= 0; i--)
        {
            var project = _siteReservations[i];
            if (project.LastAttempt >= _world.Tick - 1) continue;
            project.ReservedAt = 0;
            _siteReservations.RemoveAt(i);
        }
        foreach (var id in _activeBuilders)
        {
            if (!_world.TryGetEntity(id, out var actor)) continue;
            var orders = actor.Get<OrderState>();
            if (!orders.HasOrder || orders.ActiveOrder.Kind != UnitActionKind.Build || !_world.IsOnMap(actor) ||
                !TryBuildTarget(actor, orders.ActiveOrder.Target, out var project, out var foundation)) continue;

            int target = project?.UnitType ?? foundation.Get<EntityIdentity>().Type;
            var position = project?.Position ?? foundation.Get<SpatialState>().Position;
            if (!BuildInRange(actor, target, position)) continue;
            var conditions = _world.ReadConditions(actor);
            if (project is not null)
            {
                if (project.LastAttempt == _world.Tick) continue;
                project.LastAttempt = _world.Tick;
                if (!TryStartConstruction(project, out foundation)) continue;
            }
            AdvanceConstruction(foundation, conditions);
        }
        foreach (var id in _idleBuilders)
        {
            if (_foundationCount == 0) break;
            if (!_world.TryGetEntity(id, out var actor) || !_world.IsOnMap(actor)) continue;
            var orders = actor.Get<OrderState>();
            uint owner = actor.Get<OwnershipState>().Owner;
            if (orders.HasOrder || orders.CombatTarget != default || actor.Get<MotionState>().Moving || owner == 0) continue;

            var type = TypeOf(actor);
            var position = actor.Get<SpatialState>().Position;
            int radius = type.ConstructionRange + Math.Max(type.Body.HalfWidth, type.Body.HalfHeight);
            _world.QuerySite(new(position.X - radius, position.Y - radius),
                new(position.X + radius, position.Y + radius), _siteCandidates);
            Entity foundation = default;
            uint bestId = uint.MaxValue;
            foreach (var candidate in _siteCandidates)
            {
                if (candidate.Value > bestId || !_world.TryGetEntity(candidate, out var site) || !IsFoundation(site) ||
                    site.Get<OwnershipState>().Owner != owner ||
                    !BuildInRange(actor, site.Get<EntityIdentity>().Type, site.Get<SpatialState>().Position)) continue;
                foundation = site;
                bestId = candidate.Value;
            }
            if (foundation != default) AdvanceConstruction(foundation, _world.ReadConditions(actor));
        }
    }

    private bool TryStartConstruction(ConstructionProject project, out Entity foundation)
    {
        foundation = default;
        if (!_world.SiteTerrainAllows(project.UnitType, project.Position) ||
            _siteReservations.Any(p => p != project && SitesOverlap(project.UnitType, project.Position, p.UnitType, p.Position)))
        {
            RemoveProject(project, cancelled: true);
            return false;
        }

        _siteOccupants.Clear();
        var footprint = _world.TypeOf(project.UnitType).Footprint!;
        var center = project.Position;
        _world.QuerySite(new(center.X - footprint.HalfWidth, center.Y - footprint.HalfHeight),
            new(center.X + footprint.HalfWidth, center.Y + footprint.HalfHeight), _siteCandidates);
        foreach (var id in _siteCandidates)
        {
            if (!_world.TryGetEntity(id, out var actor) || !_world.IsOnMap(actor)) continue;
            int typeIndex = actor.Get<EntityIdentity>().Type;
            var type = _world.TypeOf(typeIndex);
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
            _siteOccupants.Add(actor);
        }

        if (_siteOccupants.Count > 0)
        {
            if (project.ReservedAt == 0)
            {
                if (!_world.CanAfford(project.Owner, project.Recipe.Cost)) return false;
                project.ReservedAt = _world.Tick;
                _siteReservations.Add(project);
                _siteOccupants.Sort(static (a, b) => a.Get<EntityIdentity>().Id.Value.CompareTo(b.Get<EntityIdentity>().Id.Value));
                _evacuationGoals.Clear();
                foreach (var actor in _siteOccupants) Evacuate(actor, project);
            }
            else if (_world.Tick - project.ReservedAt >= ClearanceTimeout) RemoveProject(project, cancelled: true);
            return false;
        }
        if (!_world.PrepareFootprint(project.UnitType, project.Position, out var grid))
        {
            RemoveProject(project, cancelled: true);
            return false;
        }
        if (!_world.Spend(project.Owner, project.Recipe.Cost))
        {
            if (project.ReservedAt > 0)
            {
                project.ReservedAt = 0;
                _siteReservations.Remove(project);
            }
            return false;
        }
        var typeDefinition = _world.TypeOf(project.UnitType);
        var start = _mechanic.PlanFoundation(typeDefinition, project.Recipe);
        if (start.Health < 1 || start.Health > typeDefinition.MaxHealth || start.RequiredWork < 1)
            throw new InvalidOperationException("Construction returned invalid foundation health or work.");
        foundation = _world.SpawnFoundation(project.Id, project.UnitType, project.Position, project.Owner, start.Health,
            entity =>
            {
                _mechanic.Initialize(entity, start, _world.Tick);
                if (!_mechanic.IsFoundation(entity)) throw new InvalidOperationException("Construction did not initialize the foundation.");
            });
        _foundationCount++;
        RemoveProject(project, cancelled: false);
        if (grid is not null) _world.CommitFootprint(grid);
        _world.Emit(new(_world.Tick, WorldEventKind.ConstructionStarted, project.Id, project.Position, project.UnitType, Owner: project.Owner));
        return true;
    }

    private void AdvanceConstruction(Entity actor, GameplayConditions conditions)
    {
        var type = TypeOf(actor);
        ref var health = ref actor.Get<HealthState>().Current;
        var step = _mechanic.Advance(new(actor, type, health, conditions, _world.Tick));
        if (step.Health < 1 || step.Health > type.MaxHealth)
            throw new InvalidOperationException("Construction returned invalid health.");
        health = step.Health;
        if (!step.Complete) return;
        _world.CompleteFoundation(actor, _clearFoundation);
        _foundationCount--;
        var identity = actor.Get<EntityIdentity>();
        _world.Emit(new(_world.Tick, WorldEventKind.ConstructionCompleted, identity.Id, actor.Get<SpatialState>().Position, identity.Type, Owner: actor.Get<OwnershipState>().Owner));
    }

    private void ClearFoundation(Entity entity)
    {
        _mechanic.Complete(entity);
        if (_mechanic.IsFoundation(entity)) throw new InvalidOperationException("Construction did not clear the completed foundation.");
    }

    private void RemoveProject(ConstructionProject project, bool cancelled)
    {
        _constructionProjects[project.Owner].Remove(project.Id.Value);
        if (project.ReservedAt > 0) _siteReservations.Remove(project);
        if (cancelled)
        {
            _world.ReleaseEntityId(project.Id);
            _world.Emit(new(_world.Tick, WorldEventKind.ConstructionCancelled, project.Id, project.Position, project.UnitType, Owner: project.Owner));
        }
    }

    private bool SitesOverlap(int typeA, WorldPosition a, int typeB, WorldPosition b) =>
        ConstructionGeometry.Overlaps(_world.TypeOf(typeA).Footprint!, a, _world.TypeOf(typeB).Footprint!, b);

    private bool SiteTouches(int type, WorldPosition site, WorldPosition from, WorldPosition to, BodyShape body) =>
        ConstructionGeometry.Touches(_world.TypeOf(type).Footprint!, site, from, to, body);

    internal bool SiteMovementClear(WorldPosition from, WorldPosition to, BodyShape body)
    {
        foreach (var site in _siteReservations)
            if (SiteTouches(site.UnitType, site.Position, from, to, body) && !SiteTouches(site.UnitType, site.Position, from, from, body))
                return false;
        return true;
    }

    internal bool SiteStopClear(WorldPosition point, BodyShape body)
    {
        foreach (var site in _siteReservations)
            if (SiteTouches(site.UnitType, site.Position, point, point, body)) return false;
        return true;
    }

    private void Evacuate(Entity actor, ConstructionProject project)
    {
        var identity = actor.Get<EntityIdentity>();
        var type = _world.TypeOf(identity.Type);
        var position = actor.Get<SpatialState>().Position;
        var footprint = _world.TypeOf(project.UnitType).Footprint!;
        var orders = actor.Get<OrderState>();
        bool preserveOrder = orders.HasOrder && orders.ActiveOrder.Kind == UnitActionKind.Build && orders.ActiveOrder.Target == project.Id;
        foreach (var point in ConstructionGeometry.Exits(footprint, project.Position, position, type.Body, _world.Width, _world.Depth)
            .OrderBy(p => ObjectGeometry.Squared(position, p)))
        {
            if (_evacuationGoals.Any(goal => BodyGeometry.Overlap(point, point, type.Body, goal.Position, goal.Position, goal.Body)) ||
                !_world.TryEvacuate(identity.Id, point, preserveOrder)) continue;
            _evacuationGoals.Add((point, type.Body));
            return;
        }
    }

    internal ConstructionProgress Project(Entity entity, UnitDefinition type, ref uint visualStates)
    {
        if (!IsFoundation(entity)) return default;
        if (type.States.Contains("constructing")) visualStates |= type.StateMask("constructing");
        return _mechanic.GetProgress(entity);
    }

    internal static void WriteFoundation(ConstructionSystem? system, BinaryWriter writer, Entity entity)
    {
        bool constructing = system?.IsFoundation(entity) == true;
        writer.Write(constructing);
        if (constructing) system!._mechanic.WriteFoundationState(writer, entity);
    }

    internal void WriteState(BinaryWriter writer) => _mechanic.WriteState(writer);
    internal int ProjectCount => _constructionProjects.Values.Sum(projects => projects.Count);

    internal void CopyProjects(Span<ConstructionProjectPose> destination)
    {
        int at = 0;
        foreach (var projects in _constructionProjects.Values)
            foreach (var project in projects.Values)
                destination[at++] = new(project.Id, project.Owner, project.UnitType, project.Position, project.ReservedAt > 0);
    }

    internal static void WriteProjects(ConstructionSystem? system, BinaryWriter writer)
    {
        writer.Write(system?.ProjectCount ?? 0);
        if (system is null) return;
        foreach (var projects in system._constructionProjects.Values)
            foreach (var project in projects.Values)
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
