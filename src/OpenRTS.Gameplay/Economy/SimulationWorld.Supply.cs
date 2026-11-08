using Frent;
using OpenRTS.Core.Construction;
using OpenRTS.Core.Orders;
using OpenRTS.Core.Simulation;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private const int DockRange = 1536;
    private const int NearbySupplyRange = 4 * WorldPosition.UnitsPerCell;

    private struct TruckState(bool automatic)
    {
        public bool Automatic = automatic, Returning;
        public EntityId Mine, Depot, Station;
        public int Cargo;
        public long Arrival;
    }

    private struct MineState(int remaining)
    {
        public int Remaining = remaining;
    }

    private readonly SortedSet<uint> _supplyMines = [], _supplyDepots = [];
    private readonly SortedDictionary<uint, (EntityId Truck, long Arrival)> _supplyDocks = new();
    private readonly List<WorldPosition> _dockPoints = [];

    private void InitializeSupply(Entity actor, EntityId id, UnitDefinition type)
    {
        switch (type.Supply?.Role)
        {
            case SupplyRole.Truck:
                actor.Add(new TruckState(type.Abilities.Any(a => a.Kind == AbilityKind.Harvest && a.AutoCast)));
                break;
            case SupplyRole.Mine:
                actor.Add(new MineState(type.Supply.InitialAmount));
                _supplyMines.Add(id.Value);
                break;
            case SupplyRole.Depot:
                _supplyDepots.Add(id.Value);
                break;
        }
    }

    private void RemoveSupply(Entity actor, EntityIdentity identity)
    {
        switch (_types[identity.Type].Supply?.Role)
        {
            case SupplyRole.Mine:
                _supplyMines.Remove(identity.Id.Value);
                break;
            case SupplyRole.Depot:
                _supplyDepots.Remove(identity.Id.Value);
                break;
        }
    }

    private bool ApplyHarvest(int index, WorldCommand command)
    {
        var actor = _entityHandles[index];
        ref var truck = ref actor.Get<TruckState>();
        if (command.Kind == CommandKind.SetAutoCast)
        {
            truck.Automatic = command.Enabled;
            if (!truck.Automatic && !actor.Get<OrderState>().HasOrder) Halt(index);
        }
        else
        {
            if (!TryGetEntity(command.TargetEntity, out var station)) return false;
            uint owner = actor.Get<OwnershipState>().Owner;
            uint stationOwner = station.Get<OwnershipState>().Owner;
            switch (TypeOf(station).Supply?.Role)
            {
                case SupplyRole.Mine when (stationOwner == 0 || stationOwner == owner) && station.Get<MineState>().Remaining > 0:
                    truck.Mine = command.TargetEntity;
                    break;
                case SupplyRole.Depot when stationOwner == owner:
                    truck.Depot = command.TargetEntity;
                    break;
                default:
                    return false;
            }
            ref var orders = ref actor.Get<OrderState>();
            orders.HasOrder = false;
            orders.PendingOrders?.Clear();
            orders.CombatTarget = default;
            truck.Automatic = true;
            Halt(index);
        }
        truck.Station = default;
        truck.Arrival = 0;
        return true;
    }

    private bool TrySupplyStation(EntityId id, uint owner, bool returning, out Entity station)
    {
        if (!TryGetEntity(id, out station)) return false;
        uint stationOwner = station.Get<OwnershipState>().Owner;
        if (returning)
            return TypeOf(station).Supply?.Role == SupplyRole.Depot && stationOwner == owner && !IsConstructing(station);
        return station.Has<MineState>() && (stationOwner == 0 || stationOwner == owner) &&
            station.Get<MineState>().Remaining > 0 && !IsUpgrading(station);
    }

    private EntityId NearestSupplyStation(Entity actor, uint owner, bool returning, long maxDistance = long.MaxValue)
    {
        var best = default(EntityId);
        long distance = long.MaxValue;
        var identity = actor.Get<EntityIdentity>();
        var position = actor.Get<SpatialState>().Position;
        foreach (uint value in returning ? _supplyDepots : _supplyMines)
        {
            var id = new EntityId(value);
            if (!TrySupplyStation(id, owner, returning, out var station)) continue;
            long candidate = ActorDistanceSquared(actor, station);
            if (candidate > maxDistance || candidate >= distance) continue;
            if (candidate > (long)DockRange * DockRange || !StopAllowed(identity.Type, position))
            {
                bool reachable = false;
                foreach (var point in SupplyExits(actor, station))
                    if (CanApproachSupply(identity.Type, position, point)) { reachable = true; break; }
                if (!reachable) continue;
            }
            distance = candidate;
            best = id;
        }
        return best;
    }

    private long ActorDistanceSquared(Entity actor, Entity station) =>
        ObjectGeometry.DistanceSquared(TypeOf(actor), actor.Get<SpatialState>().Position,
            TypeOf(station), station.Get<SpatialState>().Position, out _, out _);

    private IEnumerable<WorldPosition> SupplyExits(Entity actor, Entity station)
    {
        var type = TypeOf(station);
        return ConstructionGeometry.Exits(type.Footprint?.HalfWidth ?? type.Body.HalfWidth,
            type.Footprint?.HalfHeight ?? type.Body.HalfHeight, station.Get<SpatialState>().Position,
            actor.Get<SpatialState>().Position, TypeOf(actor).Body, Width, Depth);
    }

    private bool DepletedSupply(EntityId id) => id != default &&
        (!TryGetEntity(id, out var source) || source.Has<MineState>() && source.Get<MineState>().Remaining == 0);

    private bool CanApproachSupply(int type, WorldPosition from, WorldPosition point) =>
        StopAllowed(type, point) && (_navigation is null || Resolver(type).CanReach(from, point, StepCancellation));

    private void ApproachFootprint(int index, Entity station)
    {
        var actor = _entityHandles[index];
        if (actor.Get<MotionState>().Moving) return;
        var identity = actor.Get<EntityIdentity>();
        if ((Tick + identity.Id.Value) % 4 != 0) return;
        var position = actor.Get<SpatialState>().Position;
        _dockPoints.Clear();
        _dockPoints.AddRange(SupplyExits(actor, station));
        _dockPoints.Sort((a, b) =>
        {
            int order = ObjectGeometry.Squared(position, a).CompareTo(ObjectGeometry.Squared(position, b));
            if (order != 0) return order;
            order = a.X.CompareTo(b.X);
            return order != 0 ? order : a.Y.CompareTo(b.Y);
        });
        foreach (var point in _dockPoints)
            if (CanApproachSupply(identity.Type, position, point) && SetTarget(index, point) && actor.Get<MotionState>().Moving)
                return;
    }

    private void BeginSupplyOrders() => _supplyDocks.Clear();

    private void UpdateSupplyOrder(int index)
    {
        var actor = _entityHandles[index];
        ref var truck = ref actor.Get<TruckState>();
        ref var orders = ref actor.Get<OrderState>();
        uint owner = actor.Get<OwnershipState>().Owner;
        if (!truck.Automatic || owner == 0 || orders.HasOrder)
        {
            truck.Station = default;
            truck.Arrival = 0;
            return;
        }

        if (!TrySupplyStation(truck.Station, owner, truck.Returning, out var station))
        {
            Halt(index);
            truck.Arrival = 0;
            var next = default(EntityId);
            if (!truck.Returning && truck.Station != default && truck.Cargo > 0)
            {
                if (DepletedSupply(truck.Station))
                    next = NearestSupplyStation(actor, owner, false, (long)NearbySupplyRange * NearbySupplyRange);
                truck.Returning = next == default;
            }
            if (DepletedSupply(truck.Mine)) truck.Mine = default;
            var pin = truck.Returning ? truck.Depot : truck.Mine;
            // An explicitly assigned station survives ownership changes and temporary outages.
            if (pin != default && !TryGetEntity(pin, out _))
            {
                if (truck.Returning) truck.Depot = default;
                else truck.Mine = default;
                pin = default;
            }
            truck.Station = next != default ? next : pin != default ? pin : NearestSupplyStation(actor, owner, truck.Returning);
            if (!TrySupplyStation(truck.Station, owner, truck.Returning, out station))
            {
                orders.OrderPhase = OrderPhase.Waiting;
                return;
            }
        }
        if (ActorDistanceSquared(actor, station) > (long)DockRange * DockRange ||
            !StopAllowed(actor.Get<EntityIdentity>().Type, actor.Get<SpatialState>().Position))
        {
            truck.Arrival = 0;
            ApproachFootprint(index, station);
            orders.OrderPhase = actor.Get<MotionState>().Moving ? OrderPhase.Approaching : OrderPhase.Waiting;
            return;
        }

        Halt(index);
        if (truck.Arrival == 0) truck.Arrival = Tick;
        orders.OrderPhase = OrderPhase.Waiting;
        if (!_supplyDocks.TryGetValue(truck.Station.Value, out var dock) || truck.Arrival < dock.Arrival)
            _supplyDocks[truck.Station.Value] = (actor.Get<EntityIdentity>().Id, truck.Arrival);
    }

    private void AdvanceSupply()
    {
        if (_supplyDocks.Count == 0) return;
        bool removedSource = false;
        foreach (var (id, dock) in _supplyDocks)
        {
            if (!TryGetEntity(dock.Truck, out var actor) || !IsOnMap(actor)) continue;
            ref var truck = ref actor.Get<TruckState>();
            uint owner = actor.Get<OwnershipState>().Owner;
            if (owner == 0 || !TrySupplyStation(new(id), owner, truck.Returning, out var station)) continue;
            actor.Get<OrderState>().OrderPhase = OrderPhase.Engaging;
            if (truck.Returning)
            {
                int transfer = Math.Min(4, truck.Cargo);
                _economy.Credit(owner, transfer);
                truck.Cargo -= transfer;
                if (truck.Cargo > 0) continue;
                SwitchSupplyLeg(ref truck);
            }
            else
            {
                ref var mine = ref station.Get<MineState>();
                int capacity = TypeOf(actor).Supply!.Capacity;
                int rate = station.Get<OwnershipState>().Owner == 0 ? 1 : 2;
                int transfer = Math.Min(rate, Math.Min(mine.Remaining, capacity - truck.Cargo));
                mine.Remaining -= transfer;
                truck.Cargo += transfer;
                if (truck.Cargo == capacity) SwitchSupplyLeg(ref truck);
                if (mine.Remaining == 0 && TypeOf(station).Supply!.RemoveWhenEmpty)
                {
                    DestroyIndex(_indices[new(id)], wreck: false);
                    removedSource = true;
                }
            }
        }
        if (removedSource) RebuildMotionIndex();
    }

    private static void SwitchSupplyLeg(ref TruckState truck)
    {
        truck.Returning = !truck.Returning;
        truck.Station = default;
        truck.Arrival = 0;
    }

    internal void CopySupplySources(List<SupplySourcePose> destination)
    {
        destination.Clear();
        foreach (uint value in _supplyMines)
        {
            var id = new EntityId(value);
            var source = _entityHandles[_indices[id]];
            destination.Add(new(id, source.Get<EntityIdentity>().Type, source.Get<SpatialState>().Position,
                source.Get<MineState>().Remaining, TypeOf(source).Supply!.Capacity));
        }
    }

    internal int ReplenishSupply(EntityId id, int amount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        if (!TryGetEntity(id, out var source) || !source.Has<MineState>()) return 0;
        ref int stock = ref source.Get<MineState>().Remaining;
        int added = Math.Min(amount, TypeOf(source).Supply!.Capacity - stock);
        stock += added;
        return added;
    }

    internal bool TrySpawnSupplySource(int unitType, WorldPosition position, out EntityId created)
    {
        ValidateType(unitType);
        var type = _types[unitType];
        if (type.Supply?.Role != SupplyRole.Mine || type.Footprint is not null)
            throw new ArgumentException("A spawned supply source must be a mine without a footprint.", nameof(unitType));
        created = default;
        var probe = type.Body.Kind == BodyKind.None ? BodyShape.Circle(16) : type.Body;
        if (position.X < probe.HalfWidth || position.X > Width - probe.HalfWidth ||
            position.Y < probe.HalfHeight || position.Y > Depth - probe.HalfHeight || !StopAllowed(unitType, position) ||
            (_motion ?? _combatIndex) is { } index && !index.ClearPlacement(position, out _, probe)) return false;
        created = SpawnFromRule(unitType, GroundPosition(position, position.Elevation), 0);
        return true;
    }

    private static SupplyPose ReadSupply(Entity actor, UnitDefinition type, ref uint states)
    {
        if (actor.Has<TruckState>())
        {
            var truck = actor.Get<TruckState>();
            if (truck.Cargo > 0 && type.States.Contains("loaded")) states |= type.StateMask("loaded");
            return new(truck.Cargo, type.Supply!.Capacity, truck.Automatic, truck.Mine, truck.Depot, truck.Station);
        }
        return actor.Has<MineState>() ? new(actor.Get<MineState>().Remaining, type.Supply!.Capacity, false, default, default, default) : default;
    }

    private static void WriteSupply(BinaryWriter writer, Entity actor)
    {
        if (actor.Has<TruckState>())
        {
            var truck = actor.Get<TruckState>();
            writer.Write(truck.Automatic); writer.Write(truck.Returning); writer.Write(truck.Cargo);
            writer.Write(truck.Mine.Value); writer.Write(truck.Depot.Value); writer.Write(truck.Station.Value); writer.Write(truck.Arrival);
        }
        else if (actor.Has<MineState>()) writer.Write(actor.Get<MineState>().Remaining);
    }
}
