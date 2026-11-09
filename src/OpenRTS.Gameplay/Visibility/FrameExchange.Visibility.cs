using OpenRTS.Core.Visibility;

namespace OpenRTS.Core;

public sealed partial class FrameExchange
{
    private static void CaptureFog(Slot slot, SimulationWorld world)
    {
        if (world.Fog is not { } fog)
        {
            slot.FogCellSize = 0; slot.FogStates = []; slot.FogPlayers = []; slot.VisionSourceCount = 0;
            return;
        }
        slot.VisionSourceCount = world.VisionSources.Length;
        if (slot.VisionSources.Length < slot.VisionSourceCount) Array.Resize(ref slot.VisionSources, slot.VisionSourceCount);
        world.VisionSources.CopyTo(slot.VisionSources);
        slot.FogCellSize = fog.CellSize; slot.FogColumns = fog.Columns; slot.FogRows = fog.Rows;
        slot.FogWidth = fog.Width; slot.FogDepth = fog.Depth;
        if (slot.FogStates.Length != fog.CellCount * fog.Players.Count) slot.FogStates = new CellVisibility[fog.CellCount * fog.Players.Count];
        slot.FogPlayers = fog.Players.ToArray(); fog.CopyStates(slot.FogStates);
        if (slot.Observers.Length < slot.Count) { slot.Observers = new ulong[slot.Count]; slot.PreviousObservers = new ulong[slot.Count]; }
        for (int i = 0; i < slot.Count; i++)
        {
            ulong mask = 0, previous = 0;
            for (int p = 0; p < fog.Players.Count; p++)
            {
                if (world.IsVisible(fog.Players[p], slot.Poses[i].Id)) mask |= 1ul << p;
                if (world.WasVisible(fog.Players[p], slot.Poses[i].Id)) previous |= 1ul << p;
            }
            slot.Observers[i] = mask; slot.PreviousObservers[i] = previous;
        }
    }
    // shortcut: viewer arrays are rebuilt per publication; reuse buffers after multi-view frame profiling.
    private static Slot Filter(Slot source, uint viewer)
    {
        int player = Array.IndexOf(source.FogPlayers, viewer), cells = source.FogColumns * source.FogRows;
        ulong bit = player < 0 ? 0 : 1ul << player;
        var circles = source.VisionSources.Take(source.VisionSourceCount).Where(s => s.Owner == viewer).ToArray();
        var result = new Slot { VisionSources = circles, VisionSourceCount = circles.Length, Viewer = viewer, Tick = source.Tick, Timestamp = source.Timestamp, TickRate = source.TickRate,
            Grid = source.Grid, BaseGrid = source.BaseGrid, FogColumns = source.FogColumns, FogRows = source.FogRows,
            FogCellSize = source.FogCellSize, FogWidth = source.FogWidth, FogDepth = source.FogDepth,
            FogStates = player < 0 ? new CellVisibility[cells] : source.FogStates.AsSpan(player * cells, cells).ToArray() };
        var visible = new HashSet<EntityId>();
        for (int i = 0; i < source.Count; i++) if ((source.Observers[i] & bit) != 0) visible.Add(source.Poses[i].Id);
        var own = new HashSet<EntityId>(); var poses = new List<EntityPose>(); var weapons = new List<WeaponPose>();
        for (int i = 0; i < source.Count; i++)
        {
            var p = source.Poses[i]; if (!visible.Contains(p.Id)) continue;
            bool mine = p.Presentation.Owner == viewer;
            if (mine) own.Add(p.Id);
            int start = weapons.Count;
            for (int m = 0; m < p.WeaponCount; m++)
            {
                var w = source.Weapons[p.WeaponOffset + m];
                weapons.Add(w with { Target = visible.Contains(w.Target) ? w.Target : default,
                    ReadyTick = mine ? w.ReadyTick : 0, BurstRemaining = mine ? w.BurstRemaining : 0, AutoCast = mine && w.AutoCast });
            }
            if ((source.PreviousObservers[i] & bit) == 0) p = p with { Previous = p.Current,
                Presentation = p.Presentation with { PreviousBodyFacing = p.Presentation.BodyFacing, PreviousWeaponFacing = p.Presentation.WeaponFacing } };
            p = p with { WeaponOffset = start, Order = mine ? p.Order with { Target = visible.Contains(p.Order.Target) ? p.Order.Target : default } : default,
                HasRally = mine && p.HasRally, Rally = mine ? p.Rally : default, Supply = mine ? p.Supply : default,
                Capture = mine ? p.Capture : default, AutoBuild = mine && p.AutoBuild,
                Transformation = mine ? p.Transformation : default,
                Upgrade = mine ? p.Upgrade : p.Upgrade with { Project = null, Work = 0, Required = 0, Payer = 0 },
                Flight = p.Flight with { Airbase = visible.Contains(p.Flight.Airbase) ? p.Flight.Airbase : default, Ammo = mine ? p.Flight.Ammo : 0, Capacity = mine ? p.Flight.Capacity : 0 } };
            poses.Add(p);
        }
        bool At(WorldPosition p) => p.X >= 0 && p.X < source.FogWidth && p.Y >= 0 && p.Y < source.FogDepth &&
            result.FogStates[p.Y / source.FogCellSize * source.FogColumns + p.X / source.FogCellSize] == CellVisibility.Visible;
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
