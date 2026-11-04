using System.Collections.ObjectModel;

namespace OpenRTS.Core.Navigation;

/// <summary>Speed uses 1024 as unity. Cost is an independent positive price per unit distance.</summary>
public readonly record struct TerrainRule(bool Traverse, bool CanStop, int Speed, int Cost)
{
    public static TerrainRule Ground => new(true, true, 1024, 1024);
    public static TerrainRule Blocked => new(false, false, 0, 0);
}

public sealed class Locomotor
{
    public static Locomotor Default { get; } = new("default", [TerrainRule.Ground]);
    public string Id { get; }
    public ReadOnlyCollection<TerrainRule> Rules { get; }
    public int MinimumCost { get; }
    public BodyShape Body { get; }
    public int MaximumSlope { get; }
    public int MaximumStep { get; }
    public int MaximumDepth { get; }
    internal string NavigationKey => $"{Id}:{(int)Body.Kind}:{Body.HalfWidth}:{Body.HalfHeight}";
    public Locomotor(string id, IEnumerable<TerrainRule> rules, BodyShape? body = null, int maximumSlope = int.MaxValue, int maximumStep = int.MaxValue, int maximumDepth = int.MaxValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var values = rules.ToArray();
        if (values.Length is < 1 or > 256) throw new ArgumentException("A locomotor needs 1..256 terrain rules.", nameof(rules));
        foreach (var value in values)
            if (value.Traverse ? value.Speed is < 1 or > 1024 || value.Cost is < 1 or > 65536 :
                value.CanStop || value.Speed != 0 || value.Cost != 0)
                throw new ArgumentException("Traversable terrain needs speed 1..1024 and cost 1..65536; blocked terrain has no stop/speed/cost.", nameof(rules));
        if (!values.Any(r => r.Traverse)) throw new ArgumentException("A locomotor needs traversable ground.", nameof(rules));
        MinimumCost = values.Where(r => r.Traverse).Min(r => r.Cost);
        if (maximumSlope < 0 || maximumStep < 0 || maximumDepth < 0) throw new ArgumentOutOfRangeException(nameof(maximumSlope));
        Body = body ?? BodyShape.Default; MaximumSlope = maximumSlope; MaximumStep = maximumStep; MaximumDepth = maximumDepth;
        Id = id; Rules = Array.AsReadOnly(values);
    }
    internal Locomotor WithBody(BodyShape body) => body == Body ? this : new(Id, Rules, body, MaximumSlope, MaximumStep, MaximumDepth);
    internal TerrainRule At(NavigationGrid grid, int cell)
    {
        var sample = grid.SurfaceAt(cell); var rule = Rules[grid.TerrainAt(cell)];
        if (sample.Slope > MaximumSlope || sample.Depth > MaximumDepth) return TerrainRule.Blocked;
        return !grid.CanStop(cell) && rule.CanStop ? rule with { CanStop = false } : rule;
    }
    internal void WriteState(BinaryWriter writer)
    {
        writer.Write(Id); Body.WriteState(writer); writer.Write(MaximumSlope); writer.Write(MaximumStep); writer.Write(MaximumDepth); writer.Write(Rules.Count);
        foreach (var rule in Rules)
        { writer.Write(rule.Traverse); writer.Write(rule.CanStop); writer.Write(rule.Speed); writer.Write(rule.Cost); }
    }
}
