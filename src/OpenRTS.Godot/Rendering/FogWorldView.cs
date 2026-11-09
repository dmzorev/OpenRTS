using Godot;
using OpenRTS.Core;
using OpenRTS.Core.Visibility;

namespace OpenRTS.Godot.Rendering;

public sealed class FogWorldView
{
    private readonly ColoredMeshBatch _batch = new(new PlaneMesh { Size = Vector2.One }, "FogOfWar");
    public MultiMeshInstance3D Node => _batch.Node;
    private long _tick = -1;
    private uint _viewer;
    public void Apply(FrameExchange.FrameLease frame)
    {
        if (frame.FogCellSize == 0 || _tick == frame.Tick && _viewer == frame.Viewer) return;
        _tick = frame.Tick; _viewer = frame.Viewer;
        _batch.Clear();
        for (int y = 0; y < frame.FogRows; y++) for (int x = 0; x < frame.FogColumns; x++)
        {
            var state = frame.FogStates[y * frame.FogColumns + x];
            if (state == CellVisibility.Visible) continue;
            float left = x * frame.FogCellSize / 1024f, top = y * frame.FogCellSize / 1024f;
            float right = Math.Min((x + 1L) * frame.FogCellSize, frame.FogWidth) / 1024f;
            float bottom = Math.Min((y + 1L) * frame.FogCellSize, frame.FogDepth) / 1024f;
            _batch.Add(new((left + right) / 2, 0.08f, (top + bottom) / 2), new(right-left, 1, bottom-top), 0,
                new Color(0, 0, 0, state == CellVisibility.Unknown ? 1 : 0.65f));
        }
        _batch.Publish();
    }
}
