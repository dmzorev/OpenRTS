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
    private sealed class ActionsOptions { public Dictionary<string, ActionOptions> Buttons { get; set; } = new(); }
    private sealed class ActionOptions
    {
        public string? Mount { get; set; }
        public bool GroundTarget { get; set; }
        public bool AutoCast { get; set; }
        public string? Unit { get; set; }
        public string? Queue { get; set; }
        [Range(1, 1000000)] public int? Ticks { get; set; }
        [Range(0, 1000000000)] public int? Cost { get; set; }
        [Range(0, WorldPosition.MaximumExtent)] public int? PlacementRadius { get; set; }
        public string? Label { get; set; }
        public string? Icon { get; set; }
        public string? Hotkey { get; set; }
        public string Cursor { get; set; } = "ability";
        public int Order { get; set; }
    }
}
