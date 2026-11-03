using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenRTS.Core;

/// <summary>Dedicated world owner. Wall-clock scheduling never enters authoritative state.</summary>
public sealed class SimulationSession : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private readonly ConcurrentQueue<WorldCommand> _input = new();
    private readonly ConcurrentQueue<WorldEvent> _events = new();
    private readonly List<WorldCommand> _journal = [];
    private readonly object _journalGate = new();
    private readonly TaskCompletionSource<SimulationReport> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly long? _endTick;
    private readonly int _maximumEventBacklog;
    private int _pendingInput, _eventCount;
    private long _inputSequence;
    private bool _disposed;

    public FrameExchange Frames { get; } = new();
    public Task<SimulationReport> Completion => _completion.Task;

    public SimulationSession(Func<SimulationWorld> createWorld, IEnumerable<WorldCommand>? scriptedCommands = null,
        long? endTick = null, bool realtime = true, int maximumEventBacklog = 65536)
    {
        ArgumentNullException.ThrowIfNull(createWorld);
        if (endTick is <= 0) throw new ArgumentOutOfRangeException(nameof(endTick));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEventBacklog);
        var script = (scriptedCommands ?? []).OrderBy(c => c.Tick).ToArray();
        if (script.Any(c => c.Tick <= 0 || c.Issuer != 0 || endTick.HasValue && c.Tick > endTick.Value))
            throw new ArgumentException("Scripted commands use issuer zero and must fit the run's tick range.", nameof(scriptedCommands));
        _endTick = endTick;
        _maximumEventBacklog = maximumEventBacklog;
        _thread = new Thread(() => Run(createWorld, script, realtime)) { IsBackground = true, Name = "OpenRTS simulation" };
        _thread.Start();
    }

    /// <summary>Enqueues live input. Its authoritative tick is assigned by the worker and recorded in the journal.</summary>
    public long Submit(CommandKind kind, WorldPosition position = default, EntityId entity = default, int unitType = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_endTick.HasValue || Completion.IsCompleted) throw new InvalidOperationException("This session no longer accepts live input.");
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (Interlocked.Increment(ref _pendingInput) > 1024)
        {
            Interlocked.Decrement(ref _pendingInput);
            throw new InvalidOperationException("Input backlog exceeded.");
        }
        long sequence = Interlocked.Increment(ref _inputSequence);
        _input.Enqueue(new(0, 1, sequence, kind, entity, position, unitType));
        return sequence;
    }

    public bool TryReadEvent(out WorldEvent value)
    {
        if (!_events.TryDequeue(out value)) return false;
        Interlocked.Decrement(ref _eventCount);
        return true;
    }

    public WorldCommand[] GetCommandLog()
    {
        lock (_journalGate) return _journal.ToArray();
    }

    private void Run(Func<SimulationWorld> createWorld, WorldCommand[] script, bool realtime)
    {
        try
        {
            var world = createWorld() ?? throw new InvalidOperationException("World factory returned null.");
            if (world.Tick != 0) throw new InvalidOperationException("A session must start from an initialized tick-zero world.");
            Frames.TryPublish(world);
            long origin = Stopwatch.GetTimestamp();
            double period = Stopwatch.Frequency / (double)world.TickRate;
            int cursor = 0;
            var batch = new List<WorldCommand>();
            while (!_stop.IsCancellationRequested && (!_endTick.HasValue || world.Tick < _endTick.Value))
            {
                double remaining = origin + (world.Tick + 1) * period - Stopwatch.GetTimestamp();
                if (realtime && remaining > 0)
                {
                    _stop.Token.WaitHandle.WaitOne((int)Math.Min(int.MaxValue, Math.Ceiling(remaining * 1000 / Stopwatch.Frequency)));
                    continue;
                }
                batch.Clear();
                while (cursor < script.Length && script[cursor].Tick == world.Tick + 1) batch.Add(script[cursor++]);
                int pending = Volatile.Read(ref _pendingInput);
                for (int i = 0; i < pending && _input.TryDequeue(out var command); i++)
                {
                    Interlocked.Decrement(ref _pendingInput);
                    batch.Add(command with { Tick = world.Tick + 1 });
                }
                world.Step(CollectionsMarshal.AsSpan(batch), _stop.Token);
                if (world.AppliedCommands.Length > 0)
                    lock (_journalGate)
                        foreach (var command in world.AppliedCommands) _journal.Add(command);
                foreach (var value in world.Events)
                {
                    if (Interlocked.Increment(ref _eventCount) > _maximumEventBacklog)
                    {
                        Interlocked.Decrement(ref _eventCount);
                        throw new InvalidOperationException("Event backlog exceeded; simulation stopped instead of losing events.");
                    }
                    _events.Enqueue(value);
                }
                Frames.TryPublish(world);
            }
            // A finite verification run must publish its final tick even if readers pin all slots temporarily.
            while (!Frames.TryPublish(world) && !_stop.IsCancellationRequested) _stop.Token.WaitHandle.WaitOne(1);
            _completion.TrySetResult(world.Report());
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { _completion.TrySetCanceled(); }
        catch (Exception error) { _completion.TrySetException(error); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _stop.Cancel();
        if (!_thread.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("Simulation worker did not stop within five seconds.");
        _stop.Dispose();
        _disposed = true;
    }
}
