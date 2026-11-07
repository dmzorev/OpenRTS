namespace OpenRTS.Core;

/// <summary>Built-in RTS session with gameplay command helpers and its typed frame exchange.</summary>
public sealed class SimulationSession : SimulationSession<SimulationWorld, WorldCommand, WorldEvent>
{
    public FrameExchange Frames { get; }

    public SimulationSession(Func<SimulationWorld> createWorld, IEnumerable<WorldCommand>? scriptedCommands = null,
        long? endTick = null, bool realtime = true, int maximumEventBacklog = 65536, double playbackSpeed = 1, bool startPaused = false)
        : this(new FrameExchange(), createWorld, scriptedCommands, endTick, realtime, maximumEventBacklog, playbackSpeed, startPaused) { }

    private SimulationSession(FrameExchange frames, Func<SimulationWorld> createWorld, IEnumerable<WorldCommand>? scriptedCommands,
        long? endTick, bool realtime, int maximumEventBacklog, double playbackSpeed, bool startPaused)
        : base(createWorld, scriptedCommands, endTick, realtime, maximumEventBacklog, playbackSpeed, startPaused, frames.TryPublish)
    {
        Frames = frames;
    }

    public long Submit(CommandKind kind, WorldPosition position = default, EntityId entity = default, int unitType = 0,
        uint owner = 0, int animation = 0, int durationTicks = 1, AnimationTrack track = AnimationTrack.Weapon,
        uint states = 0, EntityId targetEntity = default, EntitySet? recipients = null, bool queued = false, string? action = null, string? queue = null, int amount = 1, bool repeat = false, bool enabled = false, long taskId = 0)
        => base.Submit(new(0, 0, 0, kind, entity, position, unitType, owner, animation, durationTicks, track, states, targetEntity, recipients, queued, action, queue, amount, repeat, enabled, taskId));

    protected override void ValidateInput(WorldCommand command)
    {
        if (!Enum.IsDefined(command.Kind)) throw new ArgumentOutOfRangeException("kind");
    }

    public long Issue(UnitAction action)
    {
        ArgumentNullException.ThrowIfNull(action.Recipients);
        CommandKind kind = action.Kind switch
        {
            UnitActionKind.Move => action.Recipients.Count == 1 ? CommandKind.Move : CommandKind.MoveSelected,
            UnitActionKind.AttackMove => CommandKind.AttackMove,
            UnitActionKind.Attack => CommandKind.AttackTarget,
            UnitActionKind.Repair => CommandKind.RepairTarget,
            UnitActionKind.Enter => CommandKind.Enter,
            UnitActionKind.Unload => CommandKind.Unload,
            UnitActionKind.UnloadAt => CommandKind.UnloadAt,
            UnitActionKind.Build => CommandKind.BuildSite,
            UnitActionKind.Capture => CommandKind.CaptureTarget,
            UnitActionKind.Ability => CommandKind.UseAbility,
            UnitActionKind.Stop => CommandKind.Stop,
            UnitActionKind.HoldPosition => CommandKind.HoldPosition,
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        if (kind == CommandKind.UseAbility && string.IsNullOrWhiteSpace(action.Action)) throw new ArgumentException("An ability action requires its registered ID.", nameof(action));
        return Submit(kind, action.Position, action.Recipients.Ids[0], targetEntity: action.Target, recipients: action.Recipients, queued: action.Queued, action: action.Action);
    }
}
