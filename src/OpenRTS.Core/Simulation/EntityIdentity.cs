namespace OpenRTS.Core.Simulation;

/// <summary>Stable identity and optional game-defined type number, independent of Frent's storage order.</summary>
public readonly record struct EntityIdentity(EntityId Id, int Type = 0);
