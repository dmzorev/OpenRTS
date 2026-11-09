using OpenRTS.Core.Visibility;

namespace OpenRTS.Core;

public sealed partial class FrameExchange
{
    private static void CaptureFog(Slot slot, SimulationWorld world)
    {
        var players = world.ObservationPlayers;
        slot.RestrictLiveFields = world.UsesSensors;
        slot.FogPlayers = players.ToArray();
        if (world.Fog is not { } fog)
        {
            slot.FogCellSize = slot.FogHoldTicks = slot.FogColumns = slot.FogRows = 0;
            slot.FogStates = []; slot.FogStrength = []; slot.VisionSourceCount = 0;
        }
        else
        {
            slot.VisionSourceCount = world.VisionSources.Length;
            if (slot.VisionSources.Length < slot.VisionSourceCount) Array.Resize(ref slot.VisionSources, slot.VisionSourceCount);
            world.VisionSources.CopyTo(slot.VisionSources);
            slot.FogHoldTicks = fog.HoldTicks;
            slot.FogCellSize = fog.CellSize; slot.FogColumns = fog.Columns; slot.FogRows = fog.Rows;
            slot.FogWidth = fog.Width; slot.FogDepth = fog.Depth;
            if (slot.FogStates.Length != fog.CellCount * players.Count) slot.FogStates = new CellVisibility[fog.CellCount * players.Count];
            fog.CopyStates(slot.FogStates);
            if (fog.HoldTicks > 0)
            {
                if (slot.FogStrength.Length != slot.FogStates.Length) slot.FogStrength = new byte[slot.FogStates.Length];
                fog.CopyStrength(slot.FogStrength);
            }
            else slot.FogStrength = [];
        }
        if (players.Count == 0) return;
        if (world.UsesSensors)
        {
            if (slot.PlayerContacts.Length != players.Count)
                slot.PlayerContacts = Enumerable.Range(0, players.Count).Select(_ => new List<ContactPose>()).ToArray();
            for (int p = 0; p < players.Count; p++) world.CopyContactPoses(p, slot.Poses.AsSpan(0, slot.Count), slot.PlayerContacts[p]);
            if (slot.HealthObservers.Length < slot.Count)
            {
                slot.HealthObservers = new ulong[slot.Count];
                slot.StateObservers = new ulong[slot.Count];
            }
        }
        if (slot.Observers.Length < slot.Count) { slot.Observers = new ulong[slot.Count]; slot.PreviousObservers = new ulong[slot.Count]; }
        for (int i = 0; i < slot.Count; i++)
        {
            ulong mask = 0, previous = 0, health = 0, states = 0;
            for (int p = 0; p < players.Count; p++)
            {
                ulong bit = 1ul << p;
                if (world.ModelObservation(players[p], slot.Poses[i].Id) is { } fields)
                {
                    mask |= bit;
                    if ((fields & LiveObservationFields.Health) != 0) health |= bit;
                    if ((fields & LiveObservationFields.States) != 0) states |= bit;
                }
                if (world.WasVisible(players[p], slot.Poses[i].Id)) previous |= bit;
            }
            slot.Observers[i] = mask; slot.PreviousObservers[i] = previous;
            if (world.UsesSensors) { slot.HealthObservers[i] = health; slot.StateObservers[i] = states; }
        }
    }
    // shortcut: viewer arrays are rebuilt per publication; reuse buffers after multi-view frame profiling.
    private static Slot Filter(Slot source, uint viewer)
    {
        int player = Array.IndexOf(source.FogPlayers, viewer), cells = source.FogColumns * source.FogRows;
        ulong bit = player < 0 ? 0 : 1ul << player;
        var circles = source.VisionSources.Take(source.VisionSourceCount).Where(s => s.Owner == viewer).ToArray();
        var result = new Slot { VisionSources = circles, VisionSourceCount = circles.Length, Viewer = viewer, Tick = source.Tick, Timestamp = source.Timestamp, TickRate = source.TickRate,
            Contacts = player >= 0 && source.RestrictLiveFields ? source.PlayerContacts[player] : null,
            Grid = source.Grid, BaseGrid = source.BaseGrid, FogColumns = source.FogColumns, FogRows = source.FogRows,
            FogCellSize = source.FogCellSize, FogHoldTicks = source.FogHoldTicks, FogWidth = source.FogWidth, FogDepth = source.FogDepth,
            FogStrength = source.FogStrength.Length == 0 ? [] : player < 0 ? new byte[cells] : source.FogStrength.AsSpan(player*cells,cells).ToArray(),
            FogStates = player < 0 ? new CellVisibility[cells] : source.FogStates.AsSpan(player * cells, cells).ToArray() };
        var visible = new HashSet<EntityId>();
        for (int i = 0; i < source.Count; i++) if ((source.Observers[i] & bit) != 0) visible.Add(source.Poses[i].Id);
        var own = new HashSet<EntityId>(); var poses = new List<EntityPose>(); var weapons = new List<WeaponPose>();
        for (int i = 0; i < source.Count; i++)
        {
            var p = source.Poses[i]; if (!visible.Contains(p.Id)) continue;
            bool mine = p.Presentation.Owner == viewer;
            bool health = !source.RestrictLiveFields || (source.HealthObservers[i] & bit) != 0;
            bool states = !source.RestrictLiveFields || (source.StateObservers[i] & bit) != 0;
            if (mine) own.Add(p.Id);
            int start = weapons.Count;
            for (int m = 0; m < p.WeaponCount; m++)
            {
                var w = source.Weapons[p.WeaponOffset + m];
                weapons.Add(w with { Target = visible.Contains(w.Target) ? w.Target : default,
                    ReadyTick = mine ? w.ReadyTick : 0, BurstRemaining = mine ? w.BurstRemaining : 0, AutoCast = mine && w.AutoCast,
                    Busy = states && w.Busy });
            }
            if ((source.PreviousObservers[i] & bit) == 0) p = p with { Previous = p.Current,
                Presentation = p.Presentation with { PreviousBodyFacing = p.Presentation.BodyFacing, PreviousWeaponFacing = p.Presentation.WeaponFacing } };
            p = p with { WeaponOffset = start, Order = mine ? p.Order with { Target = visible.Contains(p.Order.Target) ? p.Order.Target : default } : default,
                Health = health ? p.Health : 0, Presentation = states ? p.Presentation : p.Presentation with { States = 0 },
                TerrainStranded = states && p.TerrainStranded, Construction = states ? p.Construction : default,
                Carrier = visible.Contains(p.Carrier) ? p.Carrier : default,
                HasRally = mine && p.HasRally, Rally = mine ? p.Rally : default, Supply = mine ? p.Supply : default,
                Capture = mine ? p.Capture : default, AutoBuild = mine && p.AutoBuild,
                Transformation = mine ? p.Transformation : default,
                Upgrade = mine ? p.Upgrade : states ? p.Upgrade with { Project = null, Work = 0, Required = 0, Payer = 0 } : default,
                Flight = p.Flight with { Airbase = visible.Contains(p.Flight.Airbase) ? p.Flight.Airbase : default, Ammo = mine ? p.Flight.Ammo : 0, Capacity = mine ? p.Flight.Capacity : 0 } };
            poses.Add(p);
        }
        bool At(WorldPosition p) => player >= 0 && (source.FogCellSize == 0 || p.X >= 0 && p.X < source.FogWidth && p.Y >= 0 && p.Y < source.FogDepth &&
            result.FogStates[p.Y / source.FogCellSize * source.FogColumns + p.X / source.FogCellSize] == CellVisibility.Visible);
        result.Poses = poses.ToArray(); result.Count = poses.Count; result.Weapons = weapons.ToArray(); result.WeaponCount = weapons.Count;
        result.Projectiles = source.Projectiles.Take(source.ProjectileCount).Where(p => At(p.Current)).Select(p => p with { Previous = At(p.Previous) ? p.Previous : p.Current, Projection = default, Facing = Direction(p.Facing) }).ToArray();
        result.ProjectileCount = result.Projectiles.Length;
        result.Tasks = source.Tasks.Take(source.TaskCount).Where(t => own.Contains(t.Producer)).ToArray(); result.TaskCount = result.Tasks.Length;
        result.Projects = source.Projects.Take(source.ProjectCount).Where(p => p.Owner == viewer).ToArray(); result.ProjectCount = result.Projects.Length;
        result.Players = source.Players.Take(source.PlayerCount).Select(p => p.Id == viewer ? p : p with { Credits = 0, EnergyProduced = 0, EnergyConsumed = 0 }).ToArray(); result.PlayerCount = result.Players.Length;
        return result;
        static FacingDirection Direction(FacingDirection d)
        {
            long size = Math.Max(Math.Abs((long)d.X), Math.Abs((long)d.Y));
            return size == 0 ? default : new((int)(d.X * 1024L / size), (int)(d.Y * 1024L / size));
        }
    }
}
