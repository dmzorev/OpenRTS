using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using OpenRTS.Core;
using OpenRTS.Core.Visibility;

/// <summary>Isolated fog experiment. The footprint memo is deliberately not a production gameplay backend.</summary>
internal static class FogRetentionBenchmark
{
    private readonly record struct Key(uint Owner, int X, int Y, int Radius);
    private sealed class Entry(VisionSource source) { internal readonly VisionSource Source = source; internal long Expires; }
    private sealed class FootprintMemo
    {
        private readonly Dictionary<Key, Entry> _entries = [];
        private HashSet<Key> _active = [], _next = [];
        private readonly PriorityQueue<(Key Key, long Expires), long> _expiry = new();
        private readonly List<VisionSource> _output = [];
        private uint _id;
        internal int Count => _entries.Count;
        internal int Hits { get; private set; }
        internal ReadOnlySpan<VisionSource> Update(ReadOnlySpan<VisionSource> sources, long tick, int hold)
        {
            Hits = 0; _next.Clear();
            foreach (var s in sources)
            {
                if (s.Owner == 0 || s.Radius == 0) continue;
                var key = new Key(s.Owner,s.X,s.Y,s.Radius);
                if (!_next.Add(key)) continue;
                if (_entries.TryGetValue(key,out var entry)) { if (entry.Expires != 0) Hits++; entry.Expires = 0; }
                else
                {
                    if (_entries.Count >= 1024*1024 || _id == uint.MaxValue) throw new InvalidOperationException("Experimental footprint memo budget exceeded.");
                    _entries.Add(key,new(s with { Id = new(++_id) }));
                }
            }
            foreach (var key in _active)
                if (!_next.Contains(key))
                {
                    var entry = _entries[key]; entry.Expires = checked(tick+hold);
                    _expiry.Enqueue((key,entry.Expires),entry.Expires);
                }
            while (_expiry.TryPeek(out var item,out long due) && due <= tick)
            {
                _expiry.Dequeue();
                if (_entries.TryGetValue(item.Key,out var entry) && entry.Expires == item.Expires && !_next.Contains(item.Key)) _entries.Remove(item.Key);
            }
            (_active,_next) = (_next,_active);
            _output.Clear(); foreach (var entry in _entries.Values) _output.Add(entry.Source);
            return CollectionsMarshal.AsSpan(_output);
        }
    }

    private sealed record Result(double Median, double P95, double Maximum, long Allocation, long Heap, int Entries, long Hits, string Hash);
    internal static void Run(int count,int ticks)
    {
        VerifyMemo();
        foreach (string profile in new[]{"straight-1024","straight-256","zigzag-1024","scatter"})
        foreach (bool grouped in new[]{false,true})
        foreach (int hold in new[]{0,2,5,12,25,50})
        {
            var cell = Measure(count,ticks,profile,grouped,hold,false);
            var memo = Measure(count,ticks,profile,grouped,hold,true);
            if(cell.Hash != memo.Hash)throw new Exception($"Retention backends diverged: {profile}/{grouped}/{hold}");
            Print("cell",cell); Print("memo",memo);
            void Print(string backend,Result r) => Console.WriteLine(FormattableString.Invariant($"FOG_RETENTION profile={profile} count={count} groups={grouped} hold={hold} seconds={hold/25.0:F2} backend={backend} ticks={ticks} p50={r.Median:F3} p95={r.P95:F3} max={r.Maximum:F3} alloc/tick={r.Allocation} retained_heap={r.Heap} entries={r.Entries} reuse={r.Hits} hash={r.Hash}"));
        }
    }
    private static Result Measure(int count,int ticks,string profile,bool grouped,int hold,bool memoized)
    {
        const int warm=64;
        GC.Collect();long heapBefore=GC.GetTotalMemory(true);
        int width=checked(((count/16+1)*1024+(ticks+warm)*256+131072+1023)/1024*1024);
        var fog=new FogOfWar(width,128*1024,[1,2],optimizeGroups:grouped,holdTicks:memoized?0:hold);
        var memo=memoized?new FootprintMemo():null;
        var sources=new VisionSource[count];
        for(int tick=0;tick<warm;tick++){Fill(sources,tick,profile);Step(tick);}
        long heap=GC.GetTotalMemory(true)-heapBefore;
        var samples=new double[ticks];long allocated=0,hits=0;
        for(int i=0;i<ticks;i++)
        {
            int tick=warm+i;Fill(sources,tick,profile);
            long before=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp();Step(tick);
            samples[i]=Stopwatch.GetElapsedTime(start).TotalMilliseconds;allocated+=GC.GetAllocatedBytesForCurrentThread()-before;
            hits+=memo?.Hits??0;
        }
        Array.Sort(samples);var states=new byte[fog.CellCount*2];
        for(uint p=1;p<=2;p++)for(int i=0;i<fog.CellCount;i++)states[(int)(p-1)*fog.CellCount+i]=(byte)fog.At(p,i);
        return new(samples[ticks/2],samples[(int)((ticks-1)*.95)],samples[^1],allocated/ticks,heap,memo?.Count??count,hits,Convert.ToHexString(SHA256.HashData(states)));
        void Step(long tick)
        {
            fog.AdvanceTo(tick);
            if(memo is null)fog.Update(sources);else fog.Update(memo.Update(sources,tick,hold));
        }
    }
    private static void Fill(VisionSource[] sources,int tick,string profile)
    {
        int pitch=profile=="straight-256"?256:1024;
        for(int i=0;i<sources.Length;i++)
        {
            int distance=i/16*pitch+tick*128;
            int x=65536+distance,y=65536+i%16*1024;
            if(profile=="zigzag-1024")y+=distance/4096%2==0?distance%4096:4096-distance%4096;
            if(profile=="scatter"){x+=tick*(i%17-8);y+=tick*(i%7-3);}
            sources[i]=new(new((uint)i+1),1,x,y,6144);
        }
    }
    private static void VerifyMemo()
    {
        var random=new Random(35);
        foreach(int hold in new[]{0,2,5,12,25,50})foreach(bool grouped in new[]{false,true})
        {
            var reference=new FogOfWar(4099,2049,[1,2],128,grouped,holdTicks:hold);
            var cached=new FogOfWar(4099,2049,[1,2],128,grouped);var memo=new FootprintMemo();
            for(int tick=0;tick<90;tick++)
            {
                var sources=Enumerable.Range(1,random.Next(0,10)).Select(i=>new VisionSource(new((uint)i),(uint)(random.Next(2)+1),random.Next(0,32)*128,random.Next(0,16)*128,random.Next(1,5)*128)).ToArray();
                reference.AdvanceTo(tick);cached.AdvanceTo(tick);reference.Update(sources);cached.Update(memo.Update(sources,tick,hold));
                foreach(uint player in reference.Players)for(int i=0;i<reference.CellCount;i++)
                    if(reference.At(player,i)!=cached.At(player,i))throw new Exception("Temporal memo differs from cell retention reference.");
            }
        }
        Console.WriteLine("FOG_RETENTION_REFERENCE:PASS 12 randomized traces, all cells after every tick");
    }
}
