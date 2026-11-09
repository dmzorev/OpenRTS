using OpenRTS.Core.Navigation;

namespace OpenRTS.Core.Visibility;

internal sealed class SensorState
{
    internal readonly record struct Concealment(int Channel, int Counter, byte? Terrain);
    internal sealed record Detector(int Channel, int Signature, int Range, SensorDisplay Display,
        LiveObservationFields Fields, int[] Counters);
    internal sealed record Recipe(int[] Signatures, Concealment[] Concealments, Detector[] Detectors);
    internal readonly record struct Grant(long Token, long ExpiresAt, int Signature, bool Suppress, Concealment? Concealment);
    internal readonly record struct Observation(SensorDisplay Display, LiveObservationFields Fields);
    internal readonly record struct Key(int Player, EntityId Entity);

    internal readonly Dictionary<string, int> Names;
    internal readonly Recipe[] Recipes;
    internal readonly int Optical, Visual;
    internal readonly IReadOnlyList<uint> Players;
    internal readonly Dictionary<EntityId, List<Grant>> Grants = [];
    internal readonly MotionIndex Index = new(0);
    internal readonly List<EntityId> Candidates = [], Targets = [];
    internal Dictionary<Key, Observation> Current = [], Pending = [];
    internal readonly Dictionary<Key, ContactKnowledge> Knowledge = [];
    internal readonly List<Key> Keys = [];
    internal readonly long[] NextContact;
    internal long NextGrant = 1;

    internal SensorState(UnitDefinition[] types, uint[] players)
    {
        if (players.Length is < 1 or > 16) throw new ArgumentException("Sensors require 1–16 explicit players.");
        Players = Array.AsReadOnly(players);
        NextContact = new long[players.Length];
        Array.Fill(NextContact, 1);
        var names = new SortedSet<string>(StringComparer.Ordinal) { SensorDefinition.Visual, SensorDefinition.Optical };
        foreach (var unit in types)
        {
            if (unit.Sensors is not { } sensors) continue;
            names.UnionWith(sensors.Signatures);
            foreach (var c in sensors.Concealments) { names.Add(c.Channel); names.Add(c.Counter); }
            foreach (var d in sensors.Detectors)
            {
                names.Add(d.Channel);
                names.Add(d.Signature);
                names.UnionWith(d.Counters);
            }
        }
        Names = names.Select((name, id) => (name, id)).ToDictionary(p => p.name, p => p.id, StringComparer.Ordinal);
        Optical = Names[SensorDefinition.Optical];
        Visual = Names[SensorDefinition.Visual];
        Recipes = types.Select(type => type.Sensors is not { } s
            ? new Recipe([Visual], [], [])
            : new Recipe(s.Signatures.Select(NameId).ToArray(), s.Concealments.Select(Compile).ToArray(),
                s.Detectors.Select(d => new Detector(NameId(d.Channel), NameId(d.Signature), d.Range, d.Display,
                    d.Fields, d.Counters.Select(NameId).ToArray())).ToArray())).ToArray();
    }

    internal int PlayerSlot(uint player)
    {
        for (int i = 0; i < Players.Count; i++)
            if (Players[i] == player) return i;
        return -1;
    }

    internal int NameId(string name) => Names.TryGetValue(name, out int id) ? id
        : throw new ArgumentException($"Unknown sensor name '{name}'.", nameof(name));
    internal Concealment Compile(ConcealmentDefinition c) => new(NameId(c.Channel), NameId(c.Counter), c.Terrain);

    internal void Expire(long tick)
    {
        foreach (var (entity, grants) in Grants)
        {
            grants.RemoveAll(g => g.ExpiresAt <= tick);
            if (grants.Count == 0) Grants.Remove(entity);
        }
    }

    internal void WriteState(BinaryWriter writer)
    {
        writer.Write(30);
        writer.Write(NextGrant);
        writer.Write(Grants.Count);
        foreach (var (entity, grants) in Grants.OrderBy(p => p.Key.Value))
        {
            writer.Write(entity.Value);
            writer.Write(grants.Count);
            foreach (var g in grants)
            {
                writer.Write(g.Token);
                writer.Write(g.ExpiresAt);
                writer.Write(g.Signature);
                writer.Write(g.Suppress);
                writer.Write(g.Concealment.HasValue);
                if (g.Concealment is not { } c) continue;
                writer.Write(c.Channel);
                writer.Write(c.Counter);
                writer.Write(c.Terrain.HasValue);
                if (c.Terrain is byte terrain) writer.Write(terrain);
            }
        }
        writer.Write(Players.Count);
        foreach (long next in NextContact) writer.Write(next);
        writer.Write(Knowledge.Count);
        foreach (var (key, value) in Knowledge.OrderBy(p => p.Key.Player).ThenBy(p => p.Key.Entity.Value))
        {
            writer.Write(key.Player);
            writer.Write(key.Entity.Value);
            writer.Write(value.Id);
            writer.Write(value.UnitType ?? -1);
            writer.Write(value.Position.X);
            writer.Write(value.Position.Y);
            writer.Write(value.Position.Elevation);
            writer.Write(value.ObservedTick);
            writer.Write(value.IdentifiedTick);
        }
    }
}
