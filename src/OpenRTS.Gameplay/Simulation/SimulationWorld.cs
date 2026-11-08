using System.Security.Cryptography;
using System.Text;
using Frent;
using OpenRTS.Core.Capture;
using OpenRTS.Core.Construction;
using OpenRTS.Core.Economy;
using OpenRTS.Core.Production;
using OpenRTS.Core.Simulation;
using OpenRTS.Core.Transport;

namespace OpenRTS.Core;

/// <summary>Single-owner world. Hosts exchange commands and copied poses, never its mutable storage.</summary>
public sealed partial class SimulationWorld : Simulation<WorldCommand, WorldEvent>, ICaptureWorld, IConstructionWorld, IProductionWorld, ITransportWorld
{
    private readonly UnitDefinition[] _types;
    private readonly GameplayMechanics _mechanics;
    private readonly EconomySystem _economy;
    private readonly BuildRecipes _buildRecipes;
    private readonly ProductionSystem _production;
    private readonly CaptureSystem? _capture;
    private readonly ConstructionSystem? _construction;
    private readonly TransportSystem? _transport;
    private readonly ICombatTargetRule? _combatTargetRule;
    private readonly IEndOfTickRule? _endOfTick;
    private readonly TerrainRoutePlanner? _navigation;
    private readonly MotionIndex? _motion;
    private readonly MotionIndex? _combatIndex;
    private long _orderGroupSequence;
    private int[] _movementOrder = [], _navigationGoals = [];
    private bool _orderDirty = true;
    private readonly IComparer<int> _idOrder;

    public int SpeedPerTick { get; }
    public NavigationGrid? BaseNavigationGrid => _baseGrid;
    public NavigationGrid? NavigationGrid => _navigation?.Grid;
    public int NavigationWaitingCount { get; private set; }
    public int NavigationBuiltFields => _navigation?.BuiltFields ?? 0;
    public int NavigationVisitedCells => _navigation?.VisitedCells ?? 0;
    public double NavigationBuildMilliseconds => _navigation?.BuildMilliseconds ?? 0;
    public int MovingCount => CountMoving();

    public SimulationWorld(IEnumerable<UnitDefinition> unitTypes, int width, int depth, ulong seed = 1,
        int initialCapacity = 0, int tickRate = 25, int speedPerTick = 128,
        NavigationGrid? navigationGrid = null, int navigationWorkers = 1, int fieldsPerTick = 2,
        int cachedFields = 8, bool reverseNavigationJobs = false, IEnumerable<PlayerDefinition>? players = null)
        : this((ProductionRateRule?)null, unitTypes, width, depth, seed, initialCapacity, tickRate, speedPerTick,
            navigationGrid, navigationWorkers, fieldsPerTick, cachedFields, reverseNavigationJobs, players) { }

    public SimulationWorld(ProductionRateRule? productionRate, IEnumerable<UnitDefinition> unitTypes, int width, int depth,
        ulong seed = 1, int initialCapacity = 0, int tickRate = 25, int speedPerTick = 128,
        NavigationGrid? navigationGrid = null, int navigationWorkers = 1, int fieldsPerTick = 2,
        int cachedFields = 8, bool reverseNavigationJobs = false, IEnumerable<PlayerDefinition>? players = null)
        : this(new GameplayMechanics { ProductionRate = productionRate }, unitTypes, width, depth, seed, initialCapacity, tickRate,
            speedPerTick, navigationGrid, navigationWorkers, fieldsPerTick, cachedFields, reverseNavigationJobs, players) { }

    public static SimulationWorld CreateWithMechanics(GameplayMechanics mechanics, IEnumerable<UnitDefinition> unitTypes, int width, int depth,
        ulong seed = 1, int initialCapacity = 0, int tickRate = 25, int speedPerTick = 128,
        NavigationGrid? navigationGrid = null, int navigationWorkers = 1, int fieldsPerTick = 2,
        int cachedFields = 8, bool reverseNavigationJobs = false, IEnumerable<PlayerDefinition>? players = null)
        => new(mechanics, unitTypes, width, depth, seed, initialCapacity, tickRate, speedPerTick,
            navigationGrid, navigationWorkers, fieldsPerTick, cachedFields, reverseNavigationJobs, players);

