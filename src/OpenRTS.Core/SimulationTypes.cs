namespace OpenRTS.Core;

public readonly record struct EntityId(uint Value);

/// <summary>X/Y are ground coordinates; elevation is independent. All values are raw fixed-point units.</summary>
public readonly record struct WorldPosition(int X, int Y, int Elevation = 0)
{
    public const int UnitsPerCell = 1024;
    public const int MaximumExtent = 1 << 29;
}

public enum CommandKind { Move, MoveAll, Scatter, Spawn, Destroy, BlockCell, UnblockCell, MoveGroup }

public readonly record struct WorldCommand(long Tick, uint Issuer, long Sequence, CommandKind Kind,
    EntityId Entity = default, WorldPosition Position = default, int UnitType = 0);

public enum WorldEventKind { Spawned, Destroyed, MissingEntity, NoRoute, CellOccupied, CellBlocked }
public readonly record struct WorldEvent(long Tick, WorldEventKind Kind, EntityId Entity,
    WorldPosition Position = default, int UnitType = -1);

public readonly record struct EntityPose(EntityId Id, int UnitType, WorldPosition Previous, WorldPosition Current);
public sealed record SimulationReport(long Tick, int EntityCount, string StateHash);

/// <summary>SplitMix64 with explicitly wrapping arithmetic; state is part of the authoritative world.</summary>
public struct DeterministicRandom(ulong seed)
{
    public ulong State { get; private set; } = seed;

    public ulong NextUInt64()
    {
        unchecked
        {
            State += 0x9E3779B97F4A7C15UL;
            ulong value = State;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }

    public int NextInt(int exclusiveMaximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exclusiveMaximum);
        ulong bound = (ulong)exclusiveMaximum;
        ulong threshold = unchecked(0UL - bound) % bound;
        ulong value;
        do value = NextUInt64(); while (value < threshold);
        return (int)(value % bound);
    }
}
