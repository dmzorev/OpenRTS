using Godot;
using OpenRTS.Core;
using OpenRTS.Godot.Controls;

public partial class Sandbox
{
    private int _aiMatchStage;
    private const int AiVerificationTicks = 550;

    private void SelectAiVerificationMatch()
    {
        var setup = _matchSetup!;
        var option = (CheckButton)setup.FindChild("AiOpponent", owned: false);
        var modes = (OptionButton)setup.FindChild("Modes", owned: false);
        modes.Select(1);
        modes.EmitSignal(OptionButton.SignalName.ItemSelected, 1);
        CheckSkirmish(option.Disabled, "AI was offered with an incompatible starting set.");
        modes.Select(0);
        modes.EmitSignal(OptionButton.SignalName.ItemSelected, 0);
        CheckSkirmish(!option.Disabled, "AI was not offered for the valley construction start.");
        option.ButtonPressed = true;
        // Exchange physical starts. The human remains player 1 and the bot plans from the west.
        var starts = (GridContainer)setup.FindChild("Players", owned: false);
        var blue = starts.GetChild<OptionButton>(1);
        blue.Select(1);
        blue.EmitSignal(OptionButton.SignalName.ItemSelected, 1);
        ((Button)setup.FindChild("Launch", owned: false)).EmitSignal(Button.SignalName.Pressed);
    }

    private void VerifyAiMatch(FrameExchange.FrameLease frame)
    {
        var session = _session!;
        if (_aiMatchStage == 0)
        {
            CheckSkirmish(session.IsPaused && frame.Tick == 0 && session.GetCommandLog().Length == 0 &&
                _opponent?.Diagnostics == "start", "AI planned while the match was paused at tick zero.");
            var yards = frame.Poses.ToArray().Where(p => _catalog!.Units[p.UnitType].Id == "supply-center").ToArray();
            var own = yards.Single(p => p.Presentation.Owner == 1);
            var enemy = yards.Single(p => p.Presentation.Owner == 2);
            _selection.Change([enemy.Id], SelectionChange.Replace);
            RefreshSelection();
            CheckSkirmish(Controlled().Length == 0 && !CanIssue(UnitActionKind.Stop) && ActiveRecipients().Length == 0,
                "Inspecting the AI base allowed player commands.");
            var ownEngineer = frame.Poses.ToArray().Single(p => p.Presentation.Owner == 1 && p.Id != own.Id);
            var enemyEngineer = frame.Poses.ToArray().Single(p => p.Presentation.Owner == 2 && p.Id != enemy.Id);
            _selection.Change([ownEngineer.Id], SelectionChange.Replace);
            RefreshSelection();
            ActivateCustom("site-power", false, false);
            var site = new WorldPosition(59904, 45056);
            HandleConstructionClick(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true }, site, null);
            session.Submit(CommandKind.PlanConstruction, site, recipients: new([enemyEngineer.Id]), action: "site-power");
            _aiMatchStage = 1;
            session.StepOnce();
            return;
        }

        if (_aiMatchStage == 1)
        {
            if (frame.Tick < 1) return;
            CheckSkirmish(frame.Tick == 1 && session.IsPaused &&
                frame.ConstructionProjects.Length == 2,
                "The human and AI did not each place one project on the single step.");
            var first = session.GetCommandLog();
            CheckSkirmish(first.Length == 3 && first.All(c => c.Tick == 1) &&
                first.Count(c => c.Issuer == 1) == 2 && first.Count(c => c.Issuer == 2) == 1,
                "Live and AI inputs were not journaled with separate issuers.");
            _aiMatchStage = 2;
            for (int i = 1; i < AiVerificationTicks; i++) session.StepOnce();
            return;
        }

        if (frame.Tick < AiVerificationTicks) return;
        CheckSkirmish(frame.Tick == AiVerificationTicks && session.IsPaused, "AI advanced past the requested paused steps.");
        CheckSkirmish(frame.Poses.ToArray().Any(p => p.Presentation.Owner == 2 && p.Current.X < 40000 &&
            _catalog!.Units[p.UnitType].Id == "barracks"), "The bot did not build from its assigned western start.");
        var log = session.GetCommandLog();
        using var replay = _mapMatch!.CreateWorld(1, restrictPlayerCommands: true);
        DiagnosticScenario.Run(replay, AiVerificationTicks, log);
        var frames = new FrameExchange();
        frames.TryPublish(replay);
        using var verified = frames.AcquireLatest()!;
        CheckSkirmish(frame.Poses.SequenceEqual(verified.Poses) && frame.Players.SequenceEqual(verified.Players) &&
            frame.Tasks.SequenceEqual(verified.Tasks), "The interactive command journal did not reproduce the same world projection.");
        GD.Print("OPENRTS_AI_MATCH:PASS setup, command ownership, pause and journal replay");
        _verificationDone = true;
        GetTree().Quit();
    }
}
