using OpenRTS.Core;
using OpenRTS.Core.Orders;
using OpenRTS.Core.Presentation;

namespace OpenRTS.AI;

/// <summary>Skirmish policy for an exclusively controlled player. Call once per completed world tick.</summary>
public sealed class SkirmishAi
{
    public const int PolicyVersion = 3;

    private readonly SkirmishAiProfile _profile;
    private readonly IReadOnlyList<UnitDefinition> _types;
    private readonly WorldPosition[] _buildingSites;
    private readonly WorldPosition _rally, _attack;
    private readonly uint _player;
    private readonly Dictionary<long, int> _placementAttempts = [];
    private readonly Dictionary<int, int> _siteAttempts = [];
    private readonly List<WorldCommand> _commands = [];
    private EntityPose[] _poses = [];
    private PlayerPose[] _players = [];
    private TaskPose[] _tasks = [];
    private ConstructionProjectPose[] _projects = [];
    private EntityId _engineer, _deposit;
    private WorldPosition _capturePosition;
    private int _captureProgress;
    private long _captureLastProgress, _captureRetryAt;
    private EntityId _avoidDeposit;
    private EntityId _mine, _mineBuilder, _avoidMine;
    private WorldPosition _mineBuilderPosition;
    private int _mineWork;
    private long _mineProgressAt, _mineRetryAt;
    private EntityId[] _group = [];
    private CommandKind _groupCommand;
    private long _groupLastProgress, _attackRetryAt;
    private long _attackDistance = long.MaxValue;
    private long _enemyHealth = long.MaxValue;
    private bool _assembled;
    private long _lastTick = -1;
    private string _productionStatus = "start";
    private string _diagnostics = "start";

    public string Diagnostics => Volatile.Read(ref _diagnostics);

    public SkirmishAi(uint player, SkirmishAiProfile profile, IReadOnlyList<UnitDefinition> types,
        IEnumerable<WorldPosition> buildingSites, WorldPosition rally, WorldPosition attack)
    {
        ArgumentOutOfRangeException.ThrowIfZero(player);
        _player = player;
        _profile = profile;
        _types = types;
        _buildingSites = buildingSites.ToArray();
        if (_buildingSites.Length is < 1 or > 64) throw new ArgumentException("AI0 requires 1..64 building sites.", nameof(buildingSites));
        _rally = rally;
        _attack = attack;
    }

    public WorldCommand[] Plan(SimulationWorld world)
    {
        long tick = world.Tick;
        if (tick != _lastTick + 1) throw new ArgumentException("AI planning requires consecutive completed ticks.", nameof(world));
        _lastTick = tick;
        bool macro = tick % _profile.MacroIntervalTicks == 0;
        bool tactics = tick % _profile.TacticsIntervalTicks == 0;
        if (!macro && !tactics) return [];
        _commands.Clear();
        Array.Resize(ref _poses, world.Count);
        world.CopyPoses(_poses);
        var own = _poses.Where(p => p.Presentation.Owner == _player && p.Carrier == default)
            .OrderBy(p => p.Id.Value).ToArray();
        if (tick == 0) ValidateStart(world, own);
        if (macro)
        {
            Array.Resize(ref _players, world.PlayerCount);
            world.CopyPlayers(_players);
            Array.Resize(ref _tasks, world.TaskCount);
            world.CopyTaskPoses(_tasks);
            Array.Resize(ref _projects, world.ConstructionProjectCount);
            world.CopyConstructionProjects(_projects);
            long credits = PlanProduction(tick, own);
            PlanCapture(tick, own);
            PlanMineUpgrade(tick, own, credits);
        }
        if (tactics) PlanGroup(tick, own);
        var goal = _groupCommand == CommandKind.AttackMove ? _attack : _rally;
        string squad = _group.Length == 0 ? "waiting for recruits" : $"{_groupCommand} ({goal.X}, {goal.Y}), {_group.Length} units";
        string capture = _deposit == default ? "idle" : $"deposit {_deposit.Value}, progress {_captureProgress}";
        string logistics = _mine == default ? "" : $" · mine {_mine.Value}, work {_mineWork}";
        Volatile.Write(ref _diagnostics, $"T{tick} · {_productionStatus} · {squad} · capture {capture}{logistics}");
        return _commands.Select((command, i) => command.WithStamp(tick + 1, _player, i + 1)).ToArray();
    }

