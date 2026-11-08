using OpenRTS.Core;
using OpenRTS.Core.Orders;
using OpenRTS.Core.Presentation;

namespace OpenRTS.AI;

internal sealed class Sergeant(AiSergeant definition, IEnumerable<EntityPose> members, SergeantStrategy strategy)
{
    private ISergeantStrategy _strategy = CreateStrategy(strategy);
    private SergeantStrategy _strategyName = strategy;
    private EntityPose[] _members = members.OrderBy(p => p.Id.Value).ToArray();
    private SergeantOrder? _lastOrder;

    public IReadOnlyList<EntityPose> Members => _members;
    public string Status => $"{_strategyName}/{_lastOrder?.Kind.ToString() ?? "waiting"}:{_members.Length}";

    public void Assign(EntityPose[] assigned)
    {
        if (!_members.Select(p => p.Id).SequenceEqual(assigned.Select(p => p.Id))) _lastOrder = null;
        _members = assigned;
    }

    public void Reprofile(SergeantStrategy strategy)
    {
        if (_strategyName == strategy) return;
        _strategyName = strategy;
        Reset();
    }

    public void Reset()
    {
        _strategy = CreateStrategy(_strategyName);
        _lastOrder = null;
    }

    public IEnumerable<(AiRecipe Recipe, int Missing)> Reinforcements()
    {
        foreach (var member in definition.Members)
        {
            int missing = member.Target - _members.Count(p => p.UnitType == member.Recipe.ProductType);
            if (missing > 0) yield return (member.Recipe, missing);
        }
    }

    public void Plan(SergeantSituation situation, List<WorldCommand> commands)
    {
        if (_members.Length == 0) return;
        var order = _strategy.Plan(situation, _members, definition.Members);
        if (_lastOrder == order) return;
        _lastOrder = order;
        commands.Add(new(0, 0, 0, order.Kind, Position: order.Position, Recipients: new EntitySet(_members.Select(p => p.Id))));
    }

    private static ISergeantStrategy CreateStrategy(SergeantStrategy strategy) => strategy switch
    {
        SergeantStrategy.Assault => new AssaultStrategy(),
        SergeantStrategy.Guard => new GuardStrategy(),
        SergeantStrategy.Patrol => new PatrolStrategy(),
        _ => throw new ArgumentOutOfRangeException(nameof(strategy))
    };
}

internal readonly record struct SergeantOrder(CommandKind Kind, WorldPosition Position);

internal readonly record struct SergeantSituation(long Tick, WorldPosition Rally, WorldPosition Objective,
    IReadOnlyList<EntityPose> Enemies, int BlockedTicks)
{
    private const long AssemblyRadius = 4096;
    private const long DefenseRadius = 12288;

    public bool AtRally(EntityPose member) => Distance(member.Current, Rally) <= AssemblyRadius * AssemblyRadius;
    public bool AtObjective(EntityPose member) => Distance(member.Current, Objective) <= AssemblyRadius * AssemblyRadius;
    public static bool Nearby(WorldPosition position, EntityPose enemy) =>
        Distance(position, enemy.Current) <= DefenseRadius * DefenseRadius;

    public SergeantOrder RallyOrder(IReadOnlyList<EntityPose> members) =>
        new(members.All(AtRally) ? CommandKind.HoldPosition : CommandKind.MoveSelected, Rally);

    public static long Distance(WorldPosition a, WorldPosition b)
    {
        long x = (long)a.X - b.X, y = (long)a.Y - b.Y;
        return x * x + y * y;
    }
}

internal interface ISergeantStrategy
{
    SergeantOrder Plan(SergeantSituation situation, IReadOnlyList<EntityPose> members, IReadOnlyList<AiMember> roster);
}

internal sealed class GuardStrategy : ISergeantStrategy
{
    public SergeantOrder Plan(SergeantSituation situation, IReadOnlyList<EntityPose> members, IReadOnlyList<AiMember> roster) =>
        situation.RallyOrder(members);
}

internal sealed class AssaultStrategy : ISergeantStrategy
{
    private long _lastProgress, _retryAt;
    private long _distance, _enemyHealth;
    private bool _attacking;

    public SergeantOrder Plan(SergeantSituation situation, IReadOnlyList<EntityPose> members, IReadOnlyList<AiMember> roster)
    {
        bool Enough(Func<EntityPose, bool> ready) => roster.All(member =>
            members.Count(p => p.UnitType == member.Recipe.ProductType && ready(p)) >= member.Minimum);

        if (situation.Enemies.Count == 0 || !Enough(_ => true) ||
            situation.Enemies.Any(p => p.WeaponCount > 0 && SergeantSituation.Nearby(situation.Rally, p)))
            _attacking = false;
        else if (_attacking)
        {
            long distance = members.Min(p => SergeantSituation.Distance(p.Current, situation.Objective));
            long health = situation.Enemies.Where(p => SergeantSituation.Nearby(situation.Objective, p)).Sum(p => (long)p.Health);
            if (distance < _distance || health < _enemyHealth)
            {
                _distance = Math.Min(_distance, distance);
                _enemyHealth = Math.Min(_enemyHealth, health);
                _lastProgress = situation.Tick;
            }
            if (situation.Tick - _lastProgress >= situation.BlockedTicks)
            {
                _retryAt = situation.Tick + situation.BlockedTicks;
                _attacking = false;
            }
        }
        else if (situation.Tick >= _retryAt && Enough(situation.AtRally))
        {
            _attacking = true;
            _lastProgress = situation.Tick;
            _distance = _enemyHealth = long.MaxValue;
        }

        return _attacking ? new(CommandKind.AttackMove, situation.Objective) : situation.RallyOrder(members);
    }
}

internal sealed class PatrolStrategy : ISergeantStrategy
{
    private bool _outbound;
    private long? _started;

    public SergeantOrder Plan(SergeantSituation situation, IReadOnlyList<EntityPose> members, IReadOnlyList<AiMember> roster)
    {
        _started ??= situation.Tick;
        if (members.All(_outbound ? situation.AtObjective : situation.AtRally) ||
            situation.Tick - _started >= situation.BlockedTicks)
        {
            _outbound = !_outbound;
            _started = situation.Tick;
        }
        return new(CommandKind.MoveSelected, _outbound ? situation.Objective : situation.Rally);
    }
}
