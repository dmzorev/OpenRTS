using OpenRTS.Core;
using OpenRTS.Core.Navigation;
using OpenRTS.Core.Orders;
using OpenRTS.Core.Presentation;

namespace OpenRTS.AI;

public readonly record struct BaseConstructionDecision(WorldCommand Command, int CreditCost = 0, int Queue = -1);

/// <summary>One base plan. Returns a proposal while the host retains control of budgets and commands.</summary>
public sealed class BaseConstructionPlanner
{
    private const int SearchBudget = 256;
    private readonly uint _player;
    private readonly WorldPosition _center;
    private readonly IReadOnlyList<UnitDefinition> _types;
    private readonly AiBuilding[] _requirements;
    private readonly int[] _opening;
    private readonly int _blockedTicks;
    private readonly IBuildingSiteSearch _strategy;
    private IEnumerator<BuildingSite>? _candidates;
    private NavigationGrid? _searchGrid, _accessGrid;
    private readonly Dictionary<(int Type, EntityId Builder, long Task), long> _retryAfter = [];
    private (int Type, EntityId Builder, long Task) _searchKey;
    private (EntityId Site, EntityId Builder) _tracked;
    private long _bestDistance, _progressTick;

    public string Status { get; private set; } = "start";
    public long CandidatesChecked { get; private set; }

    public BaseConstructionPlanner(uint player, WorldPosition center, IReadOnlyList<UnitDefinition> types,
        IEnumerable<AiBuilding> requirements, IEnumerable<int>? opening = null, int blockedTicks = 250, IBuildingSiteSearch? strategy = null)
    {
        _player = player;
        _center = center;
        _types = types;
        _requirements = requirements.OrderByDescending(b => b.Priority).ThenBy(b => b.Recipe.ProductType).ToArray();
        _opening = (opening ?? []).ToArray();
        _blockedTicks = blockedTicks;
        _strategy = strategy ?? new CompactBuildingSiteSearch();
    }

