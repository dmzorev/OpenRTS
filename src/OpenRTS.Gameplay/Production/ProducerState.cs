namespace OpenRTS.Core.Production;

internal sealed class ProductionTask(long id, int product, bool repeat)
{
    public long Id = id;
    public int Product = product, Progress, Paid, Remainder;
    public uint Payer;
    public bool Repeat = repeat, Ready, Blocked;
}

// Cold per-producer state; the ECS component is its only owner.
internal sealed class ProducerState(int queues)
{
    public readonly List<ProductionTask>[] Queues = Enumerable.Range(0, queues).Select(_ => new List<ProductionTask>()).ToArray();
    public bool HasRally;
    public WorldPosition Rally;
}
