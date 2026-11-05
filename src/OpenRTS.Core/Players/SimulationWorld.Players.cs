using Frent;
using OpenRTS.Core.Simulation;
using OpenRTS.Core.Production;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    public const long MaximumCredits = 1000000000000000;
    private readonly SortedDictionary<uint, PlayerState> _players = new();
    private bool _powerDirty = true;
    public int PlayerCount => _players.Count;
    private void InitializePlayers(IEnumerable<PlayerDefinition>? players)
    {
        _players.Add(0, new(new(0, "Нейтральный")));
        foreach (var p in players ?? [])
        {
            if (p.Id == 0 || string.IsNullOrWhiteSpace(p.Name) || p.Credits < 0 || p.Credits > MaximumCredits || !_players.TryAdd(p.Id, new(p)))
                throw new ArgumentException("Players require unique nonzero IDs, names and bounded credits.");
        }
    }
    private PlayerState Player(uint owner)
    {
        if (!_players.TryGetValue(owner, out var player)) _players.Add(owner, player = new(new(owner, $"Игрок {owner}")));
        return player;
    }
    private void UpdatePower()
    {
        if (!_powerDirty) return;
        foreach (var p in _players.Values) p.Produced = p.Consumed = 0;
        if (_unitStorage is not null)
            foreach (var (definition, ownership) in _unitStorage.Query<EntityDefinition, OwnershipState>())
                if (ownership.Value.Owner != 0 && definition.Value.Economy is { } economy)
                {
                    var p = Player(ownership.Value.Owner);
                    if (economy.Energy >= 0) p.Produced += economy.Energy; else p.Consumed -= economy.Energy;
                }
        _powerDirty = false;
    }
    private bool LowPower(uint owner) { UpdatePower(); return owner != 0 && Player(owner).Produced < Player(owner).Consumed; }
    public void CopyPlayers(Span<PlayerPose> destination)
    {
        ThrowIfDisposed(); if (_failed) throw new InvalidOperationException("Incomplete world.");
        UpdatePower(); if (destination.Length < _players.Count) throw new ArgumentException("Destination is too small.");
        int i = 0; foreach (var p in _players.Values) destination[i++] = new(p.Id, p.Name, p.Credits, p.Produced, p.Consumed);
    }
    private bool Spend(uint owner, long amount)
    {
        if (amount < 0) throw new InvalidOperationException("Negative payment.");
        var p = Player(owner); if (owner == 0 || p.Credits < amount) return false;
        p.Credits -= amount; return true;
    }
    private void Credit(uint owner, long amount)
    {
        if (owner != 0) { var p = Player(owner); p.Credits += Math.Min(MaximumCredits - p.Credits, amount); }
    }
    private void UpdateIncome()
    {
        if (_unitStorage is null) return;
        foreach (var (income, owner, definition) in _unitStorage.Query<IncomeState, OwnershipState, EntityDefinition>())
        {
            var settings = definition.Value.Economy!;
            if (Tick < income.Value.NextTick) continue;
            income.Value.NextTick = checked(Tick + settings.Interval);
            Credit(owner.Value.Owner, settings.Income);
        }
    }
    private void Refund(ProductionTask task, BuildDefinition build)
        => Credit(task.Payer, Math.Max(0, task.Paid - (long)build.Cost * Math.Min(task.Progress, build.Ticks) / build.Ticks));
    private void ChangeOwner(int index, uint owner)
    {
        var entity = _unitHandles[index]; var identity = entity.Get<UnitIdentity>();
        uint previous = entity.Get<OwnershipState>().Owner; if (previous == owner) return;
        Player(owner);
        if (TryGetProducer(index, out var producer))
        {
            for (int q = 0; q < producer.Queues.Length; q++)
            {
                foreach (var task in producer.Queues[q]) Refund(task, _builds[identity.UnitType][task.Product]);
                producer.Queues[q].Clear();
            }
            producer.HasRally = false;
        }
        ref var orders = ref entity.Get<OrderState>(); orders.HasOrder = false; orders.PendingOrders?.Clear(); orders.CombatTarget = default;
        Halt(index); CancelWindups(index);
        ref var visual = ref entity.Get<PresentationState>(); visual.HasAim = false; visual.AimEntity = default;
        if (entity.Has<CaptureState>()) entity.Get<CaptureState>().Progress = 0;
        entity.Get<OwnershipState>().Owner = owner;
        if (entity.Has<IncomeState>()) entity.Get<IncomeState>().NextTick = checked(Tick + _types[identity.UnitType].Economy!.Interval);
        if (entity.Has<CapturableState>()) entity.Get<CapturableState>().ChangedTick = Tick;
        _powerDirty = true;
        _events.Add(new(Tick, WorldEventKind.OwnerChanged, identity.Id, entity.Get<SpatialState>().Position, identity.UnitType, Owner: owner, PreviousOwner: previous));
    }
    private void WritePlayers(BinaryWriter writer)
    {
        UpdatePower(); writer.Write(_players.Count);
        foreach (var p in _players.Values) { writer.Write(p.Id); writer.Write(p.Name); writer.Write(p.Credits); writer.Write(p.Produced); writer.Write(p.Consumed); }
        foreach (var entity in _unitHandles.AsSpan(0, Count).ToArray().OrderBy(e => e.Get<UnitIdentity>().Id.Value))
        {
            writer.Write(entity.Has<IncomeState>()); if (entity.Has<IncomeState>()) writer.Write(entity.Get<IncomeState>().NextTick);
            writer.Write(entity.Has<CapturableState>()); if (entity.Has<CapturableState>()) writer.Write(entity.Get<CapturableState>().ChangedTick);
            writer.Write(entity.Has<CaptureState>()); if (entity.Has<CaptureState>()) { writer.Write(entity.Get<CaptureState>().Progress); writer.Write(entity.Get<CaptureState>().Owner); }
        }
    }
}