    private void ValidateStart(SimulationWorld world, EntityPose[] own)
    {
        if (world.NavigationGrid is null || !_buildingSites.Append(_rally).Append(_attack)
            .All(p => p.X >= 0 && p.X <= world.Width && p.Y >= 0 && p.Y <= world.Depth))
            throw new ArgumentException("AI0 requires a navigation map with in-bounds scenario points.", nameof(world));
        var reachable = own.Where(p => !p.Construction.Active).Select(p => p.UnitType).ToHashSet();
        if (!reachable.Contains(_profile.EngineerType))
            throw new ArgumentException("AI0 requires its capture unit in the starting set.", nameof(world));
        foreach (int type in _profile.Opening)
        {
            if (reachable.Contains(type)) continue;
            var recipe = _profile.Buildings.First(b => b.Recipe.ProductType == type).Recipe;
            if (!reachable.Contains(recipe.ProducerType))
                throw new ArgumentException($"Opening step '{_types[type].Id}' needs an earlier producer.", nameof(world));
            reachable.Add(type);
        }
        int previousCount;
        do
        {
            previousCount = reachable.Count;
            foreach (var building in _profile.Buildings)
                if (reachable.Contains(building.Recipe.ProducerType)) reachable.Add(building.Recipe.ProductType);
        } while (reachable.Count > previousCount);
        if (_profile.Buildings.Any(b => !reachable.Contains(b.Recipe.ProducerType)) ||
            _profile.Group.Any(m => !reachable.Contains(m.Recipe.ProducerType)) ||
            _profile.Supply is { } supply && !reachable.Contains(supply.Truck.ProducerType) ||
            _profile.ReplacementEngineer is { } replacement && !reachable.Contains(replacement.ProducerType))
            throw new ArgumentException("AI0 profile requires a producer unreachable from the starting set.", nameof(world));
    }

    private bool FreeBuilder(EntityPose actor) => actor.Id != _engineer && actor.Id != _mineBuilder &&
        !actor.Construction.Active && !actor.TerrainStranded &&
        actor.Order.Phase == OrderPhase.Idle && actor.Order.QueuedCount == 0 && !actor.Upgrade.Active &&
        !_commands.Any(c => c.Entity == actor.Id);

