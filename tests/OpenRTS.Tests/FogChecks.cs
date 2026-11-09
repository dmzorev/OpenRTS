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