    private SimulationWorld(GameplayMechanics mechanics, IEnumerable<UnitDefinition> unitTypes, int width, int depth,
        ulong seed, int initialCapacity, int tickRate, int speedPerTick, NavigationGrid? navigationGrid, int navigationWorkers,
        int fieldsPerTick, int cachedFields, bool reverseNavigationJobs, IEnumerable<PlayerDefinition>? players)
        : base(width, depth, seed, initialCapacity, tickRate)
    {
        ArgumentNullException.ThrowIfNull(mechanics);
        if (speedPerTick < 1 || speedPerTick > 1 << 20) throw new ArgumentOutOfRangeException(nameof(speedPerTick));
        _types = unitTypes.ToArray();
        if (_types.Length == 0 || _types.Any(t => t is null) || _types.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != _types.Length)
            throw new ArgumentException("Unit definitions must be nonempty, nonnull and uniquely named.", nameof(unitTypes));
        _mechanics = mechanics;
        bool usesCapture = _types.Any(type => type.Capturable is not null || type.Abilities.Any(a => a.Kind == AbilityKind.Capture));
        if (usesCapture && mechanics.Capture is null)
            throw new ArgumentException("Unit definitions require a capture mechanic.", nameof(mechanics));
        bool usesTransport = _types.Any(type => type.Transport is not null || type.Passenger is not null);
        if (usesTransport && mechanics.Transport is null)
            throw new ArgumentException("Unit definitions require a transport mechanic.", nameof(mechanics));
        bool usesConstruction = _types.Any(type => type.ConstructionRange > 0);
        if (usesConstruction && mechanics.Construction is null)
            throw new ArgumentException("Unit definitions require a construction mechanic.", nameof(mechanics));
        if (mechanics.GridPathfinder is not null && navigationGrid is null)
            throw new ArgumentException("A registered grid pathfinder requires a navigation grid.", nameof(navigationGrid));
        _economy = new(players, StorageGate, IsConstructing);
        _buildRecipes = new(_types);
        _production = new(this, _types, _buildRecipes, _economy, mechanics.ProductionRate, StorageGate);
        SpeedPerTick = speedPerTick;
        _idOrder = Comparer<int>.Create((a, b) => _entityHandles[a].Get<EntityIdentity>().Id.Value.CompareTo(_entityHandles[b].Get<EntityIdentity>().Id.Value));
        if (navigationGrid is not null)
        {
            if (speedPerTick > 1024) throw new ArgumentOutOfRangeException(nameof(speedPerTick), "The current local motion budget allows at most 1024 world units per tick.");
            if (width != (long)navigationGrid.Width * navigationGrid.CellSize || depth != (long)navigationGrid.Height * navigationGrid.CellSize)
                throw new ArgumentException("World bounds must match the navigation grid.", nameof(navigationGrid));
            _baseGrid = navigationGrid;
            _navigation = new(navigationGrid, _types, navigationWorkers, fieldsPerTick, cachedFields, reverseNavigationJobs, mechanics.GridPathfinder);
            _motion = new(speedPerTick);
            _motion.Clear(initialCapacity);
        }
        if (_motion is null && _types.Any(t => t.Weapon is not null || t.Footprint is not null || t.TaskQueues.Count > 0 || t.Transport is not null)) _combatIndex = new(speedPerTick);
        if (mechanics.Capture is { } capture && (usesCapture || !ReferenceEquals(capture, GameplayMechanics.BuiltInCapture)))
            _capture = new(this, capture.Create());
        if (mechanics.Transport is { } transport && (usesTransport || !ReferenceEquals(transport, GameplayMechanics.BuiltInTransport)))
            _transport = new(this, transport.Create());
        if (mechanics.Construction is { } construction && (usesConstruction || !ReferenceEquals(construction, GameplayMechanics.BuiltInConstruction)))
            _construction = new(this, construction.Create());
        _combatTargetRule = mechanics.CombatTargeting?.Create();
        _endOfTick = mechanics.EndOfTick?.Create();
    }

    public EntityId AddInitialUnit(int unitType, WorldPosition position, uint owner = 0, uint states = 0)
        => AddInitialUnit(unitType, position, owner, states, false, false);

    public EntityId AddInitialMapUnit(int unitType, WorldPosition position, uint owner, bool isStart)
        => AddInitialUnit(unitType, position, owner, 0, true, isStart);

