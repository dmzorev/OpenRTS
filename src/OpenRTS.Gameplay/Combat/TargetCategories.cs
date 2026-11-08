using System.Collections.ObjectModel;

namespace OpenRTS.Core.Combat;

/// <summary>Game-defined, ordinal names. No enum or fixed bit budget limits new target categories.</summary>
public sealed class TargetCategories
{
    public static TargetCategories Ground { get; } = new(["ground"]);
    public ReadOnlyCollection<string> Names { get; }
    public TargetCategories(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var values = names.ToArray();
        if (values.Length == 0 || values.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 128) ||
            values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new ArgumentException("Target categories require unique nonempty names.", nameof(names));
        Array.Sort(values, StringComparer.Ordinal);
        Names = Array.AsReadOnly(values);
    }
    public bool Intersects(TargetCategories other)
    {
        int a = 0, b = 0;
        while (a < Names.Count && b < other.Names.Count)
        {
            int comparison = StringComparer.Ordinal.Compare(Names[a], other.Names[b]);
            if (comparison == 0) return true;
            if (comparison < 0) a++; else b++;
        }
        return false;
    }
    public void Write(BinaryWriter writer)
    {
        writer.Write(Names.Count);
        foreach (var name in Names) writer.Write(name);
    }
}
