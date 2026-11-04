using System.Diagnostics;

namespace OpenRTS.Core;

/// <summary>One producer and multiple readers. Leases pin a slot until its last reader releases it.</summary>
public sealed class FrameExchange
{
    private sealed class Slot
    {
        public EntityPose[] Poses = [];
        public WeaponPose[] Weapons = [];
        public ProjectilePose[] Projectiles = [];
        public int WeaponCount, ProjectileCount;
        public int Count, Readers, TickRate, NavigationWaitingCount, MovingCount, LocalSearches, Yields, GoalExchanges, QueueWaiting;
        public long Tick, Timestamp;
        public NavigationGrid? Grid;
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
            world.CopyPoses(slot.Poses);
            slot.WeaponCount = world.WeaponCount; slot.ProjectileCount = world.ProjectileCount;
            if (slot.Weapons.Length < slot.WeaponCount) Array.Resize(ref slot.Weapons, Math.Max(slot.WeaponCount, slot.Weapons.Length * 2));
            if (slot.Projectiles.Length < slot.ProjectileCount) Array.Resize(ref slot.Projectiles, Math.Max(slot.ProjectileCount, slot.Projectiles.Length * 2));
            world.CopyWeaponPoses(slot.Weapons); world.CopyProjectilePoses(slot.Projectiles);
            slot.Count = world.Count;
            slot.Tick = world.Tick;
            slot.TickRate = world.TickRate;
            slot.Timestamp = Stopwatch.GetTimestamp();
            slot.Grid = world.NavigationGrid;
            slot.NavigationWaitingCount = world.NavigationWaitingCount;
            slot.MovingCount = world.MovingCount;
            slot.LocalSearches = world.NavigationLocalSearches;
            slot.Yields = world.NavigationYields;
            slot.GoalExchanges = world.NavigationGoalExchanges;
            slot.QueueWaiting = world.NavigationQueueWaitingCount;
            _latest = index;
            return true;
        }
    }

    public FrameLease? AcquireLatest()
    {
        lock (_gate)
        {
            if (_latest < 0) return null;
            int index = _latest;
            _slots[index].Readers++;
            return new FrameLease(this, index);
        }
    }

    public sealed class FrameLease : IDisposable
    {
        private readonly FrameExchange _owner;
        private readonly int _index;
        private int _disposed;
        internal FrameLease(FrameExchange owner, int index) { _owner = owner; _index = index; }
        private Slot Data
        {
            get
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                return _owner._slots[_index];
            }
        }
        public long Tick => Data.Tick;
        public int TickRate => Data.TickRate;
        public long Timestamp => Data.Timestamp;
        public NavigationGrid? Grid => Data.Grid;
        public int NavigationWaitingCount => Data.NavigationWaitingCount;
        public int MovingCount => Data.MovingCount;
        public int NavigationLocalSearches => Data.LocalSearches;
        public int NavigationYields => Data.Yields;
        public int NavigationGoalExchanges => Data.GoalExchanges;
        public int NavigationQueueWaitingCount => Data.QueueWaiting;
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
