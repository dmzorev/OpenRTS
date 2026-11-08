using System.Collections.ObjectModel;
using DeclarativeContent;
using OpenRTS.Core.Combat;

namespace OpenRTS.Content;

/// <summary>Shared named weapon definitions; adapter visuals are loaded by EffectCatalog separately.</summary>
public sealed class WeaponCatalog
{
    public ReadOnlyDictionary<string, WeaponDefinition> Weapons { get; }
    private WeaponCatalog(Dictionary<string, WeaponDefinition> weapons) => Weapons = new(weapons);
    public static WeaponCatalog Load(IEnumerable<ContentSource> sources)
    {
        var definitions = DefinitionSet.Load(sources);
        var registry = new TraitRegistry<string>().Register<WeaponOptions>("Weapon", (_, _, options) => options, required: true);
        registry.ValidateTemplates(definitions);
        var weapons = new Dictionary<string, WeaponDefinition>(StringComparer.Ordinal);
        foreach (var (id, node) in definitions.Concrete.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var o = registry.Compile(id, node).Create(id).Single<WeaponOptions>();
            try { weapons.Add(id, new(o.Range, o.AcquireRange, o.Damage, o.ReloadTicks, o.WindupTicks, id, o.Projectile, o.Effect,
                o.Speed, o.TravelLimit, o.SplashRadius, o.Burst, o.BurstIntervalTicks, o.AnimationTicks,
                o.Versus.Living, o.Versus.Mechanical, o.Versus.Building, o.FriendlyFire, o.PassengerDamage, o.PassengerDamageSpill, new TargetCategories(o.Targets), o.SpatialTrajectory,
                o.ExcludedTargets.Length == 0 ? null : new TargetCategories(o.ExcludedTargets))); }
            catch (ArgumentException error) { throw node.Error(id + ".Weapon", error.Message); }
        }
        return new(weapons);
    }
    private sealed class WeaponOptions
    {
        public string[] Targets { get; set; } = ["ground"];
        public string[] ExcludedTargets { get; set; } = [];
        public bool SpatialTrajectory { get; set; }
        public int Range { get; set; } = 4096;
        public int AcquireRange { get; set; } = 8192;
        public int Damage { get; set; } = 10;
        public int ReloadTicks { get; set; } = 20;
        public int WindupTicks { get; set; } = 2;
        public int Burst { get; set; } = 1;
        public int BurstIntervalTicks { get; set; } = 2;
        public int AnimationTicks { get; set; } = 4;
        public ProjectileKind Projectile { get; set; }
        public WeaponEffect Effect { get; set; }
        public int Speed { get; set; } = 256;
        public int TravelLimit { get; set; } = 16384;
        public int SplashRadius { get; set; }
        public bool FriendlyFire { get; set; }
        public int PassengerDamage { get; set; }
        public bool PassengerDamageSpill { get; set; }
        public ArmorOptions Versus { get; set; } = new();
    }
    private sealed class ArmorOptions
    {
        public int Living { get; set; } = 100;
        public int Mechanical { get; set; } = 100;
        public int Building { get; set; } = 100;
    }
}
