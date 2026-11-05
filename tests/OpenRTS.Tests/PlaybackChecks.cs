using OpenRTS.Core;

internal static class PlaybackChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static SimulationWorld Create()
    {
        var world = new SimulationWorld([new("unit", 100)], 100000, 100000);
        world.AddInitialUnit(0, new(0, 0));
        return world;
    }
    private static long Tick(SimulationSession session)
    {
        using var frame = session.Frames.AcquireLatest();
        return frame?.Tick ?? -1;
    }
    private static void AwaitTick(SimulationSession session, long tick)
    {
        Check(SpinWait.SpinUntil(() => Tick(session) >= tick || session.Completion.IsCompleted, TimeSpan.FromSeconds(5)), "Playback worker did not reach requested tick");
        if (session.Completion.IsFaulted) session.Completion.GetAwaiter().GetResult();
        Check(Tick(session) == tick, "Playback advanced beyond a paused step");
    }

    public static void Run(Action<string, Action> test)
    {
        test("Pause, queued single steps and tempo preserve the command log and world state", () =>
        {
            WorldCommand[] commands = [new(1, 0, 1, CommandKind.MoveAll, Position: new(10000, 10000))];
            using var session = new SimulationSession(Create, commands, endTick: 24, startPaused: true);
            AwaitTick(session, 0);
            Thread.Sleep(80);
            Check(Tick(session) == 0, "Paused session advanced");
            session.StepOnce(); session.StepOnce(); AwaitTick(session, 2);
            Check(session.IsPaused && session.GetInterpolationAlpha(2) == 1, "Single step did not display its completed state");
            session.SetPlayback(0.125, true); session.StepOnce(); AwaitTick(session, 3);
            session.SetPlayback(4, false);
            var result = session.Completion.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            using var direct = Create();
            for (int tick = 1; tick <= 24; tick++) direct.Step(tick == 1 ? commands : []);
            Check(result == direct.Report(), "Playback controls changed authoritative simulation");
            Check(session.GetCommandLog().SequenceEqual(commands), "Playback controls entered the game journal");
        });
        test("Playback validates controls, avoids interpolation rewind and stops while paused", () =>
        {
            using var session = new SimulationSession(Create, startPaused: true);
            AwaitTick(session, 0);
            Reject<ArgumentOutOfRangeException>(() => session.SetPlayback(double.NaN, false));
            Reject<ArgumentOutOfRangeException>(() => session.SetPlayback(0, false));
            Reject<ArgumentOutOfRangeException>(() => session.SetPlayback(16, false));
            session.StepOnce(); AwaitTick(session, 1);
            session.SetPlayback(0.125, false);
            Check(session.GetInterpolationAlpha(1) == 1, "Resuming rewound a completed paused frame");
            Reject<InvalidOperationException>(() => session.StepOnce());
            session.SetPlayback(1, true);
            session.Dispose();
            Check(session.Completion.IsCompleted, "Paused worker outlived Dispose");
            Reject<ObjectDisposedException>(() => session.StepOnce());
            using var unpaced = new SimulationSession(Create, endTick: 1, realtime: false);
            Reject<InvalidOperationException>(() => unpaced.SetPlayback(1, true));
            Reject<InvalidOperationException>(() => unpaced.StepOnce());
            unpaced.Completion.GetAwaiter().GetResult();
        });
    }
}