    private long PlanProduction(long tick, EntityPose[] own)
    {
        foreach (long finished in _placementAttempts.Keys.Where(id => !_tasks.Any(t => t.Id == id)).ToArray())
            _placementAttempts.Remove(finished);

        long unpaidTasks = _tasks.Where(t => t.Payer == _player).Sum(t => (long)t.UnpaidCost);
        long credits = _players.First(p => p.Id == _player).Credits - _profile.CreditReserve - unpaidTasks -
            _projects.Where(p => p.Owner == _player).Sum(p => (long)p.UnpaidCost);
        var plannedQueues = new HashSet<(EntityId, int)>();
        bool IssueProduction(AiRecipe recipe)
        {
            foreach (var producer in own)
            {
                if (producer.UnitType != recipe.ProducerType || producer.Construction.Active) continue;
                if (!_types[producer.UnitType].ProductEnabled(recipe.Action, producer.Upgrade.Completed)) continue;
                var key = (producer.Id, recipe.Queue);
                if (plannedQueues.Contains(key)) continue;
                if (_tasks.Any(t => t.Producer == producer.Id && t.Queue == recipe.Queue)) continue;
                if (credits < recipe.Cost) continue;
                if (recipe.OnSite)
                {
                    if (!FreeBuilder(producer) || _projects.Any(p => p.Owner == _player) ||
                        _commands.Any(c => c.Kind is CommandKind.PlanConstruction or CommandKind.BuildSite)) continue;
                    int attempt = _siteAttempts.GetValueOrDefault(recipe.ProductType);
                    _siteAttempts[recipe.ProductType] = (attempt + 1) % _buildingSites.Length;
                    _commands.Add(new(0, 0, 0, CommandKind.PlanConstruction, producer.Id,
                        _buildingSites[attempt], Action: recipe.Action, Queued: true));
                }
                else _commands.Add(new(0, 0, 0, CommandKind.EnqueueTask, producer.Id, Action: recipe.Action));
                credits -= recipe.Cost;
                plannedQueues.Add(key);
                return true;
            }
            return false;
        }

        int Count(int type) => own.Count(p => p.UnitType == type && !p.Construction.Active);
        int Pending(int type) => _tasks.Count(t => t.UnitType == type && own.Any(p => p.Id == t.Producer)) +
            own.Count(p => p.UnitType == type && p.Construction.Active) +
            _projects.Count(p => p.Owner == _player && p.UnitType == type);

        var unfinished = _projects.Where(p => p.Owner == _player).Select(p => (p.Id, p.UnitType))
            .Concat(own.Where(p => p.Construction.Active).Select(p => (p.Id, p.UnitType))).OrderBy(p => p.Id.Value);
        foreach (var site in unfinished)
        {
            var recipe = _profile.Buildings.FirstOrDefault(b => b.Recipe.OnSite && b.Recipe.ProductType == site.UnitType)?.Recipe;
            if (recipe is null || own.Any(p => p.Order is { Kind: UnitActionKind.Build, Phase: not OrderPhase.Idle } && p.Order.Target == site.Id)) continue;
            var builder = Array.Find(own, p => p.UnitType == recipe.ProducerType && FreeBuilder(p));
            if (builder.Id == default) continue;
            _commands.Add(new(0, 0, 0, CommandKind.BuildSite, builder.Id, TargetEntity: site.Id));
            break;
        }

        // The opening observes completed buildings. A paid, ready task still needs its placement command.
        var opening = _profile.Opening.FirstOrDefault(type => Count(type) == 0, -1);
        _productionStatus = opening < 0 ? "maintenance" : $"opening: {_types[opening].Id}";
        foreach (var building in _profile.Buildings)
            if (!building.Recipe.OnSite && Pending(building.Recipe.ProductType) > 0)
                PlaceReady(building.Recipe);
        if (opening >= 0)
        {
            var building = _profile.Buildings.First(b => b.Recipe.ProductType == opening);
            if (Pending(opening) == 0) IssueProduction(building.Recipe);
        }
        else
        {
            foreach (var building in _profile.Buildings)
                if (Count(building.Recipe.ProductType) + Pending(building.Recipe.ProductType) < building.Target)
                    IssueProduction(building.Recipe);
        }
        if (_profile.ReplacementEngineer is { } engineer && Count(engineer.ProductType) + Pending(engineer.ProductType) == 0)
            IssueProduction(engineer);
        if (_profile.Supply is { } supply && (opening < 0 || Count(supply.Truck.ProductType) == 0) &&
            Count(supply.Truck.ProductType) + Pending(supply.Truck.ProductType) < supply.Target &&
            _poses.Any(p => (_types[p.UnitType].Supply?.Role == SupplyRole.Mine) && p.Supply.Amount > 0 &&
                (p.Presentation.Owner == 0 || p.Presentation.Owner == _player)))
            IssueProduction(supply.Truck);
        foreach (var member in _profile.Group)
            if (Count(member.Recipe.ProductType) + Pending(member.Recipe.ProductType) < member.Target)
                IssueProduction(member.Recipe);
        var production = _commands.FirstOrDefault();
        if (production.Action is { } action) _productionStatus += $" · {production.Kind} {action}";
        else if (opening >= 0)
        {
            var recipe = _profile.Buildings.First(b => b.Recipe.ProductType == opening).Recipe;
            bool ready = _tasks.Any(t => t.UnitType == opening && t.Ready && own.Any(p => p.Id == t.Producer));
            _productionStatus += ready ? " · waiting for placement retry" : Pending(opening) > 0 ? " · producing" :
                credits < recipe.Cost ? " · waiting for credits" : " · waiting for producer";
        }
        if (unpaidTasks > 0) _productionStatus += $" · reserved for tasks: {unpaidTasks}";
        return credits;

        void PlaceReady(AiRecipe recipe)
        {
            foreach (var task in _tasks.Where(t => t.Ready && t.UnitType == recipe.ProductType).OrderBy(t => t.Id))
            {
                var producer = Array.Find(own, p => p.Id == task.Producer && p.UnitType == recipe.ProducerType && !p.Construction.Active);
                if (producer.Id == default || task.Queue != recipe.Queue ||
                    task.Product >= _types[producer.UnitType].Products.Count ||
                    _types[producer.UnitType].Products[task.Product].Id != recipe.Action)
                    continue;
                int attempt = _placementAttempts.GetValueOrDefault(task.Id);
                // shortcut: retry scenario sites in rounds until AI2 introduces placement search.
                if (attempt > 0 && attempt % _buildingSites.Length == 0 && tick % (_profile.MacroIntervalTicks * 4L) != 0)
                    continue;
                if (!plannedQueues.Add((producer.Id, task.Queue))) continue;
                _placementAttempts[task.Id] = attempt + 1;
                _commands.Add(new(0, 0, 0, CommandKind.PlaceProduced, producer.Id,
                    _buildingSites[attempt % _buildingSites.Length], Action: recipe.Action, TaskId: task.Id));
            }
        }
    }

