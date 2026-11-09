using Godot;
using OpenRTS.Core;
using OpenRTS.Core.Visibility;
using System;
using System.IO;

public partial class Sandbox
{
    private bool _verifyFog, _fogCaptureStarted;
    private void SwitchFogViewer()
    {
        _fogViewer = _fogViewer == 1 ? 2u : 1u; _constructionViewer = _fogViewer;
        _selection.Reset(); _focusStartingBase = true; _combatEffects?.Clear(); _sprites?.ClearWrecks();
    }
    private async void VerifyFog(FrameExchange.FrameLease frame)
    {
        if (_fogCaptureStarted || !_session!.Completion.IsCompleted || frame.Tick != _ticks) return;
        _fogCaptureStarted = true;
        try
        {
            if (frame.Viewer != 1 || !frame.FogStates.Contains(CellVisibility.Unknown) || !frame.FogStates.Contains(CellVisibility.Visible) || !frame.FogStates.Contains(CellVisibility.Explored))
                throw new Exception("Fog frame must contain all three states for player one.");
            using (var other = _session.Frames.AcquireLatest(2)!)
                if (other.Viewer != 2 || other.Poses.Length == 0 || other.Poses[0].Presentation.Owner == 1 && other.Poses[0].Id.Value == 3)
                    throw new Exception("Other viewer received a hidden base.");
            if (_label is not null) _label.Text = "Туман войны F1 · F8 сменить наблюдателя · три состояния карты";
            if (_capturePath is not null && DisplayServer.GetName() != "headless")
            {
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                string path = Path.GetFullPath(_capturePath); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var picture = GetViewport().GetTexture().GetImage();
                if (picture.SavePng(path) != Error.Ok) throw new IOException("Fog capture failed.");
            }
            SwitchFogViewer();
            if (Controls(1) || !Controls(2)) throw new Exception("Viewer control policy failed.");
            GD.Print("OPENRTS_FOG:PASS three map states, filtered players, hidden buildings and viewer controls");
            _verificationDone = true; GetTree().Quit();
        }
        catch(Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }
}
