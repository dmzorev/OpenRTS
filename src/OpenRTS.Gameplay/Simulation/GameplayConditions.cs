namespace OpenRTS.Core;

/// <summary>Read-only gameplay facts, computed from authoritative simulation state.</summary>
[Flags]
public enum GameplayConditions : byte
{
    None = 0,
    LowPower = 1
}