    private void PlanCapture(long tick, EntityPose[] own)
    {
        var actor = Array.Find(own, p => p.Id == _engineer);
        var target = Array.Find(_poses, p => p.Id == _deposit);
        if (actor.Id != default && target.Id != default && target.Presentation.Owner == 0)
        {
            if (actor.Current != _capturePosition || actor.Order.CaptureProgress > _captureProgress)
            {
                _capturePosition = actor.Current;
                _captureProgress = actor.Order.CaptureProgress;
                _captureLastProgress = tick;
            }
            if (tick - _captureLastProgress < _profile.BlockedGoalTicks) return;
            _commands.Add(new(0, 0, 0, CommandKind.Stop, actor.Id));
            _avoidDeposit = target.Id;
            _captureRetryAt = tick + _profile.MacroIntervalTicks;
            _deposit = default;
            _engineer = default;
            return;
        }
        if (actor.Id != default && target.Presentation.Owner != _player && actor.Order.Kind == UnitActionKind.Capture)
            _commands.Add(new(0, 0, 0, CommandKind.Stop, actor.Id));
        _deposit = default;
        _engineer = default;

        bool IncomeSource(EntityPose pose) => _types[pose.UnitType].Economy is { Income: > 0 } &&
            _types[pose.UnitType].Capturable is not null && !pose.Construction.Active;
        if (tick < _captureRetryAt || own.Any(IncomeSource)) return;
        actor = Array.Find(own, p => p.UnitType == _profile.EngineerType && FreeBuilder(p));
        if (actor.Id == default) return;
        var candidates = _poses.Where(p => p.Presentation.Owner == 0 && p.Carrier == default && IncomeSource(p))
            .OrderBy(p => p.Id == _avoidDeposit).ThenBy(p => Distance(p.Current, actor.Current)).ThenBy(p => p.Id.Value);
        target = candidates.FirstOrDefault();
        if (target.Id == default) return;
        _engineer = actor.Id;
        _deposit = target.Id;
        _capturePosition = actor.Current;
        _captureProgress = 0;
        _captureLastProgress = tick;
        _commands.Add(new(0, 0, 0, CommandKind.UseAbility, actor.Id, TargetEntity: target.Id, Action: _profile.CaptureAction));
    }

    private void PlanMineUpgrade(long tick, EntityPose[] own, long credits)
    {
        if (_profile.Supply is not { ImproveMines: true } supply) return;
        if (_mine != default)
        {
            var target = Array.Find(_poses, p => p.Id == _mine);
            var support = Array.Find(own, p => p.Id == _mineBuilder);
            if (target.Upgrade.Project is not null && target.Upgrade.Payer == _player && support.Id != default)
            {
                if (target.Upgrade.Work > _mineWork || support.Current != _mineBuilderPosition)
                {
                    _mineWork = target.Upgrade.Work;
                    _mineBuilderPosition = support.Current;
                    _mineProgressAt = tick;
                }
                if (tick - _mineProgressAt < _profile.BlockedGoalTicks) return;
                _commands.Add(new(0, 0, 0, CommandKind.CancelUpgrade, Owner: _player, TargetEntity: _mine));
                _commands.Add(new(0, 0, 0, CommandKind.Stop, support.Id));
            }
            if (target.Upgrade.Completed is null)
            {
                _avoidMine = _mine;
                _mineRetryAt = tick + _profile.BlockedGoalTicks;
            }
            _mine = _mineBuilder = default;
        }
        if (tick < _mineRetryAt || _profile.Opening.Any(type => !own.Any(p => p.UnitType == type && !p.Construction.Active)) ||
            own.Any(p => _types[p.UnitType].Supply?.Role == SupplyRole.Mine && p.Supply.Amount > 0)) return;

        var builder = Array.Find(own, p => p.UnitType == _profile.EngineerType && FreeBuilder(p));
        if (builder.Id == default) return;
        var serviced = own.Where(p => p.UnitType == supply.Truck.ProductType && p.Supply.Automatic)
            .SelectMany(p => new[] { p.Supply.Station, p.Supply.Mine }).ToHashSet();
        foreach (var target in _poses.Where(p => p.Presentation.Owner == 0 && serviced.Contains(p.Id) && p.Supply.Amount > 0 &&
                     _types[p.UnitType].Supply?.Role == SupplyRole.Mine && p.Upgrade is { Project: null, Completed: null })
                     .OrderBy(p => p.Id == _avoidMine).ThenBy(p => Distance(p.Current, builder.Current)).ThenBy(p => p.Id.Value))
        {
            if (_types[target.UnitType].DefaultUpgrade is not { Support: true, ClaimNeutral: true } upgrade || credits < upgrade.Cost) continue;
            _mine = target.Id;
            _mineBuilder = builder.Id;
            _mineBuilderPosition = builder.Current;
            _mineWork = 0;
            _mineProgressAt = tick;
            _commands.Add(new(0, 0, 0, CommandKind.StartUpgrade, builder.Id, TargetEntity: target.Id, Action: upgrade.Id));
            break;
        }
    }

