namespace OpenRTS.Core;

public enum AbilityKind { Weapon, Capture }
public sealed record AbilityDefinition(string Id, string Mount, bool GroundTarget = false, bool AutoCast = false, AbilityKind Kind = AbilityKind.Weapon, int Range = 1024);