    public BaseConstructionDecision? Plan(SimulationWorld world, EntityPose[] poses, TaskPose[] tasks,
        ConstructionProjectPose[] projects, long credits, Func<EntityPose, bool> freeBuilder)
    {
        var grid = world.BaseNavigationGrid ?? throw new ArgumentException("Base construction requires a terrain grid.", nameof(world));
        if (!ReferenceEquals(grid, _searchGrid) || !ReferenceEquals(world.NavigationGrid, _accessGrid))
        {
            _searchGrid = grid;
            _accessGrid = world.NavigationGrid;
            _candidates?.Dispose();
            _candidates = null;
            _retryAfter.Clear();
        }
        foreach (var key in _retryAfter.Where(pair => pair.Value <= world.Tick).Select(pair => pair.Key).ToArray())
            _retryAfter.Remove(key);
        var own = poses.Where(p => p.Presentation.Owner == _player && p.Carrier == default).OrderBy(p => p.Id.Value).ToArray();
        var ownIds = own.Select(p => p.Id).ToHashSet();
        var blueprints = projects.Where(p => p.Owner == _player).ToArray();
        int opening = _opening.FirstOrDefault(t => !own.Any(p => p.UnitType == t && !p.Construction.Active), -1);
        Status = opening >= 0 ? $"opening: {_types[opening].Id}" : "maintenance";
        SiteOccupancy? occupancy = null;

        foreach (var site in blueprints.Select(p => (p.Id, p.UnitType, p.Position, p.QuarterTurns, Unpaid: true))
                     .Concat(own.Where(p => p.Construction.Active).Select(p => (p.Id, p.UnitType, Position: p.Current, p.QuarterTurns, Unpaid: false)))
                     .OrderBy(p => p.Id.Value))
        {
            var recipe = _requirements.FirstOrDefault(b => b.Recipe.OnSite && b.Recipe.ProductType == site.UnitType)?.Recipe;
            if (recipe is null) continue;
            var active = Array.Find(own, p => p.Order is { Kind: UnitActionKind.Build, Phase: not OrderPhase.Idle } && p.Order.Target == site.Id);
            if (site.Unpaid && (!TerrainAllows(grid, site.UnitType, new(site.Position, site.QuarterTurns)) ||
                active.Id != default && Stalled(world.Tick, site.Id, site.Position, active)))
                return Decide(new(0, 0, 0, CommandKind.CancelConstruction, Owner: _player, TargetEntity: site.Id));
            if (active.Id != default) continue;
            var builder = Array.Find(own, p => p.UnitType == recipe.ProducerType && freeBuilder(p) &&
                world.CanAccessBuildingSite(p.Id, site.UnitType, site.Position, site.QuarterTurns));
            if (builder.Id != default) return Decide(new(0, 0, 0, CommandKind.BuildSite, builder.Id, TargetEntity: site.Id));
        }

        foreach (var building in _requirements)
        {
            var recipe = building.Recipe;
            if (recipe.OnSite) continue;
            foreach (var task in tasks.Where(t => t.Ready && t.UnitType == recipe.ProductType && t.Queue == recipe.Queue).OrderBy(t => t.Id))
            {
                var producer = Array.Find(own, p => p.Id == task.Producer && p.UnitType == recipe.ProducerType && !p.Construction.Active);
                if (producer.Id == default || _types[producer.UnitType].Products[task.Product].Id != recipe.Action) continue;
                if (_retryAfter.ContainsKey((recipe.ProductType, producer.Id, task.Id))) continue;
                if (FindSite(recipe, producer.Id, task.Id) is { } site)
                    return Decide(new WorldCommand(0, 0, 0, CommandKind.PlaceProduced, producer.Id, site.Position,
                        Action: recipe.Action, TaskId: task.Id) { QuarterTurns = site.QuarterTurns }, queue: recipe.Queue);
                return null;
            }
        }

        foreach (var building in _requirements)
        {
            var recipe = building.Recipe;
            int type = recipe.ProductType;
            if (opening >= 0 && opening != type) continue;
            int goal = opening >= 0 ? 1 : building.Target;
            int pending = own.Count(p => p.UnitType == type) + blueprints.Count(p => p.UnitType == type) +
                tasks.Count(t => t.UnitType == type && ownIds.Contains(t.Producer));
            if (pending >= goal) continue;
            if (credits < recipe.Cost) { Status += $" · waiting for credits: {_types[type].Id}"; continue; }
            foreach (var producer in own)
            {
                if (producer.UnitType != recipe.ProducerType || producer.Construction.Active ||
                    !_types[producer.UnitType].ProductEnabled(recipe.Action, producer.Upgrade.Completed)) continue;
                if (!recipe.OnSite)
                {
                    if (tasks.Any(t => t.Producer == producer.Id && t.Queue == recipe.Queue)) continue;
                    return Decide(new(0, 0, 0, CommandKind.EnqueueTask, producer.Id, Action: recipe.Action), recipe.Cost, recipe.Queue);
                }
                if (blueprints.Length != 0 || !freeBuilder(producer)) continue;
                if (_retryAfter.ContainsKey((recipe.ProductType, producer.Id, 0))) continue;
                if (FindSite(recipe, producer.Id, 0) is { } site)
                    return Decide(new WorldCommand(0, 0, 0, CommandKind.PlanConstruction, producer.Id, site.Position,
                        Action: recipe.Action, Queued: true) { QuarterTurns = site.QuarterTurns }, recipe.Cost);
                return null;
            }
            Status += $" · waiting for builder or queue: {_types[type].Id}";
        }
        return null;

        BuildingSite? FindSite(AiRecipe recipe, EntityId producer, long task)
        {
            var key = (recipe.ProductType, producer, task);
            if (_candidates is null || _searchKey != key)
            {
                _candidates?.Dispose();
                _searchKey = key;
                _candidates = _strategy.Candidates(grid, _center, _types[recipe.ProductType]).GetEnumerator();
            }
            occupancy ??= new(grid, _types, poses, projects);
            BuildingSite? fallback = null;
            for (int i = 0; i < SearchBudget; i++)
            {
                if (!_candidates.MoveNext())
                {
                    _candidates.Dispose();
                    _candidates = null;
                    if (fallback is null) _retryAfter[key] = world.Tick + _blockedTicks;
                    Status += $" · no site: {_types[recipe.ProductType].Id}";
                    return fallback;
                }
                var candidate = _candidates.Current;
                CandidatesChecked++;
                if (candidate.QuarterTurns is < 0 or > 3 || !TerrainAllows(grid, recipe.ProductType, candidate)) continue;
                var footprint = _types[recipe.ProductType].WithRotation(candidate.QuarterTurns).Footprint!;
                if (occupancy.Overlaps(footprint, candidate.Position) ||
                    !(recipe.OnSite ? world.CanAccessBuildingSite(producer, recipe.ProductType, candidate.Position, candidate.QuarterTurns) :
                        world.IsProducedBuildingInRange(producer, recipe.Action, candidate.Position, candidate.QuarterTurns))) continue;
                if (occupancy.HasClearance(footprint, candidate.Position)) return candidate;
                fallback ??= candidate;
            }
            Status += $" · searching: {_types[recipe.ProductType].Id}";
            return fallback;
        }

        BaseConstructionDecision Decide(WorldCommand command, int cost = 0, int queue = -1)
        {
            Status += $" · {command.Kind} {command.Action}";
            return new(command, cost, queue);
        }
    }

