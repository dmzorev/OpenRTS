namespace OpenRTS.AI;

public sealed record AiRecipe(int ProducerType, int ProductType, int Queue, string Action, int Cost, bool OnSite = false);
public sealed record AiBuilding(AiRecipe Recipe, int Target);
public sealed record AiMember(AiRecipe Recipe, int Minimum, int Target);

public sealed class SkirmishAiProfile(string id, IEnumerable<int> opening, IEnumerable<AiBuilding> buildings,
    IEnumerable<AiMember> group, int engineerType, string captureAction, int creditReserve,
    int macroIntervalTicks, int tacticsIntervalTicks, int blockedGoalTicks)
{
    public string Id { get; } = id;
    public IReadOnlyList<int> Opening { get; } = Array.AsReadOnly(opening.ToArray());
    public IReadOnlyList<AiBuilding> Buildings { get; } = Array.AsReadOnly(buildings.ToArray());
    public IReadOnlyList<AiMember> Group { get; } = Array.AsReadOnly(group.ToArray());
    public int EngineerType { get; } = engineerType;
    public string CaptureAction { get; } = captureAction;
    public int CreditReserve { get; } = creditReserve;
    public int MacroIntervalTicks { get; } = macroIntervalTicks;
    public int TacticsIntervalTicks { get; } = tacticsIntervalTicks;
    public int BlockedGoalTicks { get; } = blockedGoalTicks;
}