    private EntityId AddInitialUnit(int unitType, WorldPosition position, uint owner, uint states, bool strictMapPlacement, bool isStart)
    {
        ThrowIfDisposed();
        if (Tick != 0) throw new InvalidOperationException("After initialization, entities are created through commands.");
        ValidatePosition(position);
        ValidateType(unitType);
        var type = _types[unitType];
        if (type.Footprint is { } footprint)
        {
            if (isStart && _baseGrid is { } basis && (!FootprintFits(footprint, position) || !footprint.AllowsConstruction(basis, position, type.Locomotor)))
                throw new ArgumentException("Starting footprint cannot be placed on this terrain.", nameof(position));
            if (strictMapPlacement && _motion is not null && !InitialMapFootprintClear(footprint, position))
                throw new ArgumentException("Initial footprint overlaps another footprint.", nameof(position));
        }
        else if (!StopAllowed(unitType, position))
            throw new ArgumentException("Initial terrain does not permit stopping.", nameof(position));
        if (!PrepareFootprint(unitType, position, out var footprintGrid)) throw new ArgumentException("Footprint placement is out of bounds, misaligned or would trap an existing body.", nameof(position));
        if (_navigation is not null && type.Footprint is null)
        {
            if (!_motion!.ClearPlacement(position, out _, _types[unitType].Body))
                throw new ArgumentException("Initial navigation placement is blocked or occupied.", nameof(position));
        }
        if ((states & ~_types[unitType].AllowedStateMask) != 0) throw new ArgumentOutOfRangeException(nameof(states));
        try
        {
            var id = Spawn(unitType, position, owner, states);
            if (footprintGrid is not null) CommitGrid(footprintGrid);
            return id;
        }
        catch { _failed = true; throw; }
    }

    protected override void PrepareStep()
    {
        _conditionGrants?.Expire(Tick + 1);
        RebuildMotionIndex();
    }

    protected override void Update(CancellationToken cancellationToken)
    {
        CapturePreviousFacings();
        PrepareUpgrades();
        UpdateOrders();
        _capture?.EndOrders(Tick);
        EnsureMotionIndex();
        if (_navigation is not null) UpdateNavigation(cancellationToken);
        else UpdateDirectMovement();
        UpdateWeapons();
        _construction?.Advance();
        AdvanceSupply();
        _economy.AdvanceIncome(ExistingStorage, Tick);
        _production.Advance(ExistingStorage, StepCancellation);
        UpdatePresentation();
        _endOfTick?.Advance(new(this));
    }

    internal bool TryGetGameplayOwner(EntityId id, out uint owner)
    {
        if (TryGetEntity(id, out var entity)) { owner = entity.Get<OwnershipState>().Owner; return true; }
        owner = 0;
        return false;
    }

    protected override void ValidateCommand(WorldCommand command)
    {
        const string batchArgument = "commands";
        if (!Enum.IsDefined(command.Kind))
            throw new ArgumentException("Commands must have a known kind.", batchArgument);
        if (command.Kind is CommandKind.Move or CommandKind.MoveAll or CommandKind.MoveGroup or CommandKind.Scatter or CommandKind.Spawn or CommandKind.BlockCell or CommandKind.UnblockCell or CommandKind.Aim or CommandKind.AttackMove or CommandKind.MoveSelected or CommandKind.UseAbility or CommandKind.PlaceProduced or CommandKind.SetRally or CommandKind.UnloadAt or CommandKind.PlanConstruction)
            ValidatePosition(command.Position);
        if (command.Kind == CommandKind.StartUpgrade && command.Action is { Length: > 128 })
            throw new ArgumentException("Upgrade ID is too long.", batchArgument);
        if (command.Kind is CommandKind.UseAbility or CommandKind.SetAutoCast or CommandKind.EnqueueTask or CommandKind.CancelTasks or CommandKind.PlaceProduced or CommandKind.PlanConstruction)
            if (string.IsNullOrWhiteSpace(command.Action) || command.Action.Length > 128 || command.Amount < 1 || command.Kind == CommandKind.EnqueueTask && command.Amount > 256 || command.TaskId < 0)
                throw new ArgumentException("Custom actions require a valid ID, amount and task identity.", batchArgument);
        if (command.Kind == CommandKind.Scatter && (command.Position.X == 0 || command.Position.Y == 0))
            throw new ArgumentException("Scatter area must have positive extents.", batchArgument);
        if (command.Kind == CommandKind.Spawn) ValidateType(command.UnitType);
        if (command.Kind == CommandKind.PlayAnimation && (command.Animation < 0 || command.DurationTicks < 1 ||
            command.DurationTicks > 1000000 || !Enum.IsDefined(command.Track)))
            throw new ArgumentException("Animation commands require a valid track, nonnegative ID and duration in 1..1000000 ticks.", batchArgument);
        if (command.Kind is CommandKind.GrantCondition or CommandKind.RevokeCondition && command.Recipients is not null)
            throw new ArgumentException("Condition commands target a single entity.", batchArgument);
        if (command.Kind == CommandKind.GrantCondition && (command.Condition != GameplayConditions.WeaponsDisabled || command.DurationTicks is < 0 or > 1000000))
            throw new ArgumentException("GrantCondition requires WeaponsDisabled and a duration in 0..1000000 ticks.", batchArgument);
        if (command.Kind == CommandKind.RevokeCondition && command.ConditionGrantId <= 0)
            throw new ArgumentException("RevokeCondition requires a positive grant ID.", batchArgument);
        if ((command.Kind is CommandKind.BlockCell or CommandKind.UnblockCell or CommandKind.MoveGroup) && _navigation is null)
            throw new ArgumentException("This world has no navigation layer.", batchArgument);
    }

