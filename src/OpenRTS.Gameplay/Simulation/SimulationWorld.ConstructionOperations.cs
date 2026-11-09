using System.Diagnostics.CodeAnalysis;
using Frent;
using OpenRTS.Core.Economy;
using OpenRTS.Core.Construction;
using OpenRTS.Core.Simulation;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    IEnumerable<Entity> IConstructionWorld.Recipients(WorldCommand command) => Recipients(command).Select(index => _entityHandles[index]);
    bool IConstructionWorld.TryGetEntity(EntityId id, out Entity entity) => TryGetEntity(id, out entity);
    UnitDefinition IConstructionWorld.TypeOf(int type) => _types[type];

    UnitDefinition IConstructionWorld.TypeOf(Entity entity) => TypeOf(entity);

    bool IConstructionWorld.TryGetRecipe(Entity builder, string? action, out int target, [NotNullWhen(true)] out BuildDefinition? recipe)
    {
        int builderType = builder.Get<EntityIdentity>().Type;
        int product = _buildRecipes.FindProduct(builderType, action);
        if (!IsConstructing(builder) && product >= 0 && _types[builderType].Products[product].OnSite &&
            _types[builderType].ProductEnabled(product, CompletedUpgrade(builder)))
        {
            var resolved = _buildRecipes[builderType, product];
            target = resolved.UnitType;
            recipe = resolved.Build;
            return true;
        }
        target = -1;
        recipe = null;
        return false;
    }

    bool IConstructionWorld.IsOnMap(Entity entity) => IsOnMap(entity);
    GameplayConditions IConstructionWorld.ReadConditions(Entity actor) => ReadConditions(TypeOf(actor), actor.Get<OwnershipState>().Owner);
    bool IConstructionWorld.CanQueueOrder(Entity actor) => (actor.Get<OrderState>().PendingOrders?.Count ?? 0) < MaximumQueuedOrders;
    void IConstructionWorld.IssueOrder(EntityId actor, UnitOrder order, bool queued) => IssueOrder(_indices[actor], order, queued);
    void IConstructionWorld.Halt(EntityId actor) => Halt(_indices[actor]);
    bool IConstructionWorld.StopAllowed(int type, WorldPosition position) => StopAllowed(type, position);
    bool IConstructionWorld.TrySetTarget(EntityId actor, WorldPosition position) => SetTarget(_indices[actor], position);

    bool IConstructionWorld.TryEvacuate(EntityId actor, WorldPosition position, bool preserveOrder) =>
        TryEvacuate(actor, position, preserveOrder);

    private bool TryEvacuate(EntityId actor, WorldPosition position, bool preserveOrder)
    {
        int index = _indices[actor];
        var entity = _entityHandles[index];
        int typeIndex = entity.Get<EntityIdentity>().Type;
        var type = _types[typeIndex];
        var from = entity.Get<SpatialState>().Position;
        if (IsTransformationLocked(entity) || UpgradeImmobilizes(entity) || !StopAllowed(typeIndex, position) || !(_motion ?? _combatIndex)!.ClearPlacement(position, out _, type.Body) ||
            _navigation is not null && !TerrainGeometry.Clear(_navigation.Grid, from, position, type.Body, type.Locomotor) ||
            !SetTarget(index, position)) return false;

        if (preserveOrder) return true;
        CancelTransformation(actor, stop: false);
        CancelWindups(index);
        ref var orders = ref entity.Get<OrderState>();
        orders.ActiveOrder = new(UnitActionKind.Move, position);
        orders.HasOrder = true;
        orders.OrderPhase = OrderPhase.Moving;
        orders.CombatTarget = default;
        orders.PendingOrders?.Clear();
        return true;
    }

    WorldPosition IConstructionWorld.GroundPosition(WorldPosition position) => GroundPosition(position, position.Elevation);

    bool IConstructionWorld.SiteTerrainAllows(UnitDefinition type, WorldPosition position) => SiteTerrainAllows(type, position);

    private bool SiteTerrainAllows(UnitDefinition type, WorldPosition position)
    {
        var footprint = type.Footprint!;
        if (!FootprintFits(footprint, position)) return false;
        return _baseGrid is { } grid ? footprint.AllowsConstruction(grid, position, type.Locomotor) : type.Locomotor.Rules[0].CanStop;
    }

    void IConstructionWorld.QuerySite(WorldPosition from, WorldPosition to, List<EntityId> occupants) =>
        (_motion ?? _combatIndex)!.Query(from, to, 0, occupants);
    bool IConstructionWorld.PrepareFootprint(UnitDefinition type, WorldPosition position, out NavigationGrid? candidate) => PrepareFootprint(type, position, out candidate);
    void IConstructionWorld.CommitFootprint(NavigationGrid candidate) => CommitGrid(candidate);
    EntityId IConstructionWorld.ReserveEntityId() => AllocateEntityId();
    void IConstructionWorld.ReleaseEntityId(EntityId id) => ReleaseEntityId(id);
    bool IConstructionWorld.CanAfford(uint owner, long cost) => _economy.CanAfford(owner, cost);
    bool IConstructionWorld.Spend(uint owner, long cost) => _economy.Spend(owner, cost);

    Entity IConstructionWorld.SpawnFoundation(EntityId id, int type, WorldPosition position, uint owner, int health, int quarterTurns, Action<Entity> initialize)
    {
        Spawn(type, position, owner, id: id, initialHealth: health, initialize: initialize, quarterTurns: quarterTurns);
        return _entityHandles[_indices[id]];
    }

    void IConstructionWorld.CompleteFoundation(Entity entity, Action<Entity> clearFoundation)
    {
        lock (StorageGate)
        {
            clearFoundation(entity);
            EconomySystem.ActivateIncome(entity, TypeOf(entity).Economy, Tick);
        }
        _economy.InvalidatePower();
    }

    void IConstructionWorld.Emit(WorldEvent value) => EmitEvent(value);
}
