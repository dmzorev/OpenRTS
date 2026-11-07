using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace OpenRTS.Core;

public interface ISessionCommand<TCommand> : ISimulationCommand
{
    TCommand WithStamp(long tick, uint issuer, long sequence);
}

/// <summary>Dedicated world owner. Wall-clock scheduling never enters authoritative state.</summary>
public class SimulationSession<TWorld, TCommand, TEvent> : IDisposable
    where TWorld : Simulation<TCommand, TEvent>
    where TCommand : struct, ISessionCommand<TCommand>
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private readonly Func<TWorld, bool>? _tryPublish;
    private readonly ConcurrentQueue<TCommand> _input = new();
    private readonly ConcurrentQueue<TEvent> _events = new();
    private readonly List<TCommand> _journal = [];
    private readonly object _journalGate = new();
    private readonly TaskCompletionSource<SimulationReport> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly long? _endTick;
    private readonly int _maximumEventBacklog;
    private int _pendingInput, _eventCount;
    private long _inputSequence;
    private bool _disposed;
    private readonly object _clockGate = new();
    private readonly bool _realtime;
    private double _playbackSpeed, _accumulated, _visualElapsed, _period;
    private bool _paused;
    private int _steps;
    private long _clockTimestamp = Stopwatch.GetTimestamp(), _displayTick;

    public double PlaybackSpeed { get { lock (_clockGate) return _playbackSpeed; } }
    public bool IsPaused { get { lock (_clockGate) return _paused; } }

    public void SetPlayback(double speed, bool paused)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!double.IsFinite(speed) || speed < 0.125 || speed > 8) throw new ArgumentOutOfRangeException(nameof(speed));
        if (!_realtime) throw new InvalidOperationException("Unpaced sessions have no playback controls.");
        lock (_clockGate)
        {
            UpdateClock();
            if (_paused || paused) _visualElapsed = _period;
            _playbackSpeed = speed; _paused = paused; _steps = 0;
            Monitor.PulseAll(_clockGate);
        }
    }

    public void StepOnce()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_clockGate)
        {
            if (!_realtime || !_paused) throw new InvalidOperationException("Single stepping requires a paused realtime session.");
            if (_steps >= 1024) throw new InvalidOperationException("Step backlog exceeded.");
            _steps++;
            Monitor.PulseAll(_clockGate);
        }
    }

    public float GetInterpolationAlpha(long frameTick)
    {
        lock (_clockGate)
        {
            UpdateClock();
            return !_realtime || _paused || frameTick != _displayTick || _period == 0
                ? 1 : (float)Math.Clamp(_visualElapsed / _period, 0, 1);
        }
    }

    private void UpdateClock()
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = (now - _clockTimestamp) / (double)Stopwatch.Frequency;
        _clockTimestamp = now;
        if (!_paused) { _accumulated += elapsed * _playbackSpeed; _visualElapsed += elapsed * _playbackSpeed; }
    }

    private bool AwaitTick()
    {
        lock (_clockGate)
        {
            while (!_stop.IsCancellationRequested)
            {
                UpdateClock();
                if (!_realtime) return true;
                if (_paused)
                {
                    if (_steps > 0) { _steps--; return true; }
                    Monitor.Wait(_clockGate);
                }
                else if (_accumulated >= _period) { _accumulated -= _period; return true; }
                else Monitor.Wait(_clockGate, (int)Math.Clamp(Math.Ceiling((_period - _accumulated) / _playbackSpeed * 1000), 1, int.MaxValue));
            }
            return false;
        }
    }

    private void Publish(TWorld world)
    {
        lock (_clockGate)
        {
            UpdateClock();
            if (_tryPublish?.Invoke(world) == true) { _displayTick = world.Tick; _visualElapsed = 0; }
        }
    }

    public Task<SimulationReport> Completion => _completion.Task;

    public SimulationSession(Func<TWorld> createWorld, IEnumerable<TCommand>? scriptedCommands = null,
        long? endTick = null, bool realtime = true, int maximumEventBacklog = 65536, double playbackSpeed = 1, bool startPaused = false,
        Func<TWorld, bool>? tryPublish = null)
    {
        ArgumentNullException.ThrowIfNull(createWorld);
        if (endTick is <= 0) throw new ArgumentOutOfRangeException(nameof(endTick));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEventBacklog);
        var script = (scriptedCommands ?? []).OrderBy(c => c.Tick).ToArray();
        if (script.Any(c => c.Tick <= 0 || c.Issuer != 0 || endTick.HasValue && c.Tick > endTick.Value))
            throw new ArgumentException("Scripted commands use issuer zero and must fit the run's tick range.", nameof(scriptedCommands));
        if (!double.IsFinite(playbackSpeed) || playbackSpeed < 0.125 || playbackSpeed > 8) throw new ArgumentOutOfRangeException(nameof(playbackSpeed));
        if (!realtime && (startPaused || playbackSpeed != 1)) throw new ArgumentException("Playback controls require a realtime session.");
        _realtime = realtime; _playbackSpeed = playbackSpeed; _paused = startPaused;
        _endTick = endTick;
        _tryPublish = tryPublish;
        _maximumEventBacklog = maximumEventBacklog;
        _thread = new Thread(() => Run(createWorld, script, realtime)) { IsBackground = true, Name = "OpenRTS simulation" };
        _thread.Start();
    }

    /// <summary>Enqueues live input. The session assigns issuer, sequence and authoritative tick.</summary>
    public long Submit(TCommand command)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_endTick.HasValue || Completion.IsCompleted) throw new InvalidOperationException("This session no longer accepts live input.");
        ValidateInput(command);
        if (Interlocked.Increment(ref _pendingInput) > 1024)
        {
            Interlocked.Decrement(ref _pendingInput);
            throw new InvalidOperationException("Input backlog exceeded.");
        }
        long sequence = Interlocked.Increment(ref _inputSequence);
        try { _input.Enqueue(command.WithStamp(0, 1, sequence)); }
        catch { Interlocked.Decrement(ref _pendingInput); throw; }
        return sequence;
    }

    protected virtual void ValidateInput(TCommand command) { }

    public bool TryReadEvent([MaybeNullWhen(false)] out TEvent value)
    {
        if (!_events.TryDequeue(out value)) return false;
        Interlocked.Decrement(ref _eventCount);
        return true;
    }

    public TCommand[] GetCommandLog()
    {
        lock (_journalGate) return _journal.ToArray();
    }

    private void Run(Func<TWorld> createWorld, TCommand[] script, bool realtime)
    {
        try
        {
            SimulationReport report;
            using (var world = createWorld() ?? throw new InvalidOperationException("World factory returned null."))
            {
                if (world.Tick != 0) throw new InvalidOperationException("A session must start from an initialized tick-zero world.");
                lock (_clockGate) { _period = 1.0 / world.TickRate; _clockTimestamp = Stopwatch.GetTimestamp(); _accumulated = 0; }
                Publish(world);
                int cursor = 0;
                var batch = new List<TCommand>();
                while (!_stop.IsCancellationRequested && (!_endTick.HasValue || world.Tick < _endTick.Value))
                {
                    if (!AwaitTick()) break;
                    batch.Clear();
                    while (cursor < script.Length && script[cursor].Tick == world.Tick + 1) batch.Add(script[cursor++]);
                    int pending = Volatile.Read(ref _pendingInput);
                    for (int i = 0; i < pending && _input.TryDequeue(out var command); i++)
                    {
                        Interlocked.Decrement(ref _pendingInput);
                        batch.Add(command.WithStamp(world.Tick + 1, command.Issuer, command.Sequence));
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
                    Publish(world);
                }
                // A finite verification run must publish its final tick even if readers pin all slots temporarily.
                while (_tryPublish?.Invoke(world) == false && !_stop.IsCancellationRequested) _stop.Token.WaitHandle.WaitOne(1);
                report = world.Report();
            }
            _completion.TrySetResult(report);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { _completion.TrySetCanceled(); }
        catch (Exception error) { _completion.TrySetException(error); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _stop.Cancel();
        lock (_clockGate) Monitor.PulseAll(_clockGate);
        if (!_thread.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("Simulation worker did not stop within five seconds.");
        _stop.Dispose();
        _disposed = true;
    }
}