    public void CopyPoses(Span<EntityPose> destination)
    {
        ThrowIfDisposed();
        if (_failed) throw new InvalidOperationException("An interrupted world cannot publish a frame.");
        if (destination.Length < Count) throw new ArgumentException("Destination is too small.", nameof(destination));
        int weaponOffset = 0;
        for (int i = 0; i < Count; i++)
        {
            ref var entity = ref _entityHandles[i];
            var entityIdentity = entity.Get<EntityIdentity>();
            ref var entityPresentation = ref entity.Get<PresentationState>();
            ref var entityOwnership = ref entity.Get<OwnershipState>();
            ref var entityCombat = ref entity.Get<CombatState>();
            ref var entityOrders = ref entity.Get<OrderState>();
            ref var entityMotion = ref entity.Get<MotionState>();
            ref var entityNavigation = ref entity.Get<NavigationAgentState>();
            int health = entity.Get<HealthState>().Current;
            var type = _types[entityIdentity.Type];
            uint states = entityPresentation.VisualStates;
            var carrier = TransportSystem.Project(_transport, entity, type, ref states, out var spatial);
            var construction = _construction?.Project(entity, type, ref states) ?? default;
            if (!construction.Active && type.LowPowerVisualMask != 0 && ReadConditions(type, entityOwnership.Owner).HasFlag(GameplayConditions.LowPower))
                states |= type.LowPowerVisualMask;
            if (type.WeaponDisableVisualMask != 0 && WeaponsDisabled(entityIdentity.Id))
                states |= type.WeaponDisableVisualMask;
            var rally = ProductionSystem.Project(entity, type, ref states);
            var upgrade = ReadUpgrade(entity, type, ref states);
            var supply = ReadSupply(entity, type, ref states);
            var capture = _capture?.GetProgress(entity, entityOrders) ?? default;
            var captureTarget = _capture?.GetTargetProgress(entity, type) ?? default;
            destination[i] = new(entityIdentity.Id, entityIdentity.Type, spatial.Previous, spatial.Position,
                new(entityOwnership.Owner, entityPresentation.PreviousBodyFacing, entityPresentation.BodyFacing, entityPresentation.PreviousWeaponFacing, entityPresentation.WeaponFacing,
                    entityPresentation.BodyAnimation, entityPresentation.WeaponAnimation, states),
                new(entityOrders.ActiveOrder.Kind, entityOrders.OrderPhase, entityMotion.Target, entityOrders.HasOrder && entityOrders.ActiveOrder.Kind is UnitActionKind.Build or UnitActionKind.UpgradeSupport ? entityOrders.ActiveOrder.Target : entityOrders.CombatTarget, entityOrders.PendingOrders?.Count ?? 0, entityCombat.WeaponReadyTick, capture.Progress, capture.Duration), health, weaponOffset, entityCombat.Weapons.Length, entityNavigation.TerrainStranded, rally.HasRally, rally.Rally, carrier, construction, entityOrders.AutoBuild, supply, upgrade, captureTarget);
            weaponOffset += entityCombat.Weapons.Length;
        }
    }

