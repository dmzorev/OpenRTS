using Frent;

namespace OpenRTS.Core.Construction;

internal sealed class StandardConstruction : IConstructionMechanic
{
    private struct ConstructionState(int required, int startingHealth, long startedAt)
    {
        public readonly int Required = required, StartingHealth = startingHealth;
        public readonly long StartedAt = startedAt;
        public int Work;
    }

    public FoundationStart PlanFoundation(UnitDefinition type, BuildDefinition recipe)
        => new(Math.Max(1, (int)((long)type.MaxHealth * recipe.InitialHealthPercent / 100)), recipe.Ticks * 2);

    public void Initialize(Entity foundation, FoundationStart start, long tick)
        => foundation.Add(new ConstructionState(start.RequiredWork, start.Health, tick));

    public bool IsFoundation(Entity entity) => entity.Has<ConstructionState>();

    public ConstructionProgress GetProgress(Entity foundation)
    {
        ref var site = ref foundation.Get<ConstructionState>();
        return new(site.Work, site.Required);
    }

    public FoundationStep Advance(FoundationWork work)
    {
        ref var site = ref work.Foundation.Get<ConstructionState>();
        if (site.StartedAt >= work.Tick) return new(work.Health, false);
        int amount = (work.BuilderConditions & GameplayConditions.LowPower) != 0 ? 1 : 2;
        int before = site.Work;
        site.Work = Math.Min(site.Required, before + amount);
        long healing = work.Type.MaxHealth - site.StartingHealth;
        int increment = (int)(healing * site.Work / site.Required - healing * before / site.Required);
        return new((int)Math.Min(work.Type.MaxHealth, (long)work.Health + increment), site.Work == site.Required);
    }

    public void Complete(Entity foundation) => foundation.Remove<ConstructionState>();

    public void WriteFoundationState(BinaryWriter writer, Entity foundation)
    {
        ref var site = ref foundation.Get<ConstructionState>();
        writer.Write(site.Work);
        writer.Write(site.Required);
        writer.Write(site.StartingHealth);
        writer.Write(site.StartedAt);
    }
}
