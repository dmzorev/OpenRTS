using Frent;

namespace OpenRTS.Core.Construction;

public readonly record struct FoundationStart(int Health, int RequiredWork);
public readonly record struct FoundationWork(Entity Foundation, UnitDefinition Type, int Health, GameplayConditions BuilderConditions, long Tick);
public readonly record struct FoundationStep(int Health, bool Complete);

/// <summary>World-local foundation state and progress. The host owns project placement, payment, and activation.</summary>
public interface IConstructionMechanic
{
    FoundationStart PlanFoundation(UnitDefinition type, BuildDefinition recipe);
    // Initialize and Complete may add or remove the mechanic's own components. IsFoundation must reflect those transitions.
    void Initialize(Entity foundation, FoundationStart start, long tick);
    bool IsFoundation(Entity entity);
    ConstructionProgress GetProgress(Entity foundation); // Called for active foundations only.
    // Called once per eligible builder, including on the start tick. Modify existing state and return the new health.
    FoundationStep Advance(FoundationWork work);
    void Complete(Entity foundation);
    // Called for foundations only. Write canonical, self-delimiting state.
    void WriteFoundationState(BinaryWriter writer, Entity foundation);
    void WriteState(BinaryWriter writer) { }
}
