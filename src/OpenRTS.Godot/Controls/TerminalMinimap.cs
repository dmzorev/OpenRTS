using System;
using System.Collections.Generic;
using Godot;
using OpenRTS.Content;
using OpenRTS.Core;
using OpenRTS.Core.Visibility;

namespace OpenRTS.Godot.Controls;

public partial class TerminalMinimap : Control
{
    private readonly List<(Vector2 Position, Color Color, bool Neutral)> _markers = [];
    private readonly List<(Vector2 Position, bool Identified)> _contacts = [];
    private NavigationGrid? _grid;
    private Texture2D? _terrain;
    private Rect2 _camera;
    private long _tick = -1;
    private bool _dragging;
    private Texture2D? _fog;
    private uint _fogViewer;

    public event Action<Vector2>? Navigate;
    public required Func<uint, Color> TeamColor { get; init; }

    public void UpdateMap(NavigationGrid grid, LocomotionCatalog locomotion, ReadOnlySpan<EntityPose> poses, long tick, Rect2 camera,
        ReadOnlySpan<ContactPose> contacts = default)
    {
        bool changed = !ReferenceEquals(_grid, grid);
        if (changed)
        {
            _grid = grid;
            _terrain = CreateTerrainTexture(grid, locomotion);
        }
        if (changed || _tick != tick)
        {
            _tick = tick;
            _markers.Clear();
            foreach (var pose in poses)
            {
                var color = TeamColor(pose.Presentation.Owner);
                _markers.Add((new Vector2(pose.Current.X, pose.Current.Y) / WorldPosition.UnitsPerCell, color,
                    pose.Presentation.Owner == 0));
            }
            _contacts.Clear();
            foreach (var contact in contacts)
            {
                var knowledge = contact.Knowledge;
                _contacts.Add((new Vector2(knowledge.Position.X, knowledge.Position.Y) / WorldPosition.UnitsPerCell,
                    knowledge.UnitType.HasValue));
            }
            changed = true;
        }
        if (changed || _camera != camera) QueueRedraw();
        _camera = camera;
    }

    public void UpdateFog(FrameExchange.FrameLease frame, Texture2D? visual = null)
    {
        if (frame.FogCellSize == 0) return;
        if (visual is not null) { _fog = visual; if (_fogViewer != frame.Viewer) { _fogViewer = frame.Viewer; _tick = -1; } QueueRedraw(); return; }
        if (_fogViewer != frame.Viewer) { _fogViewer = frame.Viewer; _tick = -1; }
        using var image = Image.CreateEmpty(frame.FogColumns, frame.FogRows, false, Image.Format.Rgba8);
        for (int y = 0; y < frame.FogRows; y++) for (int x = 0; x < frame.FogColumns; x++)
        {
            var state = frame.FogStates[y * frame.FogColumns + x];
            image.SetPixel(x, y, new Color(0, 0, 0, state == OpenRTS.Core.Visibility.CellVisibility.Unknown ? 1 : state == OpenRTS.Core.Visibility.CellVisibility.Explored ? 0.65f : 0));
        }
        if (_fog is ImageTexture texture) texture.Update(image); else _fog = ImageTexture.CreateFromImage(image);
        QueueRedraw();
    }

    internal static Texture2D CreateTerrainTexture(NavigationGrid grid, LocomotionCatalog locomotion)
    {
        using var image = Image.CreateEmpty(grid.Width, grid.Height, false, Image.Format.Rgb8);
        for (int y = 0; y < grid.Height; y++)
        for (int x = 0; x < grid.Width; x++)
            image.SetPixel(x, y, TerrainColor(locomotion.Terrains[grid.TerrainAt(y * grid.Width + x)]));
        return ImageTexture.CreateFromImage(image);
    }

    private static Color TerrainColor(string id) => id switch
    {
        "water" => new("#285567"),
        "road" => new("#737668"),
        "forest" => new("#243c21"),
        "rock" or "mountains" => new("#565143"),
        "mud" => new("#544431"),
        "swamp" => new("#3c5244"),
        _ => new("#46592e")
    };

    private Vector2 MapSize => new Vector2(_grid!.Width, _grid.Height) * (_grid.CellSize / (float)WorldPosition.UnitsPerCell);

    private Rect2 MapRect()
    {
        var world = MapSize;
        float scale = Mathf.Min(Size.X / world.X, Size.Y / world.Y);
        return new((Size - world * scale) / 2, world * scale);
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), new Color("#090e09"));
        if (_terrain is null) return;
        var map = MapRect();
        if (!map.HasArea()) return;
        var scale = map.Size / MapSize;
        DrawTextureRect(_terrain, map, false);
        if (_fog is not null) DrawTextureRect(_fog, map, false);
        foreach (var (world, color, neutral) in _markers)
        {
            var point = map.Position + world * scale;
            var marker = new Rect2(point - Vector2.One * 2, Vector2.One * 4);
            if (neutral) DrawRect(marker, color, false);
            else DrawRect(marker, color);
        }
        var radar = new Color("#ffcc6e");
        foreach (var (world, identified) in _contacts)
        {
            var point = map.Position + world * scale;
            if (identified) DrawRect(new Rect2(point - Vector2.One * 3, Vector2.One * 6), radar, false, 1.5f);
            else DrawCircle(point, 2.5f, radar);
        }
        var camera = new Rect2(map.Position + _camera.Position * scale, _camera.Size * scale);
        var visible = map.Intersection(camera);
        if (visible.HasArea()) DrawRect(visible, new Color("#f3e5b5"), false, 1.5f);
    }

    public override void _GuiInput(InputEvent input)
    {
        if (_grid is null) return;
        var map = MapRect();
        if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left } click)
        {
            _dragging = click.Pressed && map.HasPoint(click.Position);
            if (_dragging) NavigateTo(click.Position);
            AcceptEvent();
        }
        else if (_dragging && input is InputEventMouseMotion move)
        {
            _dragging = (move.ButtonMask & MouseButtonMask.Left) != 0;
            if (_dragging) NavigateTo(move.Position);
            AcceptEvent();
        }
    }

    private void NavigateTo(Vector2 point)
    {
        var map = MapRect();
        if (map.HasArea()) Navigate?.Invoke(((point - map.Position) / map.Size).Clamp(Vector2.Zero, Vector2.One) * MapSize);
    }
}
