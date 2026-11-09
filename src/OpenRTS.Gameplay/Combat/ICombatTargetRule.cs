namespace OpenRTS.Core.Combat;

/// <summary>The entity used as the firing origin, including a carrier when a passenger fires.</summary>
public readonly record struct CombatTargetQuery(EntityId Origin, uint Owner, UnitDefinition OriginType, WeaponDefinition Weapon, int Range);

/// <summary>A legal automatic target. Distance is measured between the entities' physical boundaries.</summary>
/// <param name="Health">Current health when disclosed to the observer, otherwise zero.</param>
public readonly record struct CombatTargetCandidate(EntityId Entity, uint Owner, UnitDefinition Type, int Health, long DistanceSquared);

/// <summary>Ranks targets during automatic acquisition, including attack-move and autocast.</summary>
public interface ICombatTargetRule
{
    /// <summary>Higher scores win. Return null to skip the candidate. Ties use distance, then entity ID.</summary>
    /// <remarks>Use only the supplied values and immutable configuration. Candidate evaluation order is unspecified.</remarks>
    long? Score(in CombatTargetQuery query, in CombatTargetCandidate candidate);

    /// <summary>Writes the immutable settings that affect scoring to the simulation hash.</summary>
    void WriteConfiguration(BinaryWriter writer) { }
}
