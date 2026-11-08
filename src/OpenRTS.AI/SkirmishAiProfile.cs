namespace OpenRTS.AI;

public sealed record AiRecipe(int ProducerType, int ProductType, int Queue, string Action, int Cost, bool OnSite = false);
public sealed record AiBuilding(AiRecipe Recipe, int Target, int Priority = 0);
public sealed record AiMember(AiRecipe Recipe, int Minimum, int Target);
public sealed record AiSupply(AiRecipe Truck, int Target, bool ImproveMines);
public enum SergeantStrategy { Assault, Guard, Patrol }

public sealed class AiSergeant(string id, SergeantStrategy strategy, IEnumerable<AiMember> members)
{
    public string Id { get; } = id;
    public SergeantStrategy Strategy { get; } = strategy;
    public IReadOnlyList<AiMember> Members { get; } = Array.AsReadOnly(members.ToArray());
}

public sealed record AiSergeantRule(AiSergeant Definition, int Min, int Max, int Priority, int EarliestTick,
    int StartCredits, int RetainCredits, SergeantStrategy? UnderThreatStrategy);

public sealed class SkirmishAiProfile(string id, IEnumerable<int> opening, IEnumerable<AiBuilding> buildings,
    IEnumerable<AiSergeantRule> sergeants, int engineerType, string captureAction, int creditReserve,
    int macroIntervalTicks, int tacticsIntervalTicks, int blockedGoalTicks, AiSupply? supply = null, AiRecipe? replacementEngineer = null)
{
    public string Id { get; } = id;
    public IReadOnlyList<int> Opening { get; } = Array.AsReadOnly(opening.ToArray());
    public IReadOnlyList<AiBuilding> Buildings { get; } = Array.AsReadOnly(buildings.ToArray());
    public IReadOnlyList<AiSergeantRule> Sergeants { get; } = Array.AsReadOnly(sergeants.ToArray());
    public int EngineerType { get; } = engineerType;
    public string CaptureAction { get; } = captureAction;
    public int CreditReserve { get; } = creditReserve;
    public int MacroIntervalTicks { get; } = macroIntervalTicks;
    public int TacticsIntervalTicks { get; } = tacticsIntervalTicks;
    public int BlockedGoalTicks { get; } = blockedGoalTicks;
    public AiSupply? Supply { get; } = supply;
    public AiRecipe? ReplacementEngineer { get; } = replacementEngineer;
}