    public int ConstructionProjectCount => _construction?.ProjectCount ?? 0;

    public void CopyConstructionProjects(Span<ConstructionProjectPose> destination)
    {
        ThrowIfDisposed();
        if (_failed) throw new InvalidOperationException("An interrupted world cannot publish a frame.");
        if (destination.Length < ConstructionProjectCount) throw new ArgumentException("Destination is too small.", nameof(destination));
        _construction?.CopyProjects(destination);
    }

    public override SimulationReport Report()
    {
        ThrowIfDisposed();
        if (_failed) throw new InvalidOperationException("An interrupted world has no completed state.");
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            WriteMechanics(writer);
            writer.Write(Tick); writer.Write(Width); writer.Write(Depth);
            writer.Write(TickRate); writer.Write(SpeedPerTick); writer.Write(NextEntityId); writer.Write(_random.State);
            writer.Write(_types.Length);
            foreach (var type in _types)
            {
                type.WriteState(writer);
            }
            writer.Write(_orderGroupSequence); writer.Write(Count);
            foreach (var entity in _entityHandles.AsSpan(0, Count).ToArray().OrderBy(e => e.Get<EntityIdentity>().Id.Value))
            {
                var entityIdentity = entity.Get<EntityIdentity>();
                ref var entityMotion = ref entity.Get<MotionState>();
                ref var entityNavigation = ref entity.Get<NavigationAgentState>();
                var position = entity.Get<SpatialState>().Position;
                writer.Write(entityIdentity.Id.Value); writer.Write(entityIdentity.Type); writer.Write(entity.Get<HealthState>().Current);
                writer.Write(position.X); writer.Write(position.Y); writer.Write(position.Elevation);
                writer.Write(entityMotion.Target.X); writer.Write(entityMotion.Target.Y); writer.Write(entityMotion.Target.Elevation);
                writer.Write(entityMotion.Moving); writer.Write(entityNavigation.TerrainStranded);
                writer.Write(entityMotion.MovementRemainder);
                WritePresentation(writer, entity); WriteOrders(writer, entity); WriteWeapons(writer, entity);
                TransportSystem.WriteEntityState(_transport, writer, entity);
                ConstructionSystem.WriteFoundation(_construction, writer, entity);
                WriteSupply(writer, entity);
                WriteUpgrade(writer, entity);
                if (_navigation is not null)
                {
                    writer.Write(entityNavigation.GoalCell); writer.Write(entityNavigation.GroupMember); writer.Write(entityNavigation.WaitTicks);
                    writer.Write(entityNavigation.YieldFor.Value); writer.Write(entityNavigation.YieldDeadline);
                    writer.Write(entityNavigation.LastProgressTick);
                    writer.Write(entityNavigation.LocalTarget.X); writer.Write(entityNavigation.LocalTarget.Y);
                    writer.Write(entityNavigation.PathLength - entityNavigation.PathCursor);
                    int index = _indices[entityIdentity.Id];
                    foreach (var point in Path(index)[entityNavigation.PathCursor..entityNavigation.PathLength])
                    { writer.Write(point.X); writer.Write(point.Y); }
                }
            }
            WriteProjectiles(writer);
            _production.WriteState(writer, ExistingStorage);
            WritePlayers(writer);
            ConstructionSystem.WriteProjects(_construction, writer);
            writer.Write(_baseGrid is not null); _baseGrid?.WriteState(writer);
            if (_navigation is null) writer.Write(false);
            else
            {
                _navigation.WriteState(writer);
                writer.Write(_groupSlots.Length);
                foreach (var point in _groupSlots) { writer.Write(point.X); writer.Write(point.Y); }
                WriteGroupState(writer);
            }
        }
        return new(Tick, Count, Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length))));
    }

    protected override void ApplyCommand(WorldCommand command)
    {
        if (_restrictPlayerCommands && command.Issuer != 0 && !CanApplyPlayerCommand(command))
        {
            _events.Add(new(Tick, WorldEventKind.CommandDenied, command.Entity, TargetEntity: command.TargetEntity, Owner: command.Issuer));
            return;
        }
        if (command.Kind is CommandKind.GrantCondition or CommandKind.RevokeCondition) { ApplyConditionCommand(command); return; }
        if (command.Kind is CommandKind.UseAbility or CommandKind.SetAutoCast) { ApplyAbility(command); return; }
        if (command.Kind is CommandKind.EnqueueTask or CommandKind.CancelTasks or CommandKind.PlaceProduced or CommandKind.SetRally)
        {
            var recipients = Array.ConvertAll(Recipients(command), index => _entityHandles[index]);
            _production.Apply(command, recipients);
            return;
        }
        if (command.Kind is CommandKind.StartUpgrade or CommandKind.CancelUpgrade) { ApplyUpgrade(command); return; }
        if (command.Kind is CommandKind.PlanConstruction or CommandKind.BuildSite or CommandKind.CancelConstruction)
        {
            ConstructionSystem.Apply(_construction, this, command);
            return;
        }
        if (command.Kind is CommandKind.Enter or CommandKind.Unload or CommandKind.UnloadAt)
        {
            TransportSystem.Apply(_transport, this, command);
            return;
        }
        if (command.Kind is CommandKind.Move or CommandKind.MoveGroup or CommandKind.MoveSelected or CommandKind.AttackMove or CommandKind.AttackTarget or CommandKind.RepairTarget or CommandKind.CaptureTarget or CommandKind.Stop or CommandKind.HoldPosition)
        {
            var members = Recipients(command);
            if (command.Kind is CommandKind.Move or CommandKind.MoveSelected or CommandKind.MoveGroup)
                members = Array.FindAll(members, index => !_production.TryHandleMoveAsRally(_entityHandles[index], command.Position));
            if (members.Length == 0) return;
            var action = command.Kind switch { CommandKind.AttackMove => UnitActionKind.AttackMove, CommandKind.AttackTarget => UnitActionKind.Attack, CommandKind.RepairTarget => UnitActionKind.Repair, CommandKind.CaptureTarget => UnitActionKind.Capture,
                CommandKind.Stop => UnitActionKind.Stop, CommandKind.HoldPosition => UnitActionKind.HoldPosition, _ => UnitActionKind.Move };
            if (_navigation is not null && (command.Kind is CommandKind.MoveGroup or CommandKind.MoveSelected || command.Kind == CommandKind.AttackMove && members.Length > 1))
                SetGroupTarget(command.Position, members, action, command.Queued);
            else foreach (int member in members) IssueOrder(member, new(action, command.Position, command.TargetEntity), command.Queued);
            return;
        }
        if (command.Kind is CommandKind.BlockCell or CommandKind.UnblockCell)
        {
            SetBaseBlocked(_navigation!.Grid.CellAt(command.Position), command.Kind == CommandKind.BlockCell);
            return;
        }
        if (command.Kind == CommandKind.Spawn)
        {
            bool footprint = _types[command.UnitType].Footprint is not null;
            bool terrain = footprint || StopAllowed(command.UnitType, command.Position);
            bool free = footprint || _motion is null || _motion.ClearPlacement(command.Position, out _, _types[command.UnitType].Body);
            bool stamp = PrepareFootprint(command.UnitType, command.Position, out var footprintGrid);
            terrain &= stamp;
            if (!terrain || !free)
            {
                _events.Add(new(Tick, !free ? WorldEventKind.CellOccupied : WorldEventKind.CellBlocked,
                    default, command.Position, command.UnitType));
                return;
            }
            if ((command.States & ~_types[command.UnitType].AllowedStateMask) != 0)
            { _events.Add(new(Tick, WorldEventKind.InvalidPresentation, default, command.Position, command.UnitType)); return; }
            var id = Spawn(command.UnitType, command.Position, command.Owner, command.States);
            if (footprintGrid is not null) CommitGrid(footprintGrid);
            _events.Add(new(Tick, WorldEventKind.Spawned, id, command.Position, command.UnitType));
            return;
        }
        if (command.Kind is CommandKind.MoveAll or CommandKind.Scatter)
        {
            ulong seed = command.Kind == CommandKind.Scatter ? _random.NextUInt64() : 0;
            for (int i = 0; i < Count; i++)
            {
                // Per-entity streams make this independent of packed storage and future task scheduling.
                var random = new DeterministicRandom(seed ^ _entityHandles[i].Get<EntityIdentity>().Id.Value);
                var target = command.Kind == CommandKind.Scatter
                    ? new WorldPosition(random.NextInt(command.Position.X), random.NextInt(command.Position.Y)) : command.Position;
                IssueOrder(i, new(UnitActionKind.Move, target), false);
            }
            return;
        }
        if (!_indices.TryGetValue(command.Entity, out int index))
        {
            _events.Add(new(Tick, WorldEventKind.MissingEntity, command.Entity));
            return;
        }
        if (command.Kind is CommandKind.PlayAnimation or CommandKind.SetVisualStates or CommandKind.ToggleVisualState or CommandKind.Aim or CommandKind.ClearAim)
        { ApplyPresentation(index, command); return; }
        if (command.Kind is CommandKind.Destroy or CommandKind.Remove)
        {
            DestroyIndex(index, wreck: command.Kind == CommandKind.Destroy);
            RebuildMotionIndex();
            return;
        }
        RejectAction(command.Entity);
    }

    private void RejectAction(EntityId id = default) => _events.Add(new(Tick, WorldEventKind.ActionUnavailable, id));

    private void DestroyIndex(int index, bool wreck = true)
    {
        var actor = _entityHandles[index];
        var id = actor.Get<EntityIdentity>().Id;
        wreck = _transport?.BeforeRemove(actor, wreck) ?? wreck;
        index = _indices[id];
        _production.BeforeRemove(_entityHandles[index]);
        _construction?.BeforeRemove(_entityHandles[index]);
        RemoveSupply(_entityHandles[index], actor.Get<EntityIdentity>());
        CancelUpgrade(actor, restoreNeutral: false);
        bool stamped = _footprints.Remove(id);
        _events.Add(new(Tick, wreck ? WorldEventKind.Destroyed : WorldEventKind.Removed, id, _entityHandles[index].Get<SpatialState>().Position, _entityHandles[index].Get<EntityIdentity>().Type, Owner: _entityHandles[index].Get<OwnershipState>().Owner));
        _economy.InvalidatePower();
        _conditionGrants?.RemoveEntity(id);
        DeleteEntity(index);
        _orderDirty = true;
        if (stamped && _baseGrid is not null) CommitGrid(ComposeFootprints(_baseGrid));
    }

    private bool SetTarget(int index, WorldPosition position)
    {
        ref var entity = ref _entityHandles[index];
        ref var entitySpatial = ref entity.Get<SpatialState>();
        var entityIdentity = entity.Get<EntityIdentity>();
        ref var entityNavigation = ref entity.Get<NavigationAgentState>();
        ref var entityMotion = ref entity.Get<MotionState>();
        if ((!_types[entityIdentity.Type].Movable || UpgradeImmobilizes(entity)) && position != entitySpatial.Position)
        { _events.Add(new(Tick, WorldEventKind.InvalidOrder, entityIdentity.Id)); return false; }
        if (_navigation is not null)
        {
            if (!Resolver(entityIdentity.Type).Resolve(entitySpatial.Position, position, out var resolved, StepCancellation))
            { _events.Add(new(Tick, WorldEventKind.NoRoute, entityIdentity.Id, position, entityIdentity.Type)); return false; }
            if (resolved != position) _events.Add(new(Tick, WorldEventKind.DestinationAdjusted, entityIdentity.Id, resolved, entityIdentity.Type));
            position = resolved;
        }
        else if (!StopAllowed(entityIdentity.Type, position))
        { _events.Add(new(Tick, WorldEventKind.NoRoute, entityIdentity.Id, position, entityIdentity.Type)); return false; }
        entityNavigation.GroupMember = false; entityNavigation.OrderGroup = 0; entityMotion.MovementRemainder = 0;
        entityNavigation.WaitTicks = entityNavigation.PathLength = entityNavigation.PathCursor = 0;
        entityNavigation.YieldFor = default; entityNavigation.YieldDeadline = 0; entityNavigation.LocalTarget = default;
        entityNavigation.Route = null;
        entityMotion.Target = GroundPosition(position, entitySpatial.Position.Elevation);
        if (_navigation is not null)
        {
            entityNavigation.GoalCell = _navigation.Grid.CellAt(position);
        }
        entityMotion.Moving = entitySpatial.Position != entityMotion.Target;
        return true;
    }

    bool ICaptureWorld.TryGetEntity(EntityId id, out Entity entity) => TryGetEntity(id, out entity);
    UnitDefinition ICaptureWorld.TypeOf(Entity entity) => TypeOf(entity);
    bool ICaptureWorld.HasPassengers(Entity entity) => _transport?.HasPassengers(entity) == true;
    void ICaptureWorld.IssueOrder(EntityId actor, UnitOrder order, bool queued) => IssueOrder(_indices[actor], order, queued);
    void ICaptureWorld.SetMovementTarget(EntityId actor, WorldPosition position) => SetTarget(_indices[actor], position);
    void ICaptureWorld.Halt(EntityId actor) => Halt(_indices[actor]);
    void ICaptureWorld.ChangeOwner(EntityId target, uint owner) => ChangeOwner(_indices[target], owner);

    private EntityId Spawn(int unitType, WorldPosition position, uint owner = 0, uint states = 0,
        EntityId id = default, int? initialHealth = null, Action<Frent.Entity>? initialize = null)
    {
        if (id.Value == 0) id = AllocateEntityId();
        Frent.Entity state;
        var type = _types[unitType];
        var health = new HealthState(initialHealth ?? type.MaxHealth);
        var producer = ProductionSystem.CreateProducer(type);
        var orders = new OrderState { AutoBuild = type.Abilities.Any(a => a.Kind == AbilityKind.Build && a.AutoCast) };
        lock (StorageGate)
        {
            var storage = Storage;
            state = producer is null
                ? storage.Create(new EntityIdentity(id, unitType), type.Definition, new SpatialState(position), health, orders, new MotionState(position), new NavigationAgentState(_navigation?.Grid.CellAt(position) ?? -1, _navigation is not null), new CombatState(type), new PresentationState(type.IdleAnimation, Tick, states), new OwnershipState(owner))
                : storage.Create(new EntityIdentity(id, unitType), type.Definition, new SpatialState(position), health, orders, new MotionState(position), new NavigationAgentState(_navigation?.Grid.CellAt(position) ?? -1, _navigation is not null), new CombatState(type), new PresentationState(type.IdleAnimation, Tick, states), new OwnershipState(owner), producer);
            _transport?.Initialize(state, type);
            initialize?.Invoke(state);
            _economy.Initialize(state, type.Economy, owner, Tick);
            _capture?.Initialize(state, type);
            if (type.Supply is not null) InitializeSupply(state, id, type);
            if (type.Upgrades.Count > 0) state.Add(new UpgradeState());
        }
        _economy.InvalidatePower();
        (_motion ?? _combatIndex)?.Add(id, position, type.Body, type.Footprint?.HalfWidth ?? type.Body.HalfWidth, type.Footprint?.HalfHeight ?? type.Body.HalfHeight);
        if (type.Footprint is not null) _footprints.Add(id);
        TrackEntity(state);
        if (producer is not null) _production.InvalidateProducerOrder();
        for (int a = 0; a < type.Abilities.Count; a++) if (type.Abilities[a].Kind == AbilityKind.Weapon) _entityHandles[_indices[id]].Get<CombatState>().Weapons[AbilityMount(unitType, a)].AutoCast = type.Abilities[a].AutoCast;
        _orderDirty = true;
        return id;
    }

    private WorldPosition StepTowards(WorldPosition position, WorldPosition target) => StepTowards(position, target, SpeedPerTick);

    private static WorldPosition StepTowards(WorldPosition position, WorldPosition target, int speed)
    {
        long dx = (long)target.X - position.X, dy = (long)target.Y - position.Y;
        ulong squared = (ulong)(dx * dx + dy * dy);
        if (squared <= (ulong)speed * (ulong)speed) return target;
        long distance = (long)SquareRoot(squared);
        if ((ulong)(distance * distance) < squared) distance++;
        int stepX = (int)(dx * speed / distance), stepY = (int)(dy * speed / distance);
        if (stepX == 0 && stepY == 0)
        {
            if (Math.Abs(dx) >= Math.Abs(dy)) stepX = Math.Sign(dx); else stepY = Math.Sign(dy);
        }
        return new(checked(position.X + stepX), checked(position.Y + stepY), position.Elevation);
    }

    private void ValidateType(int type)
    {
        if ((uint)type >= (uint)_types.Length) throw new ArgumentOutOfRangeException(nameof(type));
    }

    internal static ulong SquareRoot(ulong value)
    {
        ulong result = 0, bit = 1UL << 62;
        while (bit > value) bit >>= 2;
        while (bit != 0)
        {
            if (value >= result + bit) { value -= result + bit; result = (result >> 1) + bit; }
            else result >>= 1;
            bit >>= 2;
        }
        return result;
    }
}
