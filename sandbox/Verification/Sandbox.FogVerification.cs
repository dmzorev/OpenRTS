using Godot;
using OpenRTS.Core;
using OpenRTS.Core.Visibility;
using System;
using System.IO;

public partial class Sandbox
{
    private bool _verifyFog, _fogCaptureStarted;

    private async void VerifySensors(FrameExchange.FrameLease frame)
    {
        if (_verificationDone || !_session!.Completion.IsCompleted || frame.Tick != _ticks) return;
        _verificationDone = true;
        try
        {
            CheckRadarFrame(frame);
            if (_capturePath is not null && DisplayServer.GetName() != "headless")
            {
                _camera!.Size = 30;
                CenterCamera(new(17, 17));
                UpdateTerminal(frame, _displayAlpha);
                UpdateRadarContacts(frame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var picture = GetViewport().GetTexture().GetImage();
                if (picture.SavePng(Path.GetFullPath(_capturePath)) != Error.Ok) throw new IOException("Radar capture failed.");
            }
            GD.Print("OPENRTS_SENSORS:PASS three radar levels and a shot at a contact");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }

    private void CheckRadarFrame(FrameExchange.FrameLease frame)
    {
        if (frame.Viewer != 1 || frame.Poses.Length != 3 || frame.Contacts.Length != 3 || _rtsOverlay?.RadarAtlas is null)
            throw new InvalidOperationException($"Expected a loaded atlas, three models and three radar contacts for player 1. Got {frame.Poses.Length} models and {frame.Contacts.Length} contacts for player {frame.Viewer}.");
        int levels = 0;
        foreach (var contact in frame.Contacts)
        {
            if (contact.Health.HasValue || contact.States.HasValue) throw new InvalidOperationException("Radar contacts disclosed live fields.");
            levels |= 1 << (int)contact.Knowledge.Level;
        }
        if (levels != 7) throw new InvalidOperationException("Expected anonymous, classified and identified radar contacts.");
        var building = new ContactKnowledge(1, default, 0, 0, 0, ContactClass.Building);
        var vehicle = building with { Class = ContactClass.Vehicle };
        if (!OpenRTS.Godot.Rendering.ContactDisplay.ShowsModel(building, OpenRTS.Godot.Rendering.ContactDisplayStyle.BuildingModels) ||
            OpenRTS.Godot.Rendering.ContactDisplay.ShowsModel(vehicle, OpenRTS.Godot.Rendering.ContactDisplayStyle.BuildingModels) ||
            OpenRTS.Godot.Rendering.ContactDisplay.ShowsModel(building, OpenRTS.Godot.Rendering.ContactDisplayStyle.Icons) ||
            !OpenRTS.Godot.Rendering.ContactDisplay.ShowsModel(vehicle, OpenRTS.Godot.Rendering.ContactDisplayStyle.IdentifiedModels))
            throw new InvalidOperationException("Contact display preferences chose an incorrect representation.");
        using var truth = _session!.Frames.AcquireLatest()!;
        if (truth.Projectiles.Length != 1) throw new InvalidOperationException("The cannon should fire at the radar contact.");
    }

    private void SwitchFogViewer()
    {
        using var frame = _session?.Frames.AcquireLatest();
        if (frame is null || frame.Players.Length < 2) return;
        var players = frame.Players[1..];
        uint next = players[0].Id;
        foreach (var player in players)
            if (player.Id > _fogViewer) { next = player.Id; break; }
        _fogViewer = _constructionViewer = next;
        _rtsOverlay?.RadarContacts.Clear();
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
                string path = Path.GetFullPath(_capturePath); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                foreach (var style in Enum.GetValues<OpenRTS.Godot.Rendering.FogDisplayStyle>())
                {
                    _fogStyle = style;
                    using (var current = _session.Frames.AcquireLatest(_fogViewer)!)
                    {
                        _fogView!.Apply(current, style);
                        _minimap?.UpdateFog(current, _fogView.Texture);
                    }
                    await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    using var picture = GetViewport().GetTexture().GetImage();
                    string variant = Path.Combine(Path.GetDirectoryName(path)!, "fog-" + style.ToString().ToLowerInvariant() + ".png");
                    if (picture.SavePng(variant) != Error.Ok || style == OpenRTS.Godot.Rendering.FogDisplayStyle.Compare && picture.SavePng(path) != Error.Ok)
                        throw new IOException("Fog capture failed.");
                }
            }
            SwitchFogViewer();
            if (Controls(1) || !Controls(2)) throw new Exception("Viewer control policy failed.");
            GD.Print("OPENRTS_FOG:PASS three map states, filtered players, hidden buildings and viewer controls");
            _verificationDone = true; GetTree().Quit();
        }
        catch(Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }
}
