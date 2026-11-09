using Frent;
using OpenRTS.Core.Simulation;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private readonly SortedSet<EntityId> _aircraft = new(Comparer<EntityId>.Create((a, b) => a.Value.CompareTo(b.Value)));
    private readonly SortedSet<EntityId> _airbases = new(Comparer<EntityId>.Create((a, b) => a.Value.CompareTo(b.Value)));
    private readonly Dictionary<(EntityId Base, int Slot), EntityId> _airSlots = new();
    private readonly Dictionary<EntityId, EntityId> _runways = new();
    private readonly List<EntityId> _flightCandidates = [];
    private static readonly Comparison<EntityId> FlightIdComparison = static (a, b) => a.Value.CompareTo(b.Value);
    private MotionIndex? _airIndex;
    private bool _flightSurfaceValidated;

    private void ValidateFlightSurface()
    {
        if (_flightSurfaceValidated) return;
        if (_baseGrid is { HasSurface: true } grid)
        {
            int plane = grid.SurfaceAt(0).Elevation;
            for (int cell = 1; cell < grid.CellCount; cell++)
                if (grid.SurfaceAt(cell).Elevation != plane)
                    throw new ArgumentException("The current aircraft controller requires a flat flight surface; variable-height flight needs terrain anticipation.");
        }
        _flightSurfaceValidated = true;
    }

    private EntityId[]? _flightOrder;
    private EntityId[] FlightOrder() => _flightOrder ??= _aircraft.ToArray();
    private EntityId[] _serviceOrder = [];
    private Comparison<EntityId>? _serviceComparison;

    public bool TryGetFlight(EntityId id, out FlightSnapshot snapshot)
    {
        ThrowIfDisposed(); snapshot = default;
        if (!TryGetEntity(id, out var actor) || !actor.Has<FlightState>()) return false;
        var f = actor.Get<FlightState>(); var definition = TypeOf(actor).Flight!;
        snapshot = new(f.Phase, f.Ammo, definition.Ammo, f.Base, f.Slot, Airborne(actor)); return true;
    }

    private bool IsAircraft(Entity actor) => actor.Has<FlightState>();
    private bool Airborne(Entity actor) => IsAircraft(actor) && actor.Get<SpatialState>().Position.Elevation > GroundHeight(actor.Get<SpatialState>().Position) + 256;
    private bool GroundContact(Entity actor) => !IsAircraft(actor) || actor.Get<SpatialState>().Position.Elevation <= GroundHeight(actor.Get<SpatialState>().Position) + 512;
    private int GroundHeight(WorldPosition point) => GroundPosition(point, 0).Elevation;
    private TargetCategories CategoriesOf(Entity actor) => Airborne(actor) ? TypeOf(actor).Flight!.Categories : TypeOf(actor).TargetCategories;
    private bool FlightPlacementAllowed(UnitDefinition type, WorldPosition point, uint owner)
    {
        ValidateFlightSurface();
        if (point.X < type.Body.HalfWidth || point.Y < type.Body.HalfHeight || point.X > Width - type.Body.HalfWidth || point.Y > Depth - type.Body.HalfHeight) return false;
        if (point.Elevation > GroundHeight(point) + 256) return true;
        EntityId ignored = default;
        foreach (var baseId in _airbases)
        {
            var provider = _entityHandles[_indices[baseId]]; var definition = TypeOf(provider).Airbase!;
            if (provider.Get<OwnershipState>().Owner != owner || IsConstructing(provider) || !definition.Accepts.Contains(type.Flight!.ServiceType) || type.Flight.NeedsRunway && !definition.RunwayStart.HasValue) continue;
            for (int slot = 0; slot < definition.Slots.Count; slot++)
                if (BasePoint(provider, definition.Slots[slot]) == point && !_airSlots.ContainsKey((baseId, slot))) { ignored = baseId; break; }
            if (ignored.Value != 0) break;
        }
        if (ignored.Value == 0 && point.Elevation != GroundHeight(point)) return false;
        if (ignored.Value == 0 && _navigation is not null && !TerrainGeometry.CanStop(_navigation.Grid, point, type.Body, type.Locomotor)) return false;
        if (ignored.Value == 0 && type.Flight!.NeedsRunway) return false;
        (_combatIndex ?? _motion)?.Query(point, point, Math.Max(type.Body.HalfWidth, type.Body.HalfHeight), _flightCandidates);
        foreach (var id in _flightCandidates)
        {
            if (id == ignored || !TryGetEntity(id, out var other) || !GroundContact(other)) continue;
            var p = other.Get<SpatialState>().Position; var otherType = TypeOf(other);
            if (otherType.Footprint is { } footprint)
            {
                foreach (var cell in footprint.Cells) if (BodyGeometry.SweptBox(point, point, footprint.Bounds(cell, p), type.Body)) return false;
            }
            else if (BodyGeometry.Overlap(point, point, type.Body, p, p, otherType.Body)) return false;
        }
        return true;
    }
    private bool WeaponAccepts(WeaponDefinition weapon, Entity actor) => weapon.CanTarget(CategoriesOf(actor));
    private bool FlightCanFire(Entity actor)
    {
        if (!IsAircraft(actor)) return true;
        var f = actor.Get<FlightState>(); var type = TypeOf(actor).Flight!;
        return f.Phase is FlightPhase.Flying or FlightPhase.Hovering or FlightPhase.Loitering &&
            !f.ReturnRequested && (type.Ammo == 0 || f.Ammo > 0);
    }

    private bool FlightFiringArc(Entity actor, WorldPosition target)
    {
        if (!IsAircraft(actor)) return true;
        var d = FlightMath.Direction(actor.Get<FlightState>().Heading); var p = actor.Get<SpatialState>().Position;
        long x = (long)target.X - p.X, y = (long)target.Y - p.Y;
        if (x == 0 && y == 0) return TypeOf(actor).Flight!.Mode == FlightMode.Hover;
        long dot = x * d.X + y * d.Y;
        long cross = x * d.Y - y * d.X;
        return dot > 0 && Math.Abs(cross) <= dot;
    }

    private void InitializeFlight(Entity actor, UnitDefinition type, EntityId id)
    {
        if (type.Flight is { } definition)
        {
            var position = actor.Get<SpatialState>().Position;
            actor.Add(new FlightState { Ammo = definition.Ammo,
                Phase = position.Elevation > GroundHeight(position) + 256 ? FlightPhase.Flying : FlightPhase.Landed });
            foreach (var baseId in _airbases)
            {
                var provider = _entityHandles[_indices[baseId]]; var airbase = TypeOf(provider).Airbase!;
                if (!CompatibleAirbase(actor, provider)) continue;
                for (int slot = 0; slot < airbase.Slots.Count; slot++)
                    if (BasePoint(provider, airbase.Slots[slot]) == position && !_airSlots.ContainsKey((baseId, slot)))
                    {
                        var f = actor.Get<FlightState>(); f.Base = baseId; f.Slot = slot; f.LandingPoint = position;
                        _airSlots[(baseId, slot)] = id;
                        break;
                    }
                if (actor.Get<FlightState>().Base.Value != 0) break;
            }
            _aircraft.Add(id); _flightOrder = null;
        }
        if (type.Airbase is not null) _airbases.Add(id);
    }

    private bool TryProduceAircraft(Entity producer, int type, out EntityId created)
    {
        created = default;
        var flight = _types[type].Flight!; var airbase = TypeOf(producer).Airbase;
        if (airbase is null || !airbase.Accepts.Contains(flight.ServiceType) || flight.NeedsRunway && !airbase.RunwayStart.HasValue) return false;
        var baseId = producer.Get<EntityIdentity>().Id;
        for (int slot = 0; slot < airbase.Slots.Count; slot++)
        {
            if (_airSlots.ContainsKey((baseId, slot))) continue;
            var point = BasePoint(producer, airbase.Slots[slot]);
            var body = _types[type].Body;
            if (point.X < body.HalfWidth || point.Y < body.HalfHeight || point.X > Width - body.HalfWidth || point.Y > Depth - body.HalfHeight) continue;
            (_combatIndex ?? _motion)!.Query(point, point, Math.Max(_types[type].Body.HalfWidth, _types[type].Body.HalfHeight), _flightCandidates);
            bool occupied = false;
            foreach (var otherId in _flightCandidates)
            {
                if (otherId == baseId || !TryGetEntity(otherId, out var other) || !GroundContact(other)) continue;
                var otherType = TypeOf(other); var p = other.Get<SpatialState>().Position;
                if (otherType.Footprint is { } footprint)
                {
                    foreach (var cell in footprint.Cells)
                        occupied |= BodyGeometry.SweptBox(point, point, footprint.Bounds(cell, p), _types[type].Body);
                }
                else occupied |= BodyGeometry.Overlap(point, point, _types[type].Body, p, p, otherType.Body);
            }
            if (occupied) continue;
            created = Spawn(type, point, producer.Get<OwnershipState>().Owner);
            var f = _entityHandles[_indices[created]].Get<FlightState>(); f.Base = baseId; f.Slot = slot; f.LandingPoint = point;
            _airSlots[(baseId, slot)] = created;
            return true;
        }
        return false;
    }

    private void ReleaseAirAssignment(EntityId id, FlightState f, bool force = false)
    {
        if (f.RunwayReserved && _runways.GetValueOrDefault(f.Base) == id) _runways.Remove(f.Base);
        f.RunwayReserved = false;
        if (!force && TryGetEntity(f.Base, out var provider) && TypeOf(provider).Airbase?.KeepsParking == true) return;
        if (_airSlots.GetValueOrDefault((f.Base, f.Slot)) == id) _airSlots.Remove((f.Base, f.Slot));
        f.Base = default; f.Slot = -1;
    }

    private void BeforeFlightOrder(Entity actor)
    {
        if (!IsAircraft(actor)) return;
        var f = actor.Get<FlightState>();
        ReleaseAirAssignment(actor.Get<EntityIdentity>().Id, f);
        f.ReturnRequested = f.LandRequested = false; f.ReloadWork = 0; f.ExitPoint = default;
        if (f.Phase is FlightPhase.Approaching or FlightPhase.Landing) f.Phase = FlightPhase.TakingOff;
        if (f.Phase is FlightPhase.Taxiing or FlightPhase.RunwayTakeoff or FlightPhase.RunwayLanding)
            f.Phase = GroundContact(actor) ? FlightPhase.Landed : FlightPhase.TakingOff;
        if (f.Phase == FlightPhase.Servicing) f.Phase = FlightPhase.Landed;
    }

    private bool StartFlightOrder(Entity actor, UnitOrder order)
    {
        if (!IsAircraft(actor)) return false;
        var f = actor.Get<FlightState>(); var definition = TypeOf(actor).Flight!;
        if (order.Kind == UnitActionKind.Land && definition.NeedsRunway) return false;
        if (order.Kind == UnitActionKind.Land && _navigation is not null &&
            !TerrainGeometry.CanStop(_navigation.Grid, order.Position, TypeOf(actor).Body, TypeOf(actor).Locomotor)) return false;
        if (order.Kind == UnitActionKind.ReturnToBase && order.Target.Value != 0 &&
            (!TryGetEntity(order.Target, out var provider) || !CompatibleAirbase(actor, provider))) return false;
        BeforeFlightOrder(actor);
        f.RequestTick = Tick; f.RetryTick = 0;
        f.ReturnRequested = order.Kind == UnitActionKind.ReturnToBase;
        f.LandRequested = order.Kind == UnitActionKind.Land;
        if (f.LandRequested) f.LandingPoint = GroundPosition(order.Position, 0);
        if (f.ReturnRequested && order.Target.Value != 0 && f.Base != order.Target) ReleaseAirAssignment(actor.Get<EntityIdentity>().Id, f, true);
        actor.Get<MotionState>().Moving = true;
        return true;
    }

    private bool CompatibleAirbase(Entity actor, Entity provider)
    {
        var flight = TypeOf(actor).Flight!; var airbase = TypeOf(provider).Airbase;
        return airbase is not null && actor.Get<OwnershipState>().Owner == provider.Get<OwnershipState>().Owner &&
            !IsConstructing(provider) && airbase.Accepts.Contains(flight.ServiceType) &&
            (!flight.NeedsRunway || airbase.RunwayStart.HasValue);
    }

    private void DetachAirbase(EntityId baseId)
    {
        foreach (var id in FlightOrder())
        {
            var actor = _entityHandles[_indices[id]]; var f = actor.Get<FlightState>();
            if (f.Base != baseId) continue;
            ReleaseAirAssignment(id, f, true);
            if (f.Phase == FlightPhase.Servicing) f.Phase = FlightPhase.Landed;
            else if (f.Phase is FlightPhase.Taxiing or FlightPhase.RunwayTakeoff or FlightPhase.RunwayLanding or FlightPhase.Landing or FlightPhase.Approaching)
                f.Phase = GroundContact(actor) ? FlightPhase.Landed : FlightPhase.TakingOff;
        }
    }

    private WorldPosition BasePoint(Entity provider, WorldPosition offset)
    {
        var p = provider.Get<SpatialState>().Position;
        var rotated = RotationOf(provider) switch { 1 => new WorldPosition(-offset.Y, offset.X), 2 => new(-offset.X, -offset.Y), 3 => new(offset.Y, -offset.X), _ => offset };
        return new(p.X + rotated.X, p.Y + rotated.Y, p.Elevation);
    }

    private bool PointOnAirbase(Entity provider, WorldPosition point)
    {
        var type = TypeOf(provider); var center = provider.Get<SpatialState>().Position;
        foreach (var cell in type.Footprint!.Cells)
        {
            var box = type.Footprint.Bounds(cell, center);
            if (point.X >= box.MinX && point.X <= box.MaxX && point.Y >= box.MinY && point.Y <= box.MaxY) return true;
        }
        return false;
    }

    private bool AssignAirbase(Entity actor)
    {
        var f = actor.Get<FlightState>(); var id = actor.Get<EntityIdentity>().Id;
        if (f.Base.Value != 0 && TryGetEntity(f.Base, out var home) && CompatibleAirbase(actor, home)) return true;
        ReleaseAirAssignment(id, f, true);
        if (Tick < f.RetryTick) return false;
        f.RetryTick = Tick + 25;
        var p = actor.Get<SpatialState>().Position;
        EntityId best = default; int slot = -1; long distance = long.MaxValue;
        var requested = actor.Get<OrderState>().ActiveOrder;
        foreach (var baseId in _airbases)
        {
            if (requested.Kind == UnitActionKind.ReturnToBase && requested.Target.Value != 0 && baseId != requested.Target &&
                TryGetEntity(requested.Target, out var specified) && CompatibleAirbase(actor, specified)) continue;
            var provider = _entityHandles[_indices[baseId]];
            if (!CompatibleAirbase(actor, provider)) continue;
            if (TypeOf(actor).Flight!.NeedsRunway && actor.Get<FlightState>().Phase == FlightPhase.Landed &&
                !PointOnAirbase(provider, actor.Get<SpatialState>().Position)) continue;
            var definition = TypeOf(provider).Airbase!;
            for (int s = 0; s < definition.Slots.Count; s++)
            {
                if (_airSlots.ContainsKey((baseId, s))) continue;
                var point = BasePoint(provider, definition.Slots[s]); long d = DistanceSquared(p, point);
                if (d >= distance) continue;
                distance = d; best = baseId; slot = s;
            }
        }
        if (slot < 0) return false;
        f.Base = best; f.Slot = slot; _airSlots[(best, slot)] = id;
        f.LandingPoint = BasePoint(_entityHandles[_indices[best]], TypeOf(_entityHandles[_indices[best]]).Airbase!.Slots[slot]);
        return true;
    }

    private bool ReserveRunway(EntityId id, FlightState f)
    {
        if (_runways.TryGetValue(f.Base, out var other) && other != id) return false;
        _runways[f.Base] = id; f.RunwayReserved = true; return true;
    }

    // Suppress only during the ground pass. Orders and published poses retain the aircraft's real motion state.
    private void SuspendAirMovement()
    {
        foreach (var id in FlightOrder())
        {
            var actor = _entityHandles[_indices[id]]; var f = actor.Get<FlightState>();
            f.MovementRequested = actor.Get<MotionState>().Moving; actor.Get<MotionState>().Moving = false;
        }
    }

    private void UpdateFlights()
    {
        if (_aircraft.Count == 0) return;
        var ids = FlightOrder();
        _airIndex ??= new(1024); _airIndex.Clear(ids.Length);
        foreach (var id in ids)
        {
            var actor = _entityHandles[_indices[id]];
            _airIndex.Add(id, actor.Get<SpatialState>().Previous, TypeOf(actor).Body);
        }
        bool hasRequests = false;
        foreach (var id in ids)
        {
            var f = _entityHandles[_indices[id]].Get<FlightState>();
            if (f.ReturnRequested || f.LandRequested) { hasRequests = true; break; }
        }
        if (hasRequests)
        {
            if (_serviceOrder.Length < ids.Length) Array.Resize(ref _serviceOrder, ids.Length);
            ids.CopyTo(_serviceOrder, 0);
            _serviceComparison ??= (a, b) =>
            {
                var af = _entityHandles[_indices[a]].Get<FlightState>(); var bf = _entityHandles[_indices[b]].Get<FlightState>();
                long at = af.ReturnRequested || af.LandRequested ? af.RequestTick : long.MaxValue;
                long bt = bf.ReturnRequested || bf.LandRequested ? bf.RequestTick : long.MaxValue;
                int compare = at.CompareTo(bt); return compare != 0 ? compare : a.Value.CompareTo(b.Value);
            };
            _serviceOrder.AsSpan(0, ids.Length).Sort(_serviceComparison);
        }
        foreach (var id in hasRequests ? _serviceOrder.AsSpan(0, ids.Length) : ids.AsSpan())
        {
            StepCancellation.ThrowIfCancellationRequested();
            var actor = _entityHandles[_indices[id]]; var f = actor.Get<FlightState>();
            ref var motion = ref actor.Get<MotionState>(); motion.Moving = f.MovementRequested;
            var definition = TypeOf(actor).Flight!;
            if (f.Base.Value != 0 && (!TryGetEntity(f.Base, out var host) || !CompatibleAirbase(actor, host)))
            {
                ReleaseAirAssignment(id, f, true);
                if (f.Phase is FlightPhase.Landing or FlightPhase.Approaching or FlightPhase.Taxiing or FlightPhase.RunwayLanding or FlightPhase.RunwayTakeoff)
                    f.Phase = FlightPhase.TakingOff;
                if (f.Phase == FlightPhase.Servicing) f.Phase = FlightPhase.Landed;
            }
            ref var orders = ref actor.Get<OrderState>();
            if (definition.Ammo > 0 && f.Ammo == 0 && !f.ReturnRequested && orders.HasOrder &&
                orders.ActiveOrder.Kind is UnitActionKind.Attack or UnitActionKind.AttackMove)
            { f.ReturnRequested = true; f.RequestTick = Tick; }
            bool go = motion.Moving || definition.Mode == FlightMode.ForwardFlight && orders.HasOrder && orders.ActiveOrder.Kind is UnitActionKind.Attack or UnitActionKind.AttackMove;
            if (f.ReturnRequested)
            {
                if (!AssignAirbase(actor))
                {
                    if (f.Phase is FlightPhase.Landed or FlightPhase.Servicing) motion.Moving = false;
                    else FlyAircraft(actor, motion.Target, false);
                    continue;
                }
                f.LandingPoint = BasePoint(_entityHandles[_indices[f.Base]], TypeOf(_entityHandles[_indices[f.Base]]).Airbase!.Slots[f.Slot]);
            }
            if (f.Phase is FlightPhase.Landed or FlightPhase.Servicing)
            {
                if (f.ReturnRequested && actor.Get<SpatialState>().Position == f.LandingPoint)
                { f.Phase = FlightPhase.Servicing; motion.Moving = false; continue; }
                if (f.ReturnRequested) go = true;
                if (!go && !f.LandRequested) { motion.Moving = false; continue; }
                if (f.LandRequested && DistanceSquared(actor.Get<SpatialState>().Position, f.LandingPoint) == 0)
                { f.LandRequested = false; orders.HasOrder = false; motion.Moving = false; continue; }
                if (definition.NeedsRunway)
                {
                    if (!AssignAirbase(actor) || !ReserveRunway(id, f)) { motion.Moving = true; continue; }
                    f.Phase = FlightPhase.Taxiing; f.TaxiToParking = false;
                }
                else f.Phase = FlightPhase.TakingOff;
            }
            if (f.Phase == FlightPhase.Taxiing)
            {
                var provider = _entityHandles[_indices[f.Base]]; var airbase = TypeOf(provider).Airbase!;
                var start = BasePoint(provider, airbase.RunwayStart!.Value);
                var point = f.TaxiToParking ? BasePoint(provider, airbase.Slots[f.Slot]) : start;
                if (PreciseFlightMove(actor, point, definition.Speed / 2))
                {
                    if (f.TaxiToParking) { f.Phase = FlightPhase.Servicing; ReleaseRunway(id, f); }
                    else f.Phase = FlightPhase.RunwayTakeoff;
                }
                motion.Moving = true; continue;
            }
            if (f.Phase == FlightPhase.RunwayTakeoff)
            {
                var provider = _entityHandles[_indices[f.Base]]; var end = BasePoint(provider, TypeOf(provider).Airbase!.RunwayEnd!.Value);
                var liftOff = end with { Elevation = end.Elevation + Math.Min(768, definition.CruiseAltitude) };
                if (PreciseFlightMove(actor, liftOff, definition.Speed, synchronize: true))
                {
                    f.Phase = FlightPhase.Flying;
                    f.Speed = definition.MinimumSpeed;
                    ReleaseAirAssignment(id, f);
                }
                motion.Moving = true; continue;
            }
            if (f.Phase == FlightPhase.TakingOff)
            {
                ref var position = ref actor.Get<SpatialState>(); int height = GroundHeight(position.Position) + definition.CruiseAltitude;
                position.Position = position.Position with { Elevation = Approach(position.Position.Elevation, height, definition.VerticalSpeed) };
                if (position.Position.Elevation == height) { f.Phase = FlightPhase.Flying; if (!f.ReturnRequested) ReleaseAirAssignment(id, f); }
                motion.Moving = true; continue;
            }
            if (f.ReturnRequested || f.LandRequested)
            {
                if (definition.NeedsRunway && f.ReturnRequested)
                {
                    UpdateRunwayReturn(actor, id, f, definition);
                    motion.Moving = true; continue;
                }
                if (DistanceSquared(actor.Get<SpatialState>().Position, f.LandingPoint) > 0)
                { f.Phase = FlightPhase.Approaching; FlyAircraft(actor, f.LandingPoint, true); motion.Moving = true; continue; }
                f.Phase = FlightPhase.Landing;
                var current = actor.Get<SpatialState>().Position;
                var next = current with { Elevation = Approach(current.Elevation, f.LandingPoint.Elevation, definition.VerticalSpeed) };
                if (LandingClear(actor, current, next, f.Base)) actor.Get<SpatialState>().Position = next;
                else
                {
                    actor.Get<SpatialState>().Position = current with { Elevation = Approach(current.Elevation, GroundHeight(current) + 512, definition.VerticalSpeed) };
                    f.Phase = FlightPhase.Approaching;
                }
                if (actor.Get<SpatialState>().Position == f.LandingPoint)
                {
                    f.Speed = 0; f.LandRequested = false; motion.Moving = false;
                    f.Phase = f.ReturnRequested ? FlightPhase.Servicing : FlightPhase.Landed;
                    if (!f.ReturnRequested && orders.ActiveOrder.Kind != UnitActionKind.UnloadAt) orders.HasOrder = false;
                }
                continue;
            }
            FlyAircraft(actor, motion.Target, go);
        }
        RebuildMotionIndex();
    }

    private static int Approach(int value, int target, int step) => value + Math.Clamp(target - value, -step, step);
    private void ReleaseRunway(EntityId id, FlightState f)
    {
        if (_runways.GetValueOrDefault(f.Base) == id) _runways.Remove(f.Base);
        f.RunwayReserved = false;
    }

    private void UpdateRunwayReturn(Entity actor, EntityId id, FlightState f, FlightDefinition flight)
    {
        var provider = _entityHandles[_indices[f.Base]];
        var airbase = TypeOf(provider).Airbase!;
        var start = BasePoint(provider, airbase.RunwayStart!.Value);
        var end = BasePoint(provider, airbase.RunwayEnd!.Value);
        if (f.Phase == FlightPhase.RunwayLanding)
        {
            if (PreciseFlightMove(actor, end, flight.Speed, synchronize: true))
            {
                f.Phase = FlightPhase.Taxiing;
                f.TaxiToParking = true;
            }
            return;
        }

        if (_runways.TryGetValue(f.Base, out var occupant) && occupant != id)
        {
            FlyAircraft(actor, start, false);
            return;
        }

        var p = actor.Get<SpatialState>().Position;
        var entry = RunwayEntry(start, end, TypeOf(actor).Body, flight);
        entry = entry with { Elevation = Approach(p.Elevation, start.Elevation + flight.CruiseAltitude, flight.VerticalSpeed) };
        f.Phase = FlightPhase.Approaching;
        if (!PreciseFlightMove(actor, entry, flight.Speed) || !ReserveRunway(id, f)) return;
        f.Phase = FlightPhase.RunwayLanding;
    }

    private WorldPosition RunwayEntry(WorldPosition start, WorldPosition end, BodyShape body, FlightDefinition flight)
    {
        long dx = (long)end.X - start.X, dy = (long)end.Y - start.Y;
        long length = (long)SquareRoot((ulong)(dx * dx + dy * dy));
        long distance = Math.Max(3072, ((flight.CruiseAltitude + flight.VerticalSpeed - 1) / flight.VerticalSpeed) * flight.Speed);
        // Shorten along the runway axis so the approach stays reachable even at the map edge.
        if (dx > 0) distance = Math.Min(distance, (start.X - body.HalfWidth) * length / dx);
        if (dx < 0) distance = Math.Min(distance, (Width - body.HalfWidth - start.X) * length / -dx);
        if (dy > 0) distance = Math.Min(distance, (start.Y - body.HalfHeight) * length / dy);
        if (dy < 0) distance = Math.Min(distance, (Depth - body.HalfHeight - start.Y) * length / -dy);
        distance = Math.Max(0, distance);
        return new(start.X - (int)(dx * distance / length), start.Y - (int)(dy * distance / length), start.Elevation);
    }

    private bool PreciseFlightMove(Entity actor, WorldPosition target, int speed, bool synchronize = false)
    {
        var p = actor.Get<SpatialState>().Position; var f = actor.Get<FlightState>();
        int vertical = TypeOf(actor).Flight!.VerticalSpeed;
        speed = Math.Max(1, speed);
        if (synchronize)
        {
            int distance = (int)SquareRoot((ulong)DistanceSquared(p, target));
            int height = Math.Abs(p.Elevation - target.Elevation);
            int ticks = Math.Max(1, Math.Max((distance + speed - 1) / speed, (height + vertical - 1) / vertical));
            speed = Math.Max(1, distance / ticks);
            vertical = Math.Max(1, (height + ticks - 1) / ticks);
        }
        var next = StepTowards(p, target, speed) with { Elevation = Approach(p.Elevation, target.Elevation, vertical) };
        if (!LandingClear(actor, p, next, f.Base)) return false;
        if (p.X != next.X || p.Y != next.Y) FaceFlight(actor, f, FlightMath.Heading(target.X - p.X, target.Y - p.Y));
        actor.Get<SpatialState>().Position = next;
        return next == target;
    }

    private static void FaceFlight(Entity actor, FlightState f, int heading)
    {
        f.Heading = heading;
        actor.Get<PresentationState>().BodyFacing = FlightMath.Direction(heading);
    }

    private bool LandingClear(Entity actor, WorldPosition from, WorldPosition to, EntityId provider)
    {
        if (Math.Min(from.Elevation, to.Elevation) > GroundHeight(to) + 512) return true;
        var type = TypeOf(actor); var id = actor.Get<EntityIdentity>().Id;
        if (provider.Value == 0 && _navigation is not null && !TerrainGeometry.CanStop(_navigation.Grid, to, type.Body, type.Locomotor)) return false;
        (_combatIndex ?? _motion)?.Query(from, to, Math.Max(type.Body.HalfWidth, type.Body.HalfHeight), _flightCandidates);
        foreach (var otherId in _flightCandidates)
        {
            if (otherId == id || otherId == provider || !_indices.TryGetValue(otherId, out int index)) continue;
            var other = _entityHandles[index]; if (!GroundContact(other)) continue;
            var otherType = TypeOf(other); var position = other.Get<SpatialState>();
            if (otherType.Footprint is { } footprint)
            {
                foreach (var cell in footprint.Cells)
                    if (BodyGeometry.SweptBox(from, to, footprint.Bounds(cell, position.Position), type.Body)) return false;
            }
            else if (BodyGeometry.Overlap(from, to, type.Body, position.Previous, position.Position, otherType.Body)) return false;
        }
        return _construction?.SiteMovementClear(from, to, type.Body) != false;
    }

    private void FlyAircraft(Entity actor, WorldPosition target, bool go)
    {
        var type = TypeOf(actor); var definition = type.Flight!; var f = actor.Get<FlightState>();
        var p = actor.Get<SpatialState>().Position;
        ref var motion = ref actor.Get<MotionState>(); var orders = actor.Get<OrderState>();
        bool forward = definition.Mode == FlightMode.ForwardFlight;
        if (forward && !f.ReturnRequested && !f.LandRequested &&
            orders.CombatTarget.Value != 0 && TryGetEntity(orders.CombatTarget, out var enemy))
        {
            var ep = enemy.Get<SpatialState>().Position;
            if (f.ExitPoint != default && DistanceSquared(p, f.ExitPoint) > 512L * 512) target = f.ExitPoint;
            else
            {
                f.ExitPoint = default; target = ep;
                if (DistanceSquared(p, ep) < 1024L * 1024)
                { f.ExitPoint = FlightMath.Forward(p, f.Heading, 4096); target = f.ExitPoint; }
            }
            go = true;
        }
        int desired = go ? FlightMath.Heading((long)target.X - p.X, (long)target.Y - p.Y) : forward ? (f.Heading + definition.TurnRate) & 1023 : f.Heading;
        if (!forward && !go && orders.CombatTarget.Value != 0 && TryGetEntity(orders.CombatTarget, out var aim))
        { var point = aim.Get<SpatialState>().Position; desired = FlightMath.Heading(point.X - p.X, point.Y - p.Y); }
        if (forward && go)
        {
            int radius = Math.Max(1, definition.Speed * 163 / definition.TurnRate);
            int turn = ((desired - f.Heading + 512) & 1023) - 512;
            var center = FlightMath.Forward(p, f.Heading + (turn >= 0 ? 256 : -256), radius);
            if (DistanceSquared(center, target) < (long)radius * radius) desired = f.Heading;
        }
        if (forward)
        {
            int margin = definition.Speed * 180 / definition.TurnRate + 1024;
            var direction = FlightMath.Direction(f.Heading);
            if (p.X < margin && direction.X < 0 || p.X > Width - margin && direction.X > 0 ||
                p.Y < margin && direction.Y < 0 || p.Y > Depth - margin && direction.Y > 0)
                desired = FlightMath.Heading(Width / 2 - p.X, Depth / 2 - p.Y);
        }
        var separation = AirSeparation(actor);
        if (forward && (separation.X != 0 || separation.Y != 0))
        {
            var direction = FlightMath.Direction(desired);
            desired = FlightMath.Heading(direction.X + separation.X * 1024, direction.Y + separation.Y * 1024);
        }
        f.Heading = FlightMath.Turn(f.Heading, desired, definition.TurnRate);
        int speed = forward ? definition.Speed : go ? Math.Min(definition.Speed, (int)SquareRoot((ulong)DistanceSquared(p, target))) : 0;
        f.Speed = Approach(f.Speed, speed, definition.Acceleration);
        if (forward) f.Speed = Math.Max(definition.MinimumSpeed, f.Speed);
        var next = forward ? FlightMath.Forward(p, f.Heading, f.Speed) : go ? StepTowards(p, target, Math.Max(1, f.Speed)) : p;
        if (!forward) next = next with { X = next.X + separation.X, Y = next.Y + separation.Y };
        next = next with { X = Math.Clamp(next.X, type.Body.HalfWidth, Width - type.Body.HalfWidth), Y = Math.Clamp(next.Y, type.Body.HalfHeight, Depth - type.Body.HalfHeight) };
        if (forward && (next.X == type.Body.HalfWidth || next.X == Width - type.Body.HalfWidth || next.Y == type.Body.HalfHeight || next.Y == Depth - type.Body.HalfHeight))
            f.Heading = FlightMath.Heading(Width / 2 - p.X, Depth / 2 - p.Y);
        int surface = Math.Max(GroundHeight(p), GroundHeight(next));
        next = next with { Elevation = Approach(p.Elevation, surface + definition.CruiseAltitude, definition.VerticalSpeed) };
        actor.Get<SpatialState>().Position = next;
        bool reached = forward ? BodyGeometry.SegmentCircle(p, next, target, Math.Max(256, f.Speed * 2)) : next.X == target.X && next.Y == target.Y;
        motion.Moving = go && !reached;
        f.Phase = go ? FlightPhase.Flying : forward ? FlightPhase.Loitering : FlightPhase.Hovering;
        actor.Get<PresentationState>().BodyFacing = FlightMath.Direction(f.Heading);
    }

    private FacingDirection AirSeparation(Entity actor)
    {
        var f = actor.Get<FlightState>();
        if (f.ReturnRequested || f.LandRequested) return default;
        var position = actor.Get<SpatialState>().Previous; var id = actor.Get<EntityIdentity>().Id;
        _airIndex!.QueryLimited(position, 1024, 64, _flightCandidates);
        _flightCandidates.Sort(FlightIdComparison);
        long x = 0, y = 0; int checkedNeighbours = 0;
        foreach (var otherId in _flightCandidates)
        {
            if (otherId == id || !TryGetEntity(otherId, out var other) || !IsAircraft(other)) continue;
            var p = other.Get<SpatialState>().Previous;
            if (Math.Abs((long)p.Elevation - position.Elevation) > 512) continue;
            long dx = (long)position.X - p.X, dy = (long)position.Y - p.Y;
            long squared = dx * dx + dy * dy;
            if (squared >= 1024L * 1024) continue;
            if (squared == 0)
            {
                uint seed = unchecked(Math.Min(id.Value, otherId.Value) * 2654435761u ^ Math.Max(id.Value, otherId.Value));
                var d = FlightMath.Direction((int)(seed & 1023)); int sign = id.Value < otherId.Value ? 1 : -1;
                x += sign * d.X / 1024; y += sign * d.Y / 1024;
            }
            else { x += dx * 8192 / squared; y += dy * 8192 / squared; }
            // shortcut: soft avoidance uses at most 64 canonical broad candidates and 24 close neighbours; exact queries are uncapped.
            if (++checkedNeighbours == 24) break;
        }
        return new((int)Math.Clamp(x, -24, 24), (int)Math.Clamp(y, -24, 24));
    }

    private void AdvanceAirService()
    {
        foreach (var id in FlightOrder())
        {
            var actor = _entityHandles[_indices[id]]; var f = actor.Get<FlightState>();
            if (f.Phase != FlightPhase.Servicing || !TryGetEntity(f.Base, out var provider) || !CompatibleAirbase(actor, provider)) continue;
            if ((ReadConditions(TypeOf(provider), provider.Get<OwnershipState>().Owner) & GameplayConditions.LowPower) != 0) continue;
            var definition = TypeOf(actor).Flight!; var service = TypeOf(provider).Airbase!;
            if (f.Ammo < definition.Ammo && ++f.ReloadWork >= service.ReloadTicks) { f.Ammo++; f.ReloadWork = 0; }
            ref var health = ref actor.Get<HealthState>();
            int before = health.Current; health.Current = Math.Min(TypeOf(actor).MaxHealth, health.Current + service.RepairPerTick);
            if (health.Current != before) EmitEvent(new(Tick, WorldEventKind.Repaired, id, actor.Get<SpatialState>().Position, actor.Get<EntityIdentity>().Type, Amount: health.Current - before));
            if (f.Ammo == definition.Ammo && (service.RepairPerTick == 0 || health.Current == TypeOf(actor).MaxHealth))
            {
                f.ReturnRequested = false; f.Phase = FlightPhase.Landed; f.ExitPoint = default;
                ref var orders = ref actor.Get<OrderState>();
                if (orders.ActiveOrder.Kind == UnitActionKind.ReturnToBase) orders.HasOrder = false;
                actor.Get<MotionState>().Moving = orders.HasOrder && orders.ActiveOrder.Kind is UnitActionKind.Attack or UnitActionKind.AttackMove;
            }
        }
    }
}
