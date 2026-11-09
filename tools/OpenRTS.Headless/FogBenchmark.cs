using System.Diagnostics;
using OpenRTS.Core;
using OpenRTS.Core.Orders;

internal static class FogBenchmark
{
    internal static void Run(int count, int ticks)
    {
        foreach (int size in new[] { 0, 512, 1024, 2048 }) foreach (int players in new[] { 1, 2, 8 }) foreach (bool moving in new[] { false, true })
        {
            using var world = SimulationWorld.CreateWithMechanics(new() { FogCellSize = size == 0 ? null : size },
                [new("scout", 100, body: OpenRTS.Core.Navigation.BodyShape.None, visionRadius: 6144)], 256*1024, 256*1024,
                players: Enumerable.Range(1,players).Select(p=>new PlayerDefinition((uint)p,"P"+p)), initialCapacity:count);
            for(int i=0;i<count;i++) world.AddInitialUnit(0,new((i%128)*2048+512,(i/128%128)*2048+512),(uint)(i%players+1));
            if(moving) world.Step([new(1,0,1,CommandKind.MoveAll,Position:new(128*1024,128*1024))]);
            for(int i=0;i<20;i++) world.Step();
            var times=new double[ticks]; long before=GC.GetAllocatedBytesForCurrentThread();
            for(int i=0;i<ticks;i++) { long start=Stopwatch.GetTimestamp();world.Step();times[i]=Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
            long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
            var frames=new FrameExchange();long startFrame=Stopwatch.GetTimestamp();frames.TryPublish(world);using var frame=frames.AcquireLatest(1);
            double frameMs=Stopwatch.GetElapsedTime(startFrame).TotalMilliseconds;
            long startHash=Stopwatch.GetTimestamp();world.Report();double hashMs=Stopwatch.GetElapsedTime(startHash).TotalMilliseconds;
            Array.Sort(times);
            Console.WriteLine($"FOG_BENCH count={count} players={players} cell={size} moving={moving} radius=6144 map=262144x262144 ticks={ticks} p50={times[ticks/2]:F3} p95={times[(int)((ticks-1)*.95)]:F3} p99={times[(int)((ticks-1)*.99)]:F3} max={times[^1]:F3} alloc/tick={allocated/ticks} frame+viewer_ms={frameMs:F3} hash_ms={hashMs:F3}");
        }
    }
}
