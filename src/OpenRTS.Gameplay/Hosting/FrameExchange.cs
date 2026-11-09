using System.Diagnostics;

namespace OpenRTS.Core;

/// <summary>One producer and multiple readers. Leases pin a slot until its last reader releases it.</summary>
public sealed partial class FrameExchange
{
    internal sealed class Slot
    {
        public EntityPose[] Poses = [];
        public ulong[] Observers = [], PreviousObservers = [];
        public ulong[] HealthObservers = [], StateObservers = [];
        public bool RestrictLiveFields;
        public List<OpenRTS.Core.Visibility.ContactPose>[] PlayerContacts = [];
        public List<OpenRTS.Core.Visibility.ContactPose>? Contacts;
        public OpenRTS.Core.Visibility.VisionSource[] VisionSources = [];
        public int VisionSourceCount;
        public uint[] FogPlayers = [];
        public OpenRTS.Core.Visibility.CellVisibility[] FogStates = [];
        public byte[] FogStrength = [];
        public int FogColumns, FogRows, FogCellSize, FogWidth, FogDepth, FogHoldTicks;
        public uint Viewer;
        public readonly Dictionary<uint, Slot> Views = [];
        public WeaponPose[] Weapons = [];
        public ProjectilePose[] Projectiles = [];
        public TaskPose[] Tasks = [];
        public PlayerPose[] Players = [];
        public int PlayerCount;
        public ConstructionProjectPose[] Projects = [];
        public int ProjectCount;
        public int TaskCount;
        public int WeaponCount, ProjectileCount;
        public int Count, Readers, TickRate, NavigationWaitingCount, MovingCount, LocalSearches, Yields, GoalExchanges, QueueWaiting;
        public long Tick, Timestamp;
        public NavigationGrid? Grid, BaseGrid;
    }

    private readonly object _gate = new();
    private readonly Slot[] _slots = [new(), new(), new()];
    private int _latest = -1;

    public bool TryPublish(SimulationWorld world)
    {
        lock (_gate)
        {
            int index = -1;
            for (int i = 0; i < _slots.Length; i++)
                if (i != _latest && _slots[i].Readers == 0) { index = i; break; }
            if (index < 0) return false;
            var slot = _slots[index];
            if (slot.Poses.Length < world.Count) Array.Resize(ref slot.Poses, Math.Max(world.Count, checked(Math.Max(16, slot.Poses.Length * 2))));
            world.SynchronizeVisibility();
            world.CopyPoses(slot.Poses);
            slot.WeaponCount = world.WeaponCount; slot.ProjectileCount = world.ProjectileCount;
            if (slot.Weapons.Length < slot.WeaponCount) Array.Resize(ref slot.Weapons, Math.Max(slot.WeaponCount, slot.Weapons.Length * 2));
            if (slot.Projectiles.Length < slot.ProjectileCount) Array.Resize(ref slot.Projectiles, Math.Max(slot.ProjectileCount, slot.Projectiles.Length * 2));
            slot.TaskCount = world.TaskCount;
            if (slot.Tasks.Length < slot.TaskCount) Array.Resize(ref slot.Tasks, Math.Max(slot.TaskCount, slot.Tasks.Length * 2));
            slot.PlayerCount = world.PlayerCount;
            if (slot.Players.Length < slot.PlayerCount) Array.Resize(ref slot.Players, slot.PlayerCount);
            world.CopyPlayers(slot.Players);
            slot.ProjectCount = world.ConstructionProjectCount;
            if (slot.Projects.Length < slot.ProjectCount) Array.Resize(ref slot.Projects, slot.ProjectCount);
            world.CopyConstructionProjects(slot.Projects);
            world.CopyTaskPoses(slot.Tasks);
            world.CopyWeaponPoses(slot.Weapons); world.CopyProjectilePoses(slot.Projectiles);
            slot.Count = world.Count;
            slot.Tick = world.Tick;
            slot.TickRate = world.TickRate;
            slot.Timestamp = Stopwatch.GetTimestamp();
            slot.Grid = world.NavigationGrid;
            slot.BaseGrid = world.BaseNavigationGrid;
            slot.NavigationWaitingCount = world.NavigationWaitingCount;
            slot.MovingCount = world.MovingCount;
            slot.LocalSearches = world.NavigationLocalSearches;
            slot.Yields = world.NavigationYields;
            slot.GoalExchanges = world.NavigationGoalExchanges;
            slot.QueueWaiting = world.NavigationQueueWaitingCount;
            CaptureFog(slot, world);
            slot.Views.Clear();
            _latest = index;
            return true;
        }
    }

