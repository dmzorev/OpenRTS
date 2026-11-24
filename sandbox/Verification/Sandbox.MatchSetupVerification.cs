using System;
using Godot;
using OpenRTS.Content;
using OpenRTS.Core;

public partial class Sandbox
{
    private bool _verifyFogMatch;
    private void SelectVerificationMatch()
    {
        CheckSkirmish(_session is null && _camera is null, "A simulation or camera started before the lobby launch.");
        OptionButton FindOption(string name) => (OptionButton)_matchSetup!.FindChild(name, owned: false);
        static void Choose(OptionButton option, int index)
        {
            option.Select(index);
            option.EmitSignal(OptionButton.SignalName.ItemSelected, index);
        }

        var fog = FindOption("Fog");
        CheckSkirmish(fog.ItemCount == 3 && fog.Selected == 0 && FindOption("FogStyle").ItemCount == 3, "Fog settings missing.");
        CheckSkirmish(FindOption("RadarStyle").ItemCount == 3 && FindOption("RadarStyle").Selected == 1,
            "Radar display preferences or their default are missing.");
        Choose(fog, 1); CheckSkirmish(!FindOption("FogStyle").Disabled, "Explored mode lost style choice.");
        Choose(fog, 2); CheckSkirmish(FindOption("FogStyle").Disabled, "Visible mode must disable fog styling.");
        if (_verifyFogMatch) Choose(fog, 0);
        var maps = FindOption("Maps");
        var count = FindOption("PlayerCount");
        CheckSkirmish(maps.ItemCount >= 3 && count.ItemCount == 2, "The lobby does not show all maps or the valley capacity.");
        Choose(maps, 1);
        CheckSkirmish(count.ItemCount == 4, "The city map does not allow four players.");
        Choose(count, 3);
        Choose(maps, 0);
        CheckSkirmish(count.ItemCount == 2 && count.Selected == 1, "Player count was not clamped on switching maps.");
        Choose(maps, 2);
        Choose(count, 2);
        Choose(FindOption("Modes"), 1);
        var players = (GridContainer)_matchSetup!.FindChild("Players", owned: false);
        var blue = (OptionButton)players.FindChild("Start1", owned: false);
        var red = (OptionButton)players.FindChild("Start2", owned: false);
        Choose(blue, 1);
        CheckSkirmish(red.Selected == 0, "Selecting an occupied start did not exchange slots.");
        ((Button)_matchSetup.FindChild("Launch", owned: false)).EmitSignal(Button.SignalName.Pressed);
        CheckSkirmish(_session is { IsPaused: true }, "The match was not prepared while the lobby was visible.");
    }

    private void VerifyConfiguredMatch(FrameExchange.FrameLease frame)
    {
        var map = _mapMatch!.Map;
        CheckSkirmish(frame.Tick == 0 && _matchSetup is null && _terminal is not null, "The configured match did not replace the lobby at tick zero.");
        CheckSkirmish(map.Id == "riverside-station" && frame.Players.Length == 4 && _mapWidth == 88 && _mapDepth == 60,
            "The selected map, dimensions or player count did not reach the match.");
        if (_verifyFogMatch)
        {
            CheckSkirmish(PlayerFog && frame.Viewer == 1 && frame.FogCellSize == 1024 && frame.VisionSources.Length == 3 &&
                frame.Poses.ToArray().Count(p => p.Presentation.Owner == 1) == 3 && !frame.Poses.ToArray().Any(p => p.Presentation.Owner == 2) && !Controls(3), "Configured fog did not filter game state.");
            GD.Print("OPENRTS_FOG_MATCH:PASS hidden match, unit vision, filtering, minimap and player control");
            _verificationDone = true; GetTree().Quit(); return;
        }
        CheckSkirmish(frame.Poses.Length == 32 && (PlayerFog ? !Controls(3) : Controls(3)), "Third side or alternate starting set is missing.");
        var poses = frame.Poses.ToArray();
        var craneOffset = _setupSets["infantry"].Units.Single(unit => unit.Type == "crane").Offset;
        for (uint owner = 1; owner <= 3; owner++)
        {
            int slot = owner switch { 1 => 1, 2 => 0, _ => 2 };
            var anchor = map.StartPosition(map.Slots[slot]);
            var team = poses.Where(p => p.Presentation.Owner == owner).ToArray();
            CheckSkirmish(team.Length == 3 && team.Any(p => _catalog!.Units[p.UnitType].Id == "crane" &&
                (p.Current == new WorldPosition(anchor.X + craneOffset.X, anchor.Y + craneOffset.Y) ||
                 p.Current == new WorldPosition(anchor.X - craneOffset.X, anchor.Y - craneOffset.Y))) &&
                team.Any(p => _catalog!.Units[p.UnitType].Id == "rocketeer"),
                "The chosen starting position or mode was ignored.");
        }
        CheckSkirmish(poses.Any(p => p.Presentation.Owner == 0 && _catalog!.Units[p.UnitType].Id == "civilian-station"),
            "The station was not placed as neutral.");
        var selected = Selected() ?? throw new InvalidOperationException("The player's starting unit is not selected.");
        CheckSkirmish(selected.Id == poses.Single(p => p.Presentation.Owner == 1 && _catalog!.Units[p.UnitType].Id == "crane").Id,
            "The initial selection belongs to a different player after exchanging starts.");
        var focus = GroundPoint(Playfield().GetCenter());
        CheckSkirmish(focus is { } center && Mathf.IsEqualApprox(center.X, selected.Current.X / (float)WorldPosition.UnitsPerCell) &&
            Mathf.IsEqualApprox(center.Z, selected.Current.Y / (float)WorldPosition.UnitsPerCell),
            "The camera did not focus the first player's chosen start.");
        if (_manifest.Settings.Presentation.Camera == GameCamera.Isometric)
        {
            var camera = _camera!;
            var ground = new Vector3(_mapWidth / 2, 0, _mapDepth / 2);
            var pixel = camera.UnprojectPosition(ground);
            var east = camera.UnprojectPosition(ground + Vector3.Right) - pixel;
            var south = camera.UnprojectPosition(ground + Vector3.Back) - pixel;
            CheckSkirmish(Math.Abs(east.X + south.X) < 0.01f && Math.Abs(east.Y - south.Y) < 0.01f &&
                east.X * south.X < 0 && east.Y * south.Y > 0,
                $"The configured isometric camera did not project a diamond grid. East {east}, south {south}, angles {camera.RotationDegrees}.");
            CheckSkirmish(GroundPoint(pixel) is { } picked && picked.DistanceTo(ground) < 0.001f,
                "The isometric camera did not pick the projected world position.");
            foreach (var (id, alternative) in _manifest.Settings.Presentation.ModelOverrides)
                CheckSkirmish(ReferenceEquals(_modelCatalog!.Models[id].Parts, _modelCatalog.Models[alternative].Parts),
                    "The match did not load the model override for " + id);
        }
        GD.Print("OPENRTS_MATCH_SETUP:PASS maps, mode, player count, start exchange and scene launch");
        _verificationDone = true;
        GetTree().Quit();
    }
}
