using System.Diagnostics.CodeAnalysis;
using Frent;
using OpenRTS.Core.Construction;
using OpenRTS.Core.Simulation;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    IEnumerable<Entity> IConstructionWorld.Recipients(WorldCommand command) => Recipients(command).Select(index => _entityHandles[index]);
    bool IConstructionWorld.TryGetEntity(EntityId id, out Entity entity) => TryGetEntity(id, out entity);
    UnitDefinition IConstructionWorld.TypeOf(int type) => _types[type];

    bool IConstructionWorld.TryGetRecipe(int builderType, string? action, out int target, [NotNullWhen(true)] out BuildDefinition? recipe)
    {
        int product = ProductIndex(builderType, action);
        if (product >= 0 && _types[builderType].Products[product].OnSite)
        {
            target = _productTypes[builderType][product];
            recipe = _builds[builderType][product];
            return true;
        }
        target = -1;
        recipe = null;
        return false;
    }

    bool IConstructionWorld.IsStored(Entity entity) => IsStored(entity);
    GameplayConditions IConstructionWorld.ReadConditions(Entity actor) => ReadConditions(TypeOf(actor), actor.Get<OwnershipState>().Owner);
    void IConstructionWorld.IssueOrder(EntityId actor, UnitOrder order, bool queued) => IssueOrder(_indices[actor], order, queued);
    void IConstructionWorld.Halt(EntityId actor) => Halt(_indices[actor]);
    bool IConstructionWorld.StopAllowed(int type, WorldPosition position) => StopAllowed(type, position);
    bool IConstructionWorld.TrySetTarget(EntityId actor, WorldPosition position) => SetTarget(_indices[actor], position);

    bool IConstructionWorld.TryEvacuate(EntityId actor, WorldPosition position, bool preserveOrder)
    {
        int index = _indices[actor];
        var entity = _entityHandles[index];
        int typeIndex = entity.Get<EntityIdentity>().Type;
        var type = _types[typeIndex];
        var from = entity.Get<SpatialState>().Position;
        if (!StopAllowed(typeIndex, position) || !(_motion ?? _combatIndex)!.ClearPlacement(position, out _, type.Body) ||
            _navigation is not null && !TerrainGeometry.Clear(_navigation.Grid, from, position, type.Body, type.Locomotor) ||
            !SetTarget(index, position)) return false;

        if (preserveOrder) return true;
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

    bool IConstructionWorld.SiteTerrainAllows(int typeIndex, WorldPosition position)
    {
        var type = _types[typeIndex];
        var footprint = type.Footprint!;
        if (!FootprintFits(footprint, position)) return false;
        return _baseGrid is { } grid ? footprint.AllowsConstruction(grid, position, type.Locomotor) : type.Locomotor.Rules[0].CanStop;
    }

    void IConstructionWorld.QuerySite(WorldPosition from, WorldPosition to, List<EntityId> occupants) =>
        (_motion ?? _combatIndex)!.Query(from, to, 0, occupants);
    bool IConstructionWorld.PrepareFootprint(int type, WorldPosition position, out NavigationGrid? candidate) => PrepareFootprint(type, position, out candidate);
    void IConstructionWorld.CommitFootprint(NavigationGrid candidate) => CommitGrid(candidate);
    EntityId IConstructionWorld.ReserveEntityId() => AllocateEntityId();
    void IConstructionWorld.ReleaseEntityId(EntityId id) => ReleaseEntityId(id);
    bool IConstructionWorld.CanAfford(uint owner, long cost) => Player(owner).Credits >= cost;
    bool IConstructionWorld.Spend(uint owner, long cost) => Spend(owner, cost);

    Entity IConstructionWorld.SpawnFoundation(EntityId id, int type, WorldPosition position, uint owner, int health, Action<Entity> initialize)
    {
        Spawn(type, position, owner, id: id, initialHealth: health, initialize: initialize);
        return _entityHandles[_indices[id]];
    }

    void IConstructionWorld.CompleteFoundation(Entity entity, Action<Entity> clearFoundation)
    {
        lock (StorageGate)
        {
            clearFoundation(entity);
            ActivateEconomy(entity, TypeOf(entity));
        }
        _powerDirty = true;
    }

    void IConstructionWorld.Emit(WorldEvent value) => _events.Add(value);
}
