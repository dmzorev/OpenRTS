using System.ComponentModel.DataAnnotations;
using OpenRTS.Core;

namespace OpenRTS.Content;

public sealed partial class UnitCatalog
{
    private sealed class BuildOptions
    {
        [Range(1, 1000000)] public int Ticks { get; set; } = 100;
        [Range(0, 1000000000)] public int Cost { get; set; }
        public ProductionKind Kind { get; set; }
        [Range(0, WorldPosition.MaximumExtent)] public int PlacementRadius { get; set; } = 102400;
        public BuildDefinition Compile() => new(Ticks, Cost, Kind, PlacementRadius);
    }
    private sealed class QueueOptions { public Dictionary<string, QueueSettings> Queues { get; set; } = new(); }
    private sealed class QueueSettings
    {
        [Range(1, 256)] public int Capacity { get; set; } = 20;
        public PaymentPolicy Payment { get; set; }
        public int[] Exit { get; set; } = [0, 2048];
        [Range(0, 8192)] public int ExitSearchRadius { get; set; } = 2048;
        public string? WorkingState { get; set; }
        public string? ExitAnimation { get; set; }
        [Range(1, 1000000)] public int ExitAnimationTicks { get; set; } = 40;
        public TaskQueueDefinition Compile(string id) => Exit.Length == 2 ? new(id, Capacity, Payment, new(Exit[0], Exit[1]), ExitSearchRadius, WorkingState, ExitAnimation, ExitAnimationTicks) : throw new ArgumentException("Exit requires two world coordinates.");
    }
    private sealed class ActionsOptions
    {
        public Dictionary<string, GameplayActionOptions> Definitions { get; set; } = new();
        public Dictionary<string, LegacyActionOptions> Buttons { get; set; } = new();
    }
    private class GameplayActionOptions
    {
        public string? Mount { get; set; }
        public bool Capture { get; set; }
        public int Range { get; set; } = 1024;
        public bool GroundTarget { get; set; }
        public bool AutoCast { get; set; }
        public string? Unit { get; set; }
        public string? Queue { get; set; }
        [Range(1, 1000000)] public int? Ticks { get; set; }
        [Range(0, 1000000000)] public int? Cost { get; set; }
        [Range(0, WorldPosition.MaximumExtent)] public int? PlacementRadius { get; set; }
    }
    private sealed class LegacyActionOptions : GameplayActionOptions
    {
        public string? Label { get; set; }
        public string? Icon { get; set; }
        public string? Hotkey { get; set; }
        public string Cursor { get; set; } = "ability";
        public int Order { get; set; }
    }
    private sealed class ActionPanelOptions { public Dictionary<string, ButtonOptions> Buttons { get; set; } = new(); }
    private sealed class ButtonOptions
    {
        public string? Label { get; set; }
        public string? Icon { get; set; }
        public string? Hotkey { get; set; }
        public string Cursor { get; set; } = "ability";
        public int Order { get; set; }
    }

    private static (AbilityDefinition[] Abilities, ProductionDefinition[] Products, ActionButtonBinding[] Buttons) CompileActions(
        ActionsOptions? actions, ActionPanelOptions? panel, TaskQueueDefinition[] queues, IReadOnlyDictionary<string, BuildDefinition?> buildDefaults)
    {
        var declarations = new Dictionary<string, GameplayActionOptions>(StringComparer.Ordinal);
        foreach (var (name, action) in actions?.Definitions ?? new()) declarations.Add(name, action);
        foreach (var (name, action) in actions?.Buttons ?? new())
            if (!declarations.TryAdd(name, action)) throw new ArgumentException("Action declared in both Definitions and legacy Buttons: " + name);
        var abilities = new List<AbilityDefinition>(); var products = new List<ProductionDefinition>();
        foreach (var (name, action) in declarations.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(name) || (action.Mount is not null ? 1 : 0) + (action.Unit is not null ? 1 : 0) + (action.Capture ? 1 : 0) != 1) throw new ArgumentException("An action requires exactly one Mount or Unit.");
            if (action.Capture)
            {
                if (action.GroundTarget || action.AutoCast || action.Queue is not null || action.Ticks is not null || action.Cost is not null || action.PlacementRadius is not null) throw new ArgumentException("Capture cannot declare weapon/production settings.");
                abilities.Add(new(name, "", Kind: AbilityKind.Capture, Range: action.Range));
            }
            if (action.Mount is { } mount)
            {
                if (action.Queue is not null || action.Ticks is not null || action.Cost is not null || action.PlacementRadius is not null)
                    throw new ArgumentException("An ability cannot specify production settings: " + name);
                abilities.Add(new(name, mount, action.GroundTarget, action.AutoCast));
            }
            if (action.Unit is { } target)
            {
                if (action.GroundTarget || action.AutoCast) throw new ArgumentException("Production cannot specify weapon ability settings: " + name);
                if (!buildDefaults.TryGetValue(target, out var defaults)) throw new ArgumentException("Unknown product: " + target);
                if (defaults is null) throw new ArgumentException("Product requires Buildable: " + target);
                var settings = new BuildDefinition(action.Ticks ?? defaults.Ticks, action.Cost ?? defaults.Cost, defaults.Kind, action.PlacementRadius ?? defaults.PlacementRadius);
                string queue = action.Queue ?? queues.FirstOrDefault()?.Id ?? throw new ArgumentException("A production action requires a task queue.");
                products.Add(new(name, target, queue, settings));
            }
        }
        var bindings = panel?.Buttons ?? (actions?.Buttons ?? new()).ToDictionary(p => p.Key,
            p => new ButtonOptions { Label = p.Value.Label, Icon = p.Value.Icon, Hotkey = p.Value.Hotkey, Cursor = p.Value.Cursor, Order = p.Value.Order }, StringComparer.Ordinal);
        var buttons = new List<ActionButtonBinding>();
        foreach (var (name, button) in bindings.OrderBy(p => p.Value.Order).ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!declarations.TryGetValue(name, out var action)) throw new ArgumentException("Panel references unknown action: " + name);
            string key = button.Hotkey ?? (buttons.Count < DefaultHotkeys.Length ? DefaultHotkeys[buttons.Count] : "");
            if (key.Length > 32 || buttons.Any(b => key.Length > 0 && string.Equals(b.Hotkey, key, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Action hotkeys must be unique within a type.");
            buttons.Add(new(name, button.Label ?? action.Unit ?? name, button.Icon, key, button.Cursor));
        }
        return (abilities.ToArray(), products.ToArray(), buttons.ToArray());
    }
    private static readonly string[] DefaultHotkeys = ["Q", "W", "E", "R", "T", "Y"];
}