    private void PlanGroup(long tick, EntityPose[] own)
    {
        const long assemblyRadiusSquared = 4096L * 4096;
        const long defenseRadiusSquared = 12288L * 12288;
        var members = _profile.Group.SelectMany(member => own.Where(p => p.UnitType == member.Recipe.ProductType)
            .Take(member.Target)).OrderBy(p => p.Id.Value).ToArray();
        if (members.Length == 0) { _group = []; _assembled = false; return; }

        bool Enough(Func<EntityPose, bool> predicate) => _profile.Group.All(member => members.Count(
            p => p.UnitType == member.Recipe.ProductType && predicate(p)) >= member.Minimum);
        bool Assembled(EntityPose p) => Distance(p.Current, _rally) <= assemblyRadiusSquared;
        var enemies = _poses.Where(p => p.Presentation.Owner != 0 && p.Presentation.Owner != _player && p.Carrier == default
            && _types[p.UnitType].Destruction != DestructionPolicy.Invulnerable).ToArray();
        bool defend = enemies.Any(p => _types[p.UnitType].Weapons.Count > 0 && Distance(p.Current, _rally) <= defenseRadiusSquared);
        if (!Enough(_ => true) || defend) _assembled = false;
        else if (Enough(Assembled)) _assembled = true;

        if (_groupCommand == CommandKind.AttackMove)
        {
            long distance = members.Min(p => Distance(p.Current, _attack));
            long health = enemies.Where(p => Distance(p.Current, _attack) <= defenseRadiusSquared).Sum(p => (long)p.Health);
            if (distance < _attackDistance || health < _enemyHealth)
            {
                _attackDistance = Math.Min(_attackDistance, distance);
                _enemyHealth = Math.Min(_enemyHealth, health);
                _groupLastProgress = tick;
            }
            if (tick - _groupLastProgress >= _profile.BlockedGoalTicks)
            {
                _attackRetryAt = tick + _profile.BlockedGoalTicks;
                _assembled = false;
            }
        }

        CommandKind kind = defend && members.All(Assembled) ? CommandKind.HoldPosition :
            !defend && _assembled && tick >= _attackRetryAt && enemies.Length > 0 ? CommandKind.AttackMove : CommandKind.MoveSelected;
        // An idle, already assembled group can stay put without a fresh Move.
        if (kind == CommandKind.MoveSelected && members.All(Assembled)) kind = CommandKind.HoldPosition;
        var ids = members.Select(p => p.Id).ToArray();
        if (kind == _groupCommand && ids.SequenceEqual(_group)) return;
        if (kind == CommandKind.AttackMove && _groupCommand != CommandKind.AttackMove)
        {
            _groupLastProgress = tick;
            _attackDistance = long.MaxValue;
            _enemyHealth = long.MaxValue;
        }
        _group = ids;
        _groupCommand = kind;
        _commands.Add(new(0, 0, 0, kind, Position: kind == CommandKind.AttackMove ? _attack : _rally, Recipients: new EntitySet(ids)));
    }

    private static long Distance(WorldPosition a, WorldPosition b)
    {
        long x = (long)a.X - b.X, y = (long)a.Y - b.Y;
        return x * x + y * y;
    }
}