    public FrameLease? AcquireLatest(uint viewer = 0)
    {
        lock (_gate)
        {
            if (_latest < 0) return null;
            int index = _latest;
            _slots[index].Readers++;
            var data = _slots[index];
            if (viewer != 0 && data.FogPlayers.Length != 0)
            {
                if (!data.Views.TryGetValue(viewer, out var view)) data.Views.Add(viewer, view = Filter(data, viewer));
                data = view;
            }
            return new FrameLease(this, index, data);
        }
    }

    public sealed class FrameLease : IDisposable
    {
        private readonly FrameExchange _owner;
        private readonly int _index;
        private int _disposed;
        private readonly Slot _data;
        internal FrameLease(FrameExchange owner, int index, Slot data) { _owner = owner; _index = index; _data = data; }
        private Slot Data
        {
            get
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                return _data;
            }
        }
        public ReadOnlySpan<OpenRTS.Core.Visibility.VisionSource> VisionSources => Data.VisionSources.AsSpan(0, Data.VisionSourceCount);
        public ReadOnlySpan<OpenRTS.Core.Visibility.ContactPose> Contacts => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(Data.Contacts);
        public uint Viewer => Data.Viewer;
        public ReadOnlySpan<byte> FogStrength => Data.FogStrength;
        public int FogHoldTicks => Data.FogHoldTicks;
        public int FogColumns => Data.FogColumns;
        public int FogRows => Data.FogRows;
        public int FogCellSize => Data.FogCellSize;
        public int FogWidth => Data.FogWidth;
        public int FogDepth => Data.FogDepth;
        public ReadOnlySpan<OpenRTS.Core.Visibility.CellVisibility> FogStates => Data.FogStates;
        public long Tick => Data.Tick;
        public int TickRate => Data.TickRate;
        public long Timestamp => Data.Timestamp;
        public NavigationGrid? Grid => Data.Grid;
        public NavigationGrid? BaseGrid => Data.BaseGrid;
        public int NavigationWaitingCount => Data.NavigationWaitingCount;
        public int MovingCount => Data.MovingCount;
        public int NavigationLocalSearches => Data.LocalSearches;
        public int NavigationYields => Data.Yields;
        public int NavigationGoalExchanges => Data.GoalExchanges;
        public int NavigationQueueWaitingCount => Data.QueueWaiting;
        public ReadOnlySpan<PlayerPose> Players => Data.Players.AsSpan(0, Data.PlayerCount);
        public ReadOnlySpan<ConstructionProjectPose> ConstructionProjects => Data.Projects.AsSpan(0, Data.ProjectCount);
        public ReadOnlySpan<TaskPose> Tasks => Data.Tasks.AsSpan(0, Data.TaskCount);
        public ReadOnlySpan<WeaponPose> Weapons => Data.Weapons.AsSpan(0, Data.WeaponCount);
        public ReadOnlySpan<ProjectilePose> Projectiles => Data.Projectiles.AsSpan(0, Data.ProjectileCount);
        public ReadOnlySpan<EntityPose> Poses => Data.Poses.AsSpan(0, Data.Count);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (_owner._gate) _owner._slots[_index].Readers--;
        }
    }
}
