using System.Diagnostics;
using OpenRTS.Core;
using OpenRTS.Core.Navigation;
using OpenRTS.Core.Orders;

internal static class FogGroupBenchmark
{
    internal static void Run(int count, int ticks)
    {
        foreach (var profile in new[] { "compact", "spread", "deforming" })
        {
            SimulationReport? reference = null;
            foreach (bool grouped in new[] { false, true })
            {
                using var world = SimulationWorld.CreateWithMechanics(new() { FogCellSize=1024, OptimizeFogGroups=grouped },
                    [new("scout",100,body:BodyShape.None,visionRadius:6144)],512*1024,512*1024,
                    initialCapacity:count,players:[new(1,"a"),new(2,"b")]);
                var commands=new WorldCommand[count];
                for(int i=0;i<count;i++)
                {
                    int pitch=profile=="spread"?2048:256;
                    var p=new WorldPosition(65536+i%100*pitch,65536+i/100*pitch);
                    var id=world.AddInitialUnit(0,p,1);
                    commands[i]=new(1,0,i+1,CommandKind.Move,id,profile=="deforming"?new(192*1024,192*1024):p with{X=p.X+65536});
                }
                world.Step(commands);
                for(int i=0;i<32;i++)world.Step();
                var times=new double[ticks];long before=GC.GetAllocatedBytesForCurrentThread();
                for(int i=0;i<ticks;i++){long start=Stopwatch.GetTimestamp();world.Step();times[i]=Stopwatch.GetElapsedTime(start).TotalMilliseconds;}
                long bytes=GC.GetAllocatedBytesForCurrentThread()-before;
                var report=world.Report();if(reference is not null && report!=reference)throw new Exception("Optimization changed gameplay.");reference=report;
                Array.Sort(times);
                Console.WriteLine($"FOG_GROUPS profile={profile} count={count} grouped={grouped} ticks={ticks} p50={times[ticks/2]:F3} p95={times[(int)((ticks-1)*.95)]:F3} max={times[^1]:F3} alloc/tick={bytes/ticks} cache_hits={world.Fog!.GroupCacheHits} rasterized={world.Fog.RasterizedSources} hash={report.StateHash}");
            }
        }
    }
}
