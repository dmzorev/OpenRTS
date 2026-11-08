using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using OpenRTS.Core;

public partial class Sandbox
{
    private bool _verifyAviation;
    private async void VerifyAviation(FrameExchange.FrameLease frame)
    {
        try
        {
            _verificationDone = true;
            var poses = frame.Poses.ToArray();
            var report = _session!.Completion.GetAwaiter().GetResult();
            if (frame.Tick != report.Tick || !poses.Any(p => _catalog!.Units[p.UnitType].Airbase is not null) ||
                !poses.Any(p => _catalog!.Units[p.UnitType].Flight is not null) || _sprites!.SpriteCount < poses.Length)
                throw new InvalidOperationException("Incomplete aviation presentation.");
            if (_capturePath is not null && DisplayServer.GetName() != "headless")
            {
                _selected = poses.First(p => _catalog!.Units[p.UnitType].Airbase is not null).Id;
                RefreshSelection(); UpdateCommandPanel(frame); UpdateSelection(1); UpdateTerminal(frame, 1);
                _camera!.Size = 42; CenterCamera(new(29, 22));
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                string path = Path.GetFullPath(_capturePath); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var picture = GetViewport().GetTexture().GetImage();
                if (picture.SavePng(path) != Error.Ok) throw new IOException("Aviation capture failed.");
            }
            GD.Print("OPENRTS_AVIATION:PASS");
            GD.Print("OPENRTS_SIMULATION:" + JsonSerializer.Serialize(report)); GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }
}