    private bool TerrainAllows(NavigationGrid grid, int type, BuildingSite site)
    {
        var definition = _types[type].WithRotation(site.QuarterTurns);
        return definition.Footprint!.AllowsConstruction(grid, site.Position, definition.Locomotor);
    }

    private bool Stalled(long tick, EntityId site, WorldPosition position, EntityPose builder)
    {
        long x = (long)position.X - builder.Current.X, y = (long)position.Y - builder.Current.Y;
        long distance = x * x + y * y;
        if (_tracked != (site, builder.Id) || distance < _bestDistance)
        {
            _tracked = (site, builder.Id);
            _bestDistance = distance;
            _progressTick = tick;
        }
        return tick - _progressTick >= _blockedTicks;
    }

    private sealed class SiteOccupancy
    {
        private readonly NavigationGrid _grid;
        private readonly HashSet<int> _occupied = [];

        public SiteOccupancy(NavigationGrid grid, IReadOnlyList<UnitDefinition> types, EntityPose[] poses, ConstructionProjectPose[] projects)
        {
            _grid = grid;
            foreach (var pose in poses)
                if (pose.Carrier == default && types[pose.UnitType].WithRotation(pose.QuarterTurns).Footprint is { } footprint)
                    Add(footprint, pose.Current);
            foreach (var project in projects)
                if (project.Upgrade is null && types[project.UnitType].WithRotation(project.QuarterTurns).Footprint is { } footprint)
                    Add(footprint, project.Position);
        }

        private void Add(FootprintDefinition footprint, WorldPosition position)
        {
            foreach (var cell in footprint.Cells)
                Visit(footprint.Bounds(cell, position), 0, index => { _occupied.Add(index); return false; });
        }

        public bool Overlaps(FootprintDefinition footprint, WorldPosition position) =>
            footprint.Cells.Any(cell => Visit(footprint.Bounds(cell, position), 0, _occupied.Contains));

        public bool HasClearance(FootprintDefinition footprint, WorldPosition position) =>
            !Visit(new(position.X - footprint.HalfWidth, position.Y - footprint.HalfHeight,
                position.X + footprint.HalfWidth, position.Y + footprint.HalfHeight), 1, _occupied.Contains);

        private bool Visit(WorldBox bounds, int padding, Func<int, bool> visitor)
        {
            int size = _grid.CellSize;
            int left = Math.Max(0, bounds.MinX / size - padding);
            int top = Math.Max(0, bounds.MinY / size - padding);
            int right = Math.Min(_grid.Width - 1, (bounds.MaxX - 1) / size + padding);
            int bottom = Math.Min(_grid.Height - 1, (bounds.MaxY - 1) / size + padding);
            for (int y = top; y <= bottom; y++)
                for (int x = left; x <= right; x++)
                    if (visitor(y * _grid.Width + x)) return true;
            return false;
        }
    }
}
