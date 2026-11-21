using System.Security.Cryptography;
using System.Text;
using Frent;
using OpenRTS.Core.Simulation;
using OpenRTS.Core.Production;
using OpenRTS.Core.Visibility;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private string? _snapshotOrigin;
    public const int MaximumSnapshotBytes = 64 * 1024 * 1024;

    /// <summary>A versioned canonical runtime snapshot taken on the world owner at a completed tick.</summary>
    public byte[] CaptureSnapshot(CancellationToken cancellationToken = default)
    {
        RequireCompletedBoundary(); CheckSnapshotMechanics();
        string hash = Report().StateHash;
        _snapshotOrigin ??= hash;
        int version = _projectiles.Any(shot => shot.LaunchAnchor.HasValue) ? 4 :
            _projectiles.Any(shot => shot.FanDirection != default) ? 3 :
            _projectiles.Any(shot => shot.Departure != default) ? 2 : 1;
        using var payload = new SnapshotBuffer(MaximumSnapshotBytes);
        using (var writer = new BinaryWriter(payload, Encoding.UTF8, true))
        {
            writer.Write(_snapshotOrigin); writer.Write(hash);
            WriteRuntimeCore(writer);
            SnapshotState(new(null, writer, cancellationToken), version);
        }
        if (payload.Length > MaximumSnapshotBytes) throw new InvalidDataException("Runtime snapshot exceeds 64 MiB.");
        using var output = new MemoryStream(); using var envelope = new BinaryWriter(output);
        envelope.Write("ORTS-RUNTIME"); envelope.Write(version); envelope.Write((int)payload.Length);
        envelope.Write(SHA256.HashData(payload.GetBuffer().AsSpan(0, (int)payload.Length)));
        payload.WriteTo(output); return output.ToArray();
    }

    /// <summary>Creates and owns a fresh matching scenario; partial or mismatching worlds are disposed on failure.</summary>
    public static SimulationWorld RestoreSnapshot(ReadOnlySpan<byte> snapshot, Func<SimulationWorld> createWorld,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.Length > MaximumSnapshotBytes + 128) throw new InvalidDataException("Runtime snapshot byte budget exceeded.");
        using var stream = new MemoryStream(snapshot.ToArray(), false); using var reader = new BinaryReader(stream);
        // Fixed-size header avoids trusting a length-prefixed string from the network.
        if (!reader.ReadBytes(13).AsSpan().SequenceEqual(new byte[] { 12, (byte)'O', (byte)'R', (byte)'T', (byte)'S', (byte)'-', (byte)'R', (byte)'U', (byte)'N', (byte)'T', (byte)'I', (byte)'M', (byte)'E' }))
            throw new InvalidDataException("Unknown runtime snapshot format.");
        int version = reader.ReadInt32();
        if (version is < 1 or > 4) throw new InvalidDataException("Unknown runtime snapshot format.");
        int length = reader.ReadInt32(); byte[] digest = reader.ReadBytes(32);
        if (length < 130 || length > MaximumSnapshotBytes || length != stream.Length - stream.Position ||
            !CryptographicOperations.FixedTimeEquals(digest, SHA256.HashData(snapshot[^length..])))
            throw new InvalidDataException("Truncated or damaged runtime snapshot.");
        // Hash strings have exactly 64 ASCII bytes and a one-byte length prefix.
        string ReadHash() { if (reader.ReadByte() != 64) throw new InvalidDataException("Invalid snapshot hash."); string value = Encoding.ASCII.GetString(reader.ReadBytes(64)); if (value.Length != 64 || !value.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid snapshot hash."); return value; }
        string origin = ReadHash(), hash = ReadHash();
        SimulationWorld? world = null;
        try
        {
            world = createWorld(); world.RequireCompletedBoundary(); world.CheckSnapshotMechanics();
            if (world.Tick != 0 || world.Report().StateHash != origin) throw new InvalidDataException("Snapshot scenario, content or rules mismatch.");
            world.RestoreRuntimeCore(reader); world._snapshotOrigin = origin;
            world.SnapshotState(new(reader, null, cancellationToken), version);
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing runtime snapshot bytes.");
            cancellationToken.ThrowIfCancellationRequested();
            if (world.Report().StateHash != hash) throw new InvalidDataException("Restored runtime state hash mismatch.");
            return world;
        }
        catch { world?.Dispose(); throw; }
    }

    private void CheckSnapshotMechanics()
    {
        if (_capture is { SupportsSnapshot: false } || _transport is { SupportsSnapshot: false } ||
            _construction is { SupportsSnapshot: false } || _navigation is { SupportsSnapshot: false } ||
            _endOfTick is not null and not IRuntimeSnapshotMechanic)
            throw new NotSupportedException("Custom stateful mechanics need their own runtime snapshot schema; replay recovery remains available.");
    }

    private void SnapshotState(RuntimeArchive a, int version)
    {
        RegisterSnapshotLayouts(a);
        if (_baseGrid is not null)
        {
            if (a.Reading) _baseGrid = _baseGrid.ReadRuntimeState(a.Reader!); else _baseGrid.WriteRuntimeState(a.Writer!);
            var grid = a.Reading ? _baseGrid.ReadRuntimeState(a.Reader!) : _navigation!.Grid;
            if (a.Reading) _navigation!.SetGrid(grid); else grid.WriteRuntimeState(a.Writer!);
        }
        a.Fields(this, "_orderGroupSequence", "_restrictPlayerCommands", "_nextProjectileId", "_bulkAllowed", "_groupSlots", "_teams",
            "_fieldDeployment", "_transformations");
        _economy.Snapshot(a); _production.Snapshot(a); _capture?.Snapshot(a); a.Custom(_endOfTick); _construction?.Snapshot(a); _transport?.Snapshot(a);
        var actors = a.Reading ? [] : _entityHandles.AsSpan(0, Count).ToArray().OrderBy(e => e.Get<EntityIdentity>().Id.Value).ToArray();
        int count = a.Count(Count);
        if (a.Reading)
        {
            _motion?.Clear(0); _combatIndex?.Clear(0); _airIndex?.Clear(0);
            _capture?.BeginSnapshotRestore();
            _footprints.Clear(); _supplyMines.Clear(); _supplyDepots.Clear(); _upgradeProjects.Clear();
            _aircraft.Clear(); _airbases.Clear(); _flightOrder = null;
        }
        uint previousId = 0;
        for (int i = 0; i < count; i++)
        {
            Entity entity = a.Reading ? default : actors[i];
            var identity = a.Value(a.Reading ? default : entity.Get<EntityIdentity>());
            int rotation = a.Value(a.Reading ? 0 : RotationOf(entity));
            if (a.Reading)
            {
                if (identity.Id.Value <= previousId) throw new InvalidDataException("Actor IDs must be strictly increasing.");
                previousId = identity.Id.Value;
                ValidateType(identity.Type);
                if (rotation is < 0 or > 3) throw new InvalidDataException("Invalid actor orientation.");
                ReserveRestoredEntityId(identity.Id);
                Spawn(identity.Type, default, id: identity.Id, quarterTurns: rotation);
                entity = _entityHandles[_indices[identity.Id]];
            }
            a.Component<SpatialState>(entity); a.Component<HealthState>(entity); a.Component<OrderState>(entity);
            a.Component<MotionState>(entity); a.Component<NavigationAgentState>(entity); a.Component<CombatState>(entity);
            a.Component<PresentationState>(entity); a.Component<OwnershipState>(entity); a.Component<ProducerState>(entity);
            a.Component<FlightState>(entity); a.Component<TruckState>(entity); a.Component<MineState>(entity); a.Component<UpgradeState>(entity);
            _economy.SnapshotEntity(a, entity);
            _capture?.SnapshotEntity(a, entity);
            _transport?.SnapshotEntity(a, entity);
            _construction?.SnapshotEntity(a, entity);
            if (a.Reading && entity.Has<UpgradeState>() && entity.Get<UpgradeState>().Project >= 0) _upgradeProjects.Add(identity.Id.Value);
        }
        a.Fields(this, "_airSlots", "_runways");
        int shots = a.Count(_projectiles.Count);
        if (a.Reading) _projectiles.Clear();
        for (int i = 0; i < shots; i++)
        {
            var shot = a.Value(a.Reading ? default : _projectiles[i]);
            if (version >= 2) shot.Departure = a.Value(shot.Departure);
            if (version >= 3) shot.FanDirection = a.Value(shot.FanDirection);
            if (version >= 4) shot.LaunchAnchor = a.Value(shot.LaunchAnchor);
            if (a.Reading)
            {
                ValidateType(shot.UnitType);
                shot.Weapon = _types[shot.UnitType].Weapons[shot.Mount].Weapon;
                if (shot.Departure != default && (shot.Departure.Tick != shot.BornTick || shot.Departure.Source != shot.Source))
                    throw new InvalidDataException("Projectile departure does not match the active shot.");
                if (shot.LaunchAnchor is { } launch && (shot.Departure == default || launch.Distance < 1))
                    throw new InvalidDataException("Invalid projectile launch anchor.");
                _projectiles.Add(shot);
            }
        }
        bool grants = a.Value(_conditionGrants is not null);
        if (grants) { _conditionGrants ??= new(); _conditionGrants.Snapshot(a); } else if (a.Reading) _conditionGrants = null;
        _navigation?.Snapshot(a); _fog?.Snapshot(a); _sensors?.Snapshot(a);
        if (a.Reading)
        {
            _production.InvalidateProducerOrder(); _economy.InvalidatePower(); _orderDirty = true;
            _visionSources.Clear(); _nextVisionSources.Clear(); _commandVision.Clear();
            RebuildMotionIndex();
        }
    }

    private static void RegisterSnapshotLayouts(RuntimeArchive a)
    {
        a.Layout<EntityId>("Value"); a.Layout<EntityIdentity>("Id", "Type");
        a.Layout<WorldPosition>("X", "Y", "Elevation"); a.Layout<FacingDirection>("X", "Y");
        a.Layout<AnimationPlayback>("Animation", "StartTick", "DurationTicks");
        a.Layout<SpatialState>("Position", "Previous"); a.Layout<HealthState>("Current"); a.Layout<OwnershipState>("Owner");
        a.Layout<MotionState>("Target", "Moving", "MovementRemainder");
        a.Layout<UnitOrder>("Kind", "Position", "Target", "Group", "Cohort", "Ability", "ContactId", "BoardingRevision");
        a.Layout<OrderState>("ActiveOrder", "SuspendedOrder", "HasOrder", "AutoBuild", "OrderPhase", "PendingOrders", "CombatTarget", "OrderStarted");
        a.Layout<NavigationAgentState>("TerrainStranded", "GroupMember", "GoalCell", "OrderGroup", "WaitTicks", "PathLength", "PathCursor", "YieldFor", "YieldDeadline", "LastProgressTick", "LocalTarget", "Route", "LocalPath");
        a.Layout<NavigationRoute>("Points", "Revision", "Cursor", "Waypoint", "HasWaypoint", "CostPrefix");
        a.Layout<WeaponState>("Target", "ChargingTarget", "ReadyTick", "FireAtTick", "BurstRemaining", "AutoCast", "PreviousFacing", "Facing", "Animation");
        a.Layout<CombatState>("Weapons", "WeaponReadyTick");
        a.Layout<PresentationState>("VisualStates", "BodyFacing", "PreviousBodyFacing", "WeaponFacing", "PreviousWeaponFacing", "BodyAnimation", "WeaponAnimation", "BodyAnimationOverride", "HasAim", "AimPoint", "AimEntity");
        a.Layout<ProducerState>("Queues", "HasRally", "Rally"); a.Layout<ProductionTask>("Id", "Product", "Progress", "Paid", "Remainder", "Payer", "Repeat", "Ready", "Blocked");
        a.Layout<FlightState>("Phase", "Heading", "Speed", "Ammo", "ReloadWork", "Base", "Slot", "ReturnRequested", "LandRequested", "MovementRequested", "RunwayReserved", "TaxiToParking", "BoardingRevision", "WorkTarget", "WorkStart", "WorkAnchor", "WorkingTick", "LandingPoint", "ExitPoint", "RequestTick", "RetryTick");
        a.Layout<TruckState>("Automatic", "Returning", "Mine", "Depot", "Station", "Cargo", "Arrival"); a.Layout<MineState>("Remaining");
        a.Layout<UpgradeState>("Completed", "Project", "Work", "Support", "Payer");
        a.Layout<ProjectileDeparture>("Tick", "Source", "Origin", "BodyFacing", "MountFacing");
        a.Layout<ProjectileLaunch>("Origin", "Distance", "Observers");
        a.LayoutOmitting<ProjectileState>(["Weapon", "Departure", "FanDirection", "LaunchAnchor"], "Id", "BornTick", "Source", "Target", "Owner", "UnitType", "Mount", "Remaining", "InitialDistance", "Previous", "Position", "Destination", "Hover", "LaunchElevation");
        a.Layout<TransformationClip>("Animation", "Ticks", "Facing"); a.Layout<TransformationRecipe>("Target", "Before", "After", "AllowRotation");
        a.Layout<PendingTransformation>("Recipe", "Position", "Turns", "MoveAfter", "Phase", "UntilTick", "LastProgress", "NextClearance", "LastPosition");
    }
}
