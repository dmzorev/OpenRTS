namespace OpenRTS.Core;

public interface ISimulationCommand
{
    long Tick { get; }
    uint Issuer { get; }
    long Sequence { get; }
}

/// <summary>Single-owner buffer that validates and orders a tick's commands before publishing them.</summary>
public sealed class CommandBatch<TCommand> where TCommand : struct, ISimulationCommand
{
    private TCommand[] _applied = [], _pending = [];
    private int _count;

    /// <summary>Commands from the last successful replacement, valid until the next successful replacement.</summary>
    public ReadOnlySpan<TCommand> Applied => _applied.AsSpan(0, _count);

    /// <summary>Validates a candidate batch. The payload validator must not mutate this buffer.</summary>
    public void Replace(ReadOnlySpan<TCommand> commands, long tick, Action<TCommand> validatePayload)
    {
        ArgumentNullException.ThrowIfNull(validatePayload);
        if (commands.IsEmpty)
        {
            Array.Clear(_applied, 0, _count);
            _count = 0;
            return;
        }

        if (_pending.Length < commands.Length) Array.Resize(ref _pending, commands.Length);
        var pending = _pending.AsSpan(0, commands.Length);
        commands.CopyTo(pending);
        try
        {
            Array.Sort(_pending, 0, pending.Length, CommandOrder.Instance);
            for (int i = 0; i < pending.Length; i++)
            {
                var command = pending[i];
                if (command.Tick != tick || command.Sequence <= 0)
                    throw new ArgumentException("Commands must belong to the next tick and have a positive sequence.", nameof(commands));
                if (i > 0 && CommandOrder.Instance.Compare(pending[i - 1], command) == 0)
                    throw new ArgumentException("Duplicate issuer/sequence in command batch.", nameof(commands));
                validatePayload(command);
            }
        }
        catch
        {
            pending.Clear();
            throw;
        }

        Array.Clear(_applied, 0, _count);
        (_applied, _pending) = (_pending, _applied);
        _count = pending.Length;
    }

    private sealed class CommandOrder : IComparer<TCommand>
    {
        public static readonly CommandOrder Instance = new();

        public int Compare(TCommand a, TCommand b)
        {
            int issuer = a.Issuer.CompareTo(b.Issuer);
            return issuer != 0 ? issuer : a.Sequence.CompareTo(b.Sequence);
        }
    }
}
