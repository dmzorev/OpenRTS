using Godot;
using OpenRTS.Content;

public sealed partial class MatchSetupPreview : Control
{
    private Texture2D? _texture;
    private Vector2[] _points = [], _objects = [];
    private int[] _assigned = [];

    public void SetMap(GameMap map, LocomotionCatalog terrain)
    {
        _texture = TerminalMinimap.CreateTerrainTexture(map.Grid, terrain);
        var worldSize = new Vector2(map.Grid.Width, map.Grid.Height) * map.Grid.CellSize;
        Vector2 Normalize(OpenRTS.Core.WorldPosition p) => new Vector2(p.X, p.Y) / worldSize;
        _points = map.Slots.Select(slot => Normalize(map.StartPosition(slot))).ToArray();
        _objects = map.ObjectPositions.Select(Normalize).ToArray();
        QueueRedraw();
    }

    public void SetAssignments(IReadOnlyList<int> slots)
    {
        _assigned = new int[_points.Length];
        for (int i = 0; i < slots.Count; i++) _assigned[slots[i]] = i + 1;
        QueueRedraw();
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), new("#090e09"));
        if (_texture is null) return;
        var textureSize = _texture.GetSize();
        float scale = Mathf.Min(Size.X / textureSize.X, Size.Y / textureSize.Y);
        var map = new Rect2((Size - textureSize * scale) / 2, textureSize * scale);
        DrawTextureRect(_texture, map, false);
        DrawRect(map, new("#71815a"), filled: false);
        foreach (var obj in _objects)
        {
            var point = map.Position + obj * map.Size;
            DrawRect(new Rect2(point - Vector2.One * 2, Vector2.One * 4), Sandbox.TeamMarkerColor(0));
        }
        var font = GetThemeDefaultFont();
        for (int i = 0; i < _points.Length; i++)
        {
            uint owner = (uint)_assigned[i];
            var center = map.Position + _points[i] * map.Size;
            var label = (i + 1).ToString("00");
            var badge = new Rect2(center - new Vector2(15, 11), new(30, 22));
            var ink = Sandbox.TeamMarkerColor(owner);
            DrawRect(badge, new("#121a13"));
            DrawRect(badge, ink, filled: false, width: 1.5f);
            float textWidth = font.GetStringSize(label, fontSize: 13).X;
            DrawString(font, center + new Vector2(-textWidth / 2, 5), label, fontSize: 13, modulate: ink);
        }
    }
}
