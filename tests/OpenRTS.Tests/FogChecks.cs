using OpenRTS.Content;
using OpenRTS.Core;
using OpenRTS.Core.Combat;
using OpenRTS.Core.Orders;
using OpenRTS.Core.Visibility;

internal static class FogChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static SimulationWorld World(UnitDefinition[]? types = null) => SimulationWorld.CreateWithMechanics(new() { FogCellSize = 1024 },
        types ?? [new("scout", 100, body: BodyShape.None, visionRadius: 2048), new("blind", 100, body: BodyShape.None)],
        16384, 8192, players: [new(1, "a"), new(2, "b")]);

    internal static void Run(Action<string, Action> test)
    {
        test("Fog: incremental coverage equals exact full reference including clipped cells", () =>
        {
            var random = new Random(9102026);
            for (int sample = 0; sample < 100; sample++)
            {
                int width = random.Next(1, 350), depth = random.Next(1, 350), size = random.Next(1, 40);
                var fog = new FogOfWar(width, depth, [7, 19], size);
                var history = new HashSet<(uint, int)>();
                for (int step = 0; step < 20; step++)
                {
                    var sources = Enumerable.Range(1, random.Next(0, 9)).Select(i => new VisionSource(new((uint)i), random.Next(2) == 0 ? 7u : 19u,
                        random.Next(width + 1), random.Next(depth + 1), random.Next(0, 300))).ToArray();
                    fog.Update(sources);
                    foreach (uint player in fog.Players) for (int y = 0; y < fog.Rows; y++) for (int x = 0; x < fog.Columns; x++)
                    {
                        long cx = (long)x * size + Math.Min((x + 1) * size, width), cy = (long)y * size + Math.Min((y + 1) * size, depth);
                        int count = sources.Count(s => s.Owner == player && s.Radius > 0 &&
                            (cx - 2*s.X)*(cx - 2*s.X) + (cy - 2*s.Y)*(cy - 2*s.Y) <= 4L*s.Radius*s.Radius);
                        int cell = y * fog.Columns + x;
                        if (count > 0) history.Add((player, cell));
                        Check(fog.Coverage(player, cell) == count, "coverage mismatch");
                        Check(fog.At(player, cell) == (count > 0 ? CellVisibility.Visible : history.Contains((player, cell)) ? CellVisibility.Explored : CellVisibility.Unknown), "history mismatch");
                    }
                }
            }
        });
        test("Fog: overlaps, owner changes, subcell movement, zero radius and bounds", () =>
        {
            var fog = new FogOfWar(4097, 4097, [1, 2]);
            VisionSource a = new(new(1), 1, 2048, 2048, 1500), b = a with { Id = new(2) };
            fog.Update([a,b]); Check(fog.Coverage(1,6) == 2, "overlap");
            fog.Update([a]); Check(fog.At(1,6) == CellVisibility.Visible, "one source remains");
            fog.Update([a with { Owner=2 }]); Check(fog.At(1,6)==CellVisibility.Explored && fog.At(2,6)==CellVisibility.Visible, "owner history");
            fog.Update([]); Check(fog.At(2,6)==CellVisibility.Explored, "removal");
            fog.Update([a with { X=4097,Y=4097,Radius=1 }]); Check(fog.At(1,24)==CellVisibility.Visible, "clipped center");
            Check(fog.At(1,new WorldPosition(4097,4097)) == CellVisibility.Unknown, "out of world");
            fog.Update([a with { Radius=0 }]); Check(fog.At(1,24)==CellVisibility.Explored, "zero disabled");
        });
        test("Fog: content validation and gameplay fingerprint include vision", () =>
        {
            UnitCatalog Load(string radius) => UnitCatalog.Load([new("u", "x: {Health: {HP: 100}, Vision: {Radius: "+radius+"}}")]);
            Check(Load("1024").Units[0].VisionRadius==1024 && Load("1024").Summary!=Load("2048").Summary,"vision compile/hash");
            bool rejected=false; try { Load("-1"); } catch (Exception) { rejected=true; }
            Check(rejected,"negative radius accepted");
            rejected=false; try { _=new FogOfWar(1<<29,1<<29,[1],1); } catch(ArgumentException) { rejected=true; }
            Check(rejected,"unbounded fields accepted");
        });
        test("Fog: lifecycle, command snapshot, filtered frames and old leases", () =>
        {
            using var w=World();
            var scout=w.AddInitialUnit(0,new(2048,2048),1); var hidden=w.AddInitialUnit(1,new(12288,2048),2);
            var exchange=new FrameExchange(); exchange.TryPublish(w);
            using var old=exchange.AcquireLatest(1)!;
            Check(old.Poses.Length==1 && old.Poses[0].Id==scout && old.FogStates.Contains(CellVisibility.Unknown),"hidden pose leaked");
            using(var invalid=exchange.AcquireLatest(99)!) Check(invalid.Poses.Length==0,"invalid viewer");
            w.Step([new(1,1,1,CommandKind.AttackTarget,scout,TargetEntity:hidden)]);
            Check(w.Events.ToArray().Any(e=>e.Kind==WorldEventKind.CommandDenied),"guessed ID accepted");
            // A new source in this command batch cannot open a target for a later command in the same batch.
            w.Step([new(2,0,1,CommandKind.Spawn,Position:new(11264,2048),Owner:1),new(2,1,2,CommandKind.Aim,scout,TargetEntity:hidden)]);
            Check(w.IsVisible(1,hidden) && w.Events.ToArray().Any(e=>e.Kind==WorldEventKind.CommandDenied),"batch observed own mutations");
            exchange.TryPublish(w); using var now=exchange.AcquireLatest(1)!;
            Check(now.Poses.ToArray().Any(p=>p.Id==hidden) && old.Poses.Length==1,"lease mutated");
            Check(now.Poses.ToArray().Single(p=>p.Id==hidden).Previous==now.Poses.ToArray().Single(p=>p.Id==hidden).Current,"reveal interpolation");
            var source=now.Poses.ToArray().Single(p=>p.Presentation.Owner==1 && p.Id!=scout).Id;
            w.Step([new(3,0,1,CommandKind.Remove,source)]);
            exchange.TryPublish(w); using var after=exchange.AcquireLatest(1)!;
            Check(!w.IsVisible(1,hidden) && after.Poses.Length==1 && after.FogStates.Contains(CellVisibility.Explored),"removal coverage");
        });
        test("Fog: targeting ignores hidden nearest and cancels a hidden windup", () =>
        {
            var gun=new WeaponDefinition(4096,4096,10,30,windupTicks:2);
            using var w=World([new("gun",100,["idle","move","fire"],weapons:[new("main",gun)],body:BodyShape.None),new("scout",100,body:BodyShape.None,visionRadius:600),new("target",100,body:BodyShape.None)]);
            var shooter=w.AddInitialUnit(0,new(4096,4096),1);
            var nearest=w.AddInitialUnit(2,new(5120,4096),2);
            var far=w.AddInitialUnit(2,new(6656,4096),2);
            var scout=w.AddInitialUnit(1,new(6656,4608),1);
            w.Step([new(1,0,1,CommandKind.HoldPosition,shooter)]);
            var poses=NavigationChecks.Poses(w); var weapons=new WeaponPose[w.WeaponCount]; w.CopyWeaponPoses(weapons);
            Check(weapons[0].Target==far,"nearest hidden suppressed visible");
            w.Step([new(2,0,1,CommandKind.Remove,scout)]);
            w.CopyWeaponPoses(weapons); Check(weapons[0].Target.Value==0 && !w.Events.ToArray().Any(e=>e.Kind==WorldEventKind.WeaponFired),"hidden windup fired");
        });
        test("Fog: footprint visibility uses targetable sparse parts and wide arithmetic", () =>
        {
            using var world=World([new("eye",100,body:BodyShape.None,visionRadius:600),
                new("building",100,movable:false,footprint:new(5,1,[new(0,0,default),new(4,0,default)]))]);
            world.AddInitialUnit(0,new(2560,2560),1);
            var building=world.AddInitialUnit(1,new(4608,2560),2);
            world.SynchronizeVisibility();
            Check(world.IsVisible(1,building),"visible targetable edge ignored");
            using var hole=World([new("eye",100,body:BodyShape.None,visionRadius:600),
                new("building",100,movable:false,footprint:new(5,1,[new(0,0,default,Targetable:false),new(4,0,default)]))]);
            hole.AddInitialUnit(0,new(2560,2560),1);
            var sparse=hole.AddInitialUnit(1,new(4608,2560),2);hole.SynchronizeVisibility();
            Check(!hole.IsVisible(1,sparse),"non-targetable cell revealed sparse object");
            var huge=new FogOfWar(WorldPosition.MaximumExtent, WorldPosition.MaximumExtent,[1],WorldPosition.MaximumExtent);
            huge.Update([new(new(1),1,0,0,WorldPosition.MaximumExtent)]);
            Check(huge.At(1,0)==CellVisibility.Visible,"wide arithmetic overflow");
        });
        test("Fog: worker parity, journal replay and event redaction", () =>
        {
            SimulationWorld Make(int workers)
            {
                var world=SimulationWorld.CreateWithMechanics(new(){FogCellSize=1024},
                    [new("scout",100,visionRadius:2048),new("enemy",100)],32768,8192,
                    navigationGrid:new(32,8),navigationWorkers:workers,players:[new(1,"a"),new(2,"b")]);
                world.AddInitialUnit(0,new(2048,2048),1);world.AddInitialUnit(1,new(12288,2048),2);return world;
            }
            using var a=Make(1);using var b=Make(4);
            for(int tick=1;tick<=100;tick++)
            {
                WorldCommand[] commands=tick==1?[new(tick,1,1,CommandKind.Move,new(1),new(14336,2048))]:[];
                a.Step(commands);b.Step(commands);
                Check(a.Report()==b.Report() && a.Events.SequenceEqual(b.Events),"fog depends on worker order");
            }
            using var w=World(); var scout=w.AddInitialUnit(0,new(2048,2048),1); var hidden=w.AddInitialUnit(1,new(12288,2048),2);
            w.SynchronizeVisibility();w.Step([new(1,0,1,CommandKind.Destroy,hidden)]);
            Check(w.Events.ToArray().Single(e=>e.Kind==WorldEventKind.Destroyed).ForViewer(1) is null,"hidden death leaked");
        });
        test("Fog: grouped unions and translated phase caches equal individual coverage", () =>
        {
            var random=new Random(34);
            foreach(var mode in new[]{FogMode.Hidden,FogMode.Explored})
            {
                var reference=new FogOfWar(65539,32773,[1,2],1024,mode:mode);
                var optimized=new FogOfWar(65539,32773,[1,2],1024,optimizeGroups:true,mode:mode);
                var sources=Enumerable.Range(1,160).Select(i=>new VisionSource(new((uint)i),1,12000+(i%16)*512,9000+(i/16)*512,3072)).ToArray();
                int hits=0;
                for(int tick=0;tick<65;tick++)
                {
                    if(tick<24) sources=sources.Select(s=>s with{X=s.X+128}).ToArray();
                    else sources=sources.Where(s=>random.Next(8)!=0).Select(s=>s with{X=random.Next(65540),Y=random.Next(32774),Radius=random.Next(1,6000),Owner=random.Next(2)==0?1u:2u}).ToArray();
                    reference.Update(sources);optimized.Update(sources);hits+=optimized.GroupCacheHits;
                    foreach(uint player in reference.Players)for(int i=0;i<reference.CellCount;i++)
                        Check(reference.At(player,i)==optimized.At(player,i) && (reference.Coverage(player,i)>0)==(optimized.Coverage(player,i)>0),"union/cache diverged from exact sources");
                }
                Check(hits>0,"rigid translation did not reuse phase masks");
                optimized.Update([]);reference.Update([]);
                foreach(uint player in reference.Players)for(int i=0;i<reference.CellCount;i++) Check(reference.At(player,i)==optimized.At(player,i),"last group removal lost history");
            }
        });
        test("Fog: map modes preserve gameplay and visible mode skips all fields", () =>
        {
            SimulationWorld Make(FogMode mode,bool groups=true) => SimulationWorld.CreateWithMechanics(new(){FogCellSize=1024,FogMode=mode,OptimizeFogGroups=groups},
                [new("unit",100,body:BodyShape.None,visionRadius:2048)],32768,16384,players:[new(1,"a"),new(2,"b")]);
            using var hidden=Make(FogMode.Hidden);using var explored=Make(FogMode.Explored);using var visible=Make(FogMode.Visible);
            foreach(var w in new[]{hidden,explored,visible}) {w.AddInitialUnit(0,new(2048,2048),1);w.AddInitialUnit(0,new(24576,2048),2);w.SynchronizeVisibility();}
            Check(hidden.Fog!.At(1,200)==CellVisibility.Unknown && explored.Fog!.At(1,200)==CellVisibility.Explored,"initial map mode");
            Check(!hidden.IsVisible(1,new(2)) && !explored.IsVisible(1,new(2)) && visible.IsVisible(1,new(2)) && visible.Fog is null,"mode target policy");
            Check(hidden.Report()!=explored.Report(),"map modes missing from hash");
            using var a=Make(FogMode.Hidden);using var b=Make(FogMode.Hidden,false);
            for(int i=0;i<16;i++) foreach(var w in new[]{a,b})w.AddInitialUnit(0,new(8192+i*128,8192),1);
            for(int tick=1;tick<=20;tick++)
            {WorldCommand[] cmds=tick==1?[new(1,0,1,CommandKind.MoveAll,Position:new(24576,8192))]:[];a.Step(cmds);b.Step(cmds);Check(a.Report()==b.Report(),"optimization changed gameplay/hash");}
            var frames=new FrameExchange();frames.TryPublish(hidden);using var frame=frames.AcquireLatest(1)!;
            Check(frame.VisionSources.Length==1 && frame.VisionSources[0].Owner==1,"visual circles leaked enemy sources");
        });
        test("Fog: retained cells expire without moving sources and fade in copied frames", () =>
        {
            foreach(int hold in new[]{2,5,12,25,50})foreach(bool grouped in new[]{false,true})foreach(var mode in new[]{FogMode.Hidden,FogMode.Explored})
            {
                var fog=new FogOfWar(8192,8192,[1,2],1024,grouped,mode,hold);
                VisionSource[] eyes=Enumerable.Range(1,8).Select(i=>new VisionSource(new((uint)i),1,2048+i*128,2048,2048)).ToArray();
                fog.AdvanceTo(0);fog.Update(eyes);
                int cell=fog.CellAt(new(2560,2560));Check(fog.At(1,cell)==CellVisibility.Visible,"initial source");
                fog.AdvanceTo(1);fog.Update([]);
                fog.AdvanceTo(hold);Check(fog.At(1,cell)==CellVisibility.Visible,"retention expired too soon");
                fog.AdvanceTo(hold+1);Check(fog.At(1,cell)==CellVisibility.Explored,"stationary/empty world kept expired vision");
                Check(fog.At(2,cell)==(mode==FogMode.Hidden?CellVisibility.Unknown:CellVisibility.Explored),"retention crossed owners");
            }
            using var w=SimulationWorld.CreateWithMechanics(new(){FogCellSize=1024,FogVisibilityHoldTicks=5},
                [new("eye",100,body:BodyShape.None,visionRadius:2048),new("blind",100,body:BodyShape.None)],16384,8192,players:[new(1,"a"),new(2,"b")]);
            var eye=w.AddInitialUnit(0,new(2048,2048),1);var target=w.AddInitialUnit(1,new(3072,2048),2);
            var frames=new FrameExchange();frames.TryPublish(w);
            w.Step([new(1,0,1,CommandKind.Remove,eye)]);frames.TryPublish(w);using var first=frames.AcquireLatest(1)!;
            int at=w.Fog!.CellAt(new(3072,2048));byte brightness=first.FogStrength[at];
            Check(w.IsVisible(1,target) && first.Poses.Length==1 && brightness is >0 and <255,"retained gameplay sight/frame");
            for(int tick=2;tick<=5;tick++)w.Step();frames.TryPublish(w);using var last=frames.AcquireLatest(1)!;
            Check(w.IsVisible(1,target) && last.FogStrength[at]<brightness && first.FogStrength[at]==brightness,"fade or old lease changed");
            w.Step();frames.TryPublish(w);using var expired=frames.AcquireLatest(1)!;
            Check(!w.IsVisible(1,target) && expired.Poses.Length==0 && expired.FogStrength[at]==0,"unchanged sources stopped expiration");
        });
        test("Fog: retention refreshes on reentry, respects batch unions and hashes future expiration", () =>
        {
            var fog=new FogOfWar(8192,8192,[1],holdTicks:5);VisionSource source=new(new(1),1,2048,2048,2048);
            int cell=fog.CellAt(new(2560,2560));fog.Update([source]);fog.AdvanceTo(1);fog.Update([]);
            fog.AdvanceTo(3);fog.Update([source]);fog.AdvanceTo(4);fog.Update([]);
            fog.AdvanceTo(8);Check(fog.At(1,cell)==CellVisibility.Visible,"reentry did not refresh expiration");
            fog.AdvanceTo(9);Check(fog.At(1,cell)==CellVisibility.Explored,"reentry extended past deadline");
            SimulationWorld Make(int hold,bool grouped=true)=>SimulationWorld.CreateWithMechanics(new(){FogCellSize=1024,FogVisibilityHoldTicks=hold,OptimizeFogGroups=grouped},
                [new("eye",100,body:BodyShape.None,visionRadius:2048)],8192,8192,players:[new(1,"a")]);
            using var a=Make(2);using var b=Make(5);
            foreach(var w in new[]{a,b}) {w.AddInitialUnit(0,new(2048,2048),1);w.Step([new(1,0,1,CommandKind.Remove,new(1))]);}
            Check(a.Report()!=b.Report(),"retention duration/state missing from hash");
            using var grouped=Make(5);using var individual=Make(5,false);
            for(int i=0;i<8;i++)foreach(var w in new[]{grouped,individual})w.AddInitialUnit(0,new(2048+i*128,2048),1);
            for(int tick=1;tick<=75;tick++)
            {
                WorldCommand[] commands=tick==1?[new(1,0,1,CommandKind.MoveAll,Position:new(6144,2048))]:[];
                grouped.Step(commands);individual.Step(commands);
                Check(grouped.Report()==individual.Report(),"grouping changed future retention state");
            }

        });
        test("Fog: event audience survives loss of vision and removal", () =>
        {
            using var w=World(); var scout=w.AddInitialUnit(0,new(2048,2048),1); var victim=w.AddInitialUnit(1,new(3072,2048),2);
            w.SynchronizeVisibility();
            w.Step([new(1,0,1,CommandKind.Destroy,victim)]);
            var death=w.Events.ToArray().Single(e=>e.Kind==WorldEventKind.Destroyed);
            Check(death.ForViewer(1).HasValue && death.ForViewer(99) is null,"event audience");
            w.Step([new(2,0,1,CommandKind.Remove,scout)]);
            Check(death.ForViewer(1).HasValue,"historical event re-filtered from later fog");
        });
    }
}
