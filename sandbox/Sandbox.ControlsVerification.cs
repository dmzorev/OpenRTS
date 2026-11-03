using System;
using Godot;

public partial class Sandbox
{
    private bool _controlsStepped;

    private void VerifyControls(long tick)
    {
        var session = _session ?? throw new InvalidOperationException("Missing controls session.");
        var camera = _camera ?? throw new InvalidOperationException("Missing camera.");
        if (!_controlsStepped)
        {
            if (tick != 0 || !session.IsPaused) throw new InvalidOperationException("Controls test must start paused at tick zero.");
            var mouse = GetViewport().GetVisibleRect().Size / 2;
            var focus = GroundPoint(mouse)!.Value;
            float size = camera.Size;
            _UnhandledInput(new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.WheelUp, Position = mouse });
            if (camera.Size >= size || GroundPoint(mouse)!.Value.DistanceTo(focus) > 0.01f)
                throw new InvalidOperationException("Wheel zoom lost focus or did not zoom.");
            _UnhandledInput(new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.WheelDown, Position = mouse });
            if (Math.Abs(camera.Size - size) > 0.01f) throw new InvalidOperationException("Wheel zoom out failed.");
            var original = camera.Position;
            _UnhandledInput(new InputEventMouseMotion { Position = mouse + new Vector2(20, 20), Relative = new(20, 20), ButtonMask = MouseButtonMask.Middle });
            if (camera.Position.IsEqualApprox(original)) throw new InvalidOperationException("Middle-button pan failed.");
            MoveCamera(original - camera.Position);
            _UnhandledInput(new InputEventKey { Keycode = Key.Equal, Pressed = true });
            if (session.PlaybackSpeed != 2) throw new InvalidOperationException("Speed-up key failed.");
            _UnhandledInput(new InputEventKey { Keycode = Key.Minus, Pressed = true });
            if (session.PlaybackSpeed != 1) throw new InvalidOperationException("Slow-down key failed.");
            _UnhandledInput(new InputEventKey { Keycode = Key.N, Pressed = true });
            _UnhandledInput(new InputEventKey { Keycode = Key.N, Pressed = true });
            _controlsStepped = true;
            return;
        }
        if (tick < 2) return;
        if (tick != 2 || !session.IsPaused || session.GetInterpolationAlpha(tick) != 1)
            throw new InvalidOperationException("Queued single-step keys did not stop at exactly two completed ticks.");
        _UnhandledInput(new InputEventKey { PhysicalKeycode = Key.P, Pressed = true });
        if (session.IsPaused) throw new InvalidOperationException("Resume key failed.");
        _UnhandledInput(new InputEventKey { Keycode = Key.P, Pressed = true });
        if (!session.IsPaused) throw new InvalidOperationException("Pause key failed.");
        GD.Print("OPENRTS_CONTROLS:PASS wheel focus, zoom out, middle pan, speed, pause and single step");
        _verificationDone = true;
        GetTree().Quit();
    }
}
