namespace OpenRTS.Core;

public sealed class UpgradeDefinition
{
    public string Id { get; }
    public int Cost { get; }
    public int Ticks { get; }
    public bool Support { get; }
    public bool Default { get; }
    public bool ClaimNeutral { get; }
    public bool ReplaceWeapons { get; }
    public IReadOnlyList<string> Mounts { get; }
    public IReadOnlyList<string> Products { get; }
    public int? Seats { get; }
    public bool? PassengerFire { get; }

    public UpgradeDefinition(string id, int cost, int ticks, bool support = false, bool @default = false,
        bool claimNeutral = false, bool replaceWeapons = false, IEnumerable<string>? mounts = null,
        IEnumerable<string>? products = null, int? seats = null, bool? passengerFire = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id.Length > 128 || cost is < 0 or > 1000000000 || ticks is < 1 or > 1000000 ||
            seats is < 1 or > 256 || claimNeutral && !support)
            throw new ArgumentException("Invalid upgrade settings.");
        Id = id;
        Cost = cost;
        Ticks = ticks;
        Support = support;
        Default = @default;
        ClaimNeutral = claimNeutral;
        ReplaceWeapons = replaceWeapons;
        Mounts = Array.AsReadOnly((mounts ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        Products = Array.AsReadOnly((products ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        Seats = seats;
        PassengerFire = passengerFire;
    }

    internal void Write(BinaryWriter writer)
    {
        writer.Write(Id); writer.Write(Cost); writer.Write(Ticks); writer.Write(Support); writer.Write(Default);
        writer.Write(ClaimNeutral); writer.Write(ReplaceWeapons);
        writer.Write(Mounts.Count); foreach (string mount in Mounts) writer.Write(mount);
        writer.Write(Products.Count); foreach (string product in Products) writer.Write(product);
        writer.Write(Seats ?? 0); writer.Write(PassengerFire.HasValue);
        if (PassengerFire is { } fire) writer.Write(fire);
    }
}

public readonly record struct UpgradePose(string? Completed, string? Project, uint Payer, int Work, int Required)
{
    public bool Active => Work > 0 && Project is not null;
}
