using Frent;
using OpenRTS.Core.Simulation;

namespace OpenRTS.Core.Economy;

internal sealed class EconomySystem
{
    internal const long MaximumCredits = 1000000000000000;

    private sealed class Account(PlayerDefinition definition)
    {
        internal readonly uint Id = definition.Id;
        internal readonly string Name = definition.Name;
        internal long Credits = definition.Credits, Produced, Consumed;
    }

    private struct IncomeState(long nextTick)
    {
        internal long NextTick = nextTick;
    }

    private readonly SortedDictionary<uint, Account> _players = new();
    private readonly object _storageGate;
    private readonly Predicate<Entity> _isConstructing;
    private bool _powerDirty = true;

    internal EconomySystem(IEnumerable<PlayerDefinition>? players, object storageGate, Predicate<Entity> isConstructing)
    {
        _storageGate = storageGate;
        _isConstructing = isConstructing;
        _players.Add(0, new(new(0, "Нейтральный")));
        foreach (var player in players ?? [])
        {
            if (player.Id == 0 || string.IsNullOrWhiteSpace(player.Name) || player.Credits < 0 ||
                player.Credits > MaximumCredits || !_players.TryAdd(player.Id, new(player)))
                throw new ArgumentException("Players require unique nonzero IDs, names and bounded credits.");
        }
    }

    internal int PlayerCount => _players.Count;
    internal void EnsurePlayer(uint owner) => Player(owner);
    internal bool CanAfford(uint owner, long amount) => Player(owner).Credits >= amount;

    internal bool Spend(uint owner, long amount)
    {
        if (amount < 0) throw new InvalidOperationException("Negative payment.");
        var player = Player(owner);
        if (owner == 0 || player.Credits < amount) return false;
        player.Credits -= amount;
        return true;
    }

    internal void Credit(uint owner, long amount)
    {
        if (owner == 0) return;
        var player = Player(owner);
        player.Credits += Math.Min(MaximumCredits - player.Credits, amount);
    }

    private Account Player(uint owner)
    {
        if (!_players.TryGetValue(owner, out var player))
            _players.Add(owner, player = new(new(owner, $"Игрок {owner}")));
        return player;
    }

    internal void Initialize(Entity entity, EconomyDefinition? settings, uint owner, long tick)
    {
        EnsurePlayer(owner);
        if (!_isConstructing(entity)) ActivateIncome(entity, settings, tick);
    }

    // The caller holds the storage gate when adding a component.
    internal static void ActivateIncome(Entity entity, EconomyDefinition? settings, long tick)
    {
        if (settings is { Income: > 0 }) entity.Add(new IncomeState(checked(tick + settings.Interval)));
    }

    internal static void ResetIncome(Entity entity, EconomyDefinition? settings, long tick)
    {
        if (entity.Has<IncomeState>()) entity.Get<IncomeState>().NextTick = checked(tick + settings!.Interval);
    }

    internal void AdvanceIncome(World? storage, long tick)
    {
        if (storage is null) return;
        lock (_storageGate)
        {
            foreach (var (income, owner, definition) in storage.Query<IncomeState, OwnershipState, EntityDefinition>())
            {
                if (tick < income.Value.NextTick) continue;
                var settings = definition.Value.Economy!;
                income.Value.NextTick = checked(tick + settings.Interval);
                Credit(owner.Value.Owner, settings.Income);
            }
        }
    }

    internal void InvalidatePower() => _powerDirty = true;

    internal GameplayConditions ReadConditions(EconomyDefinition? source, uint owner, World? storage)
    {
        if (source is null || owner == 0) return GameplayConditions.None;
        UpdatePower(storage);
        var player = _players[owner];
        return player.Produced < player.Consumed ? GameplayConditions.LowPower : GameplayConditions.None;
    }

    private void UpdatePower(World? storage)
    {
        if (!_powerDirty) return;
        foreach (var player in _players.Values) player.Produced = player.Consumed = 0;
        if (storage is not null)
        {
            lock (_storageGate)
            {
                foreach (var (entity, definition, ownership) in storage.Query<EntityDefinition, OwnershipState>().WithEntities())
                {
                    uint owner = ownership.Value.Owner;
                    if (owner == 0 || definition.Value.Economy is not { } settings || _isConstructing(entity)) continue;
                    var player = Player(owner);
                    if (settings.Energy >= 0) player.Produced += settings.Energy;
                    else player.Consumed -= settings.Energy;
                }
            }
        }
        _powerDirty = false;
    }

    internal void CopyPlayers(Span<PlayerPose> destination, World? storage)
    {
        UpdatePower(storage);
        if (destination.Length < _players.Count) throw new ArgumentException("Destination is too small.");
        int i = 0;
        foreach (var player in _players.Values)
            destination[i++] = new(player.Id, player.Name, player.Credits, player.Produced, player.Consumed);
    }

    internal void WritePlayers(BinaryWriter writer, World? storage)
    {
        UpdatePower(storage);
        writer.Write(_players.Count);
        foreach (var player in _players.Values)
        {
            writer.Write(player.Id);
            writer.Write(player.Name);
            writer.Write(player.Credits);
            writer.Write(player.Produced);
            writer.Write(player.Consumed);
        }
    }

    internal static void WriteIncome(BinaryWriter writer, Entity entity)
    {
        bool active = entity.Has<IncomeState>();
        writer.Write(active);
        if (active) writer.Write(entity.Get<IncomeState>().NextTick);
    }
}
