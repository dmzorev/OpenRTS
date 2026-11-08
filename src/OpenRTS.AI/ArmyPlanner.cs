using OpenRTS.Core;
using OpenRTS.Core.Orders;
using OpenRTS.Core.Presentation;

namespace OpenRTS.AI;

internal sealed class ArmyPlanner(SkirmishAiProfile profile, WorldPosition rally, WorldPosition objective)
{
    private sealed class Slot(AiSergeantRule rule, int number)
    {
        public AiSergeantRule Rule { get; } = rule;
        public int Number { get; } = number;
        public bool Active;
        public Sergeant? Sergeant;
    }

    private readonly Slot[] _slots = profile.Sergeants.OrderByDescending(rule => rule.Priority)
        .ThenBy(rule => rule.Definition.Id, StringComparer.Ordinal)
        .SelectMany(rule => Enumerable.Range(0, rule.Max).Select(number => new Slot(rule, number))).ToArray();

    public string Status => string.Join(" · ", _slots.Where(slot => slot.Active)
        .Select(slot => $"{slot.Rule.Definition.Id}#{slot.Number + 1} {slot.Sergeant!.Status}"));

    public void Update(long tick, int width, int depth, EntityPose[] own, EntityPose[] enemies,
        long? credits, bool tactics, List<WorldCommand> commands)
    {
        var available = own.Where(p => !p.Construction.Active).ToDictionary(p => p.Id);
        var assigned = new HashSet<EntityId>();
        bool threat = credits.HasValue && enemies.Any(p => p.WeaponCount > 0 && SergeantSituation.Nearby(rally, p));
        foreach (var slot in _slots)
        {
            if (credits is not { } balance) continue;
            var rule = slot.Rule;
            long threshold = slot.Active ? rule.RetainCredits : rule.StartCredits;
            int extra = Math.Max(0, slot.Number + 1 - rule.Min);
            bool active = tick >= rule.EarliestTick && (extra == 0 || Math.Max(0, balance) >= threshold * extra);
            if (slot.Active && !active)
            {
                var released = slot.Sergeant!.Members.Where(p => available.ContainsKey(p.Id)).Select(p => p.Id).ToArray();
                if (released.Length > 0) commands.Add(new(0, 0, 0, CommandKind.Stop, Recipients: new EntitySet(released)));
                slot.Sergeant.Assign([]);
                slot.Sergeant.Reset();
            }
            slot.Active = active;
            if (active) slot.Sergeant?.Reprofile(threat ? rule.UnderThreatStrategy ?? rule.Definition.Strategy : rule.Definition.Strategy);
        }

        // Retain ownership across all active slots before offering free actors to the highest priority slot.
        foreach (var slot in _slots)
            if (slot.Active && slot.Sergeant is { } sergeant)
                foreach (var member in sergeant.Members)
                    if (available.ContainsKey(member.Id)) assigned.Add(member.Id);

        var freeByType = own.Where(p => available.ContainsKey(p.Id) && !assigned.Contains(p.Id))
            .GroupBy(p => p.UnitType).ToDictionary(group => group.Key, group => new Queue<EntityPose>(group));
        for (int i = 0; i < _slots.Length; i++)
        {
            var slot = _slots[i];
            if (!slot.Active) continue;
            var roster = slot.Rule.Definition;
            var members = new List<EntityPose>();
            if (slot.Sergeant is { } existing)
                foreach (var member in existing.Members)
                    if (available.TryGetValue(member.Id, out var pose)) members.Add(pose);
            if (members.Count == 0 && slot.Sergeant is { Members.Count: > 0 } wiped) wiped.Reset();

            foreach (var member in roster.Members)
            {
                if (!freeByType.TryGetValue(member.Recipe.ProductType, out var free)) continue;
                int missing = member.Target - members.Count(p => p.UnitType == member.Recipe.ProductType);
                while (missing-- > 0 && free.TryDequeue(out var recruit)) members.Add(recruit);
            }

            if (slot.Sergeant is null)
                slot.Sergeant = new(roster, members, threat ? slot.Rule.UnderThreatStrategy ?? roster.Strategy : roster.Strategy);
            else
                slot.Sergeant.Assign(members.OrderBy(p => p.Id.Value).ToArray());

            if (tactics)
            {
                var point = RallyPoint(i, width, depth);
                slot.Sergeant.Plan(new(tick, point, objective, enemies, profile.BlockedGoalTicks), commands);
            }
        }
    }

    public void PlanReinforcements(TaskPose[] tasks, EntityPose[] own, uint player, Func<AiRecipe, bool> issue)
    {
        var producers = own.Select(p => p.Id).ToHashSet();
        var pending = tasks.Where(t => t.Payer == player && producers.Contains(t.Producer))
            .GroupBy(t => t.UnitType).ToDictionary(group => group.Key, group => group.Count());
        foreach (var slot in _slots)
        {
            if (!slot.Active) continue;
            foreach (var (recipe, missing) in slot.Sergeant!.Reinforcements())
            {
                int type = recipe.ProductType;
                int covered = Math.Min(pending.GetValueOrDefault(type), missing);
                pending[type] = pending.GetValueOrDefault(type) - covered;
                if (missing > covered) issue(recipe);
            }
        }
    }

    private WorldPosition RallyPoint(int index, int width, int depth)
    {
        const int spacing = 6 * WorldPosition.UnitsPerCell;
        int column = index % 5;
        int lateral = (column + 1) / 2 * (column % 2 == 0 ? 1 : -1);
        int rows = index / 5;
        int x = Math.Sign(objective.X - rally.X), y = Math.Sign(objective.Y - rally.Y);
        if (x == 0 && y == 0) x = 1;
        return new(Math.Clamp(rally.X - spacing * (lateral * y + rows * x), 0, width),
            Math.Clamp(rally.Y + spacing * (lateral * x - rows * y), 0, depth));
    }
}
