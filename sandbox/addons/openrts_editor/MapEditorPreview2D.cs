#if TOOLS
using Godot;
using OpenRTS.Content;
using OpenRTS.Core;
using OpenRTS.Core.Navigation;

namespace OpenRTS.Godot.Authoring;

[Tool]
internal partial class MapEditorPreview2D : Node2D
{
    private readonly record struct Sprite(Texture2D Texture, Rect2 Rect, Transform2D Transform, Color Modulate);
    private readonly record struct Line(Vector2 From, Vector2 To, Color Color, float Width = 1.5f);
    private readonly List<Sprite> _sprites = [];
    private readonly List<Line> _lines = [];
    private readonly List<(Vector2 Position, int Elevation)> _heights = [];
    private readonly Dictionary<string, SpriteFrames> _frames = new(StringComparer.Ordinal);
    private Node2D? _modelCanvas;
    private MapProjection2D _projection;
    private IReadOnlyList<MapReliefDiagnostics.Edge> _edges = [];
    private Vector2I? _focus;
    internal int SpriteCount => _sprites.Count;
    internal int GeometryCount => _lines.Count;
    internal WorldPosition[] StartPositions { get; private set; } = [];
    internal int[] StartElevations { get; private set; } = [];

    public override void _Ready()
    {
        // Replace the same magenta team mask as the world sprite shader, on the sprite canvas only.
        var shader = new Shader { Code = """
            shader_type canvas_item;
            varying vec4 paint;
            void vertex() { paint = COLOR; }
            void fragment() {
                vec4 texel = texture(TEXTURE, UV);
                float light = max(texel.r, texel.b);
                float mask = smoothstep(0.12, 0.35, (min(texel.r, texel.b) - texel.g) / max(light, 0.001));
                COLOR = vec4(mix(texel.rgb, paint.rgb * mix(0.52, 1.08, light), mask), texel.a * paint.a);
            }
            """ };
        _modelCanvas = new Node2D { Material = new ShaderMaterial { Shader = shader }, ShowBehindParent = true };
        _modelCanvas.Draw += DrawModels;
        AddChild(_modelCanvas, false, InternalMode.Front);
    }

    internal void Reset(MapProjection2D projection)
    {
        _projection = projection;
        _sprites.Clear(); _lines.Clear(); _heights.Clear();
        _edges = [];
        StartPositions = []; StartElevations = [];
        _modelCanvas?.QueueRedraw();
        QueueRedraw();
    }

    internal void Apply(MapSource source, NavigationGrid grid, UnitCatalog catalog, ModelCatalog models,
        StartingSet? starts, IReadOnlySet<string>? selectedStarts)
    {
        var types = catalog.Units.Select((unit, i) => (unit.Id, i)).ToDictionary(p => p.Id, p => p.i, StringComparer.Ordinal);
        var startPositions = new List<WorldPosition>();
        var startElevations = new List<int>();
        var owners = source.Slots.OrderBy(slot => slot.Id, StringComparer.Ordinal)
            .Select((slot, i) => (slot.Id, Paint: i == 0 ? new Color(0.25f, 0.55f, 1) : i == 1 ? new Color(1, 0.25f, 0.2f) : Colors.White))
            .ToDictionary(pair => pair.Id, pair => pair.Paint, StringComparer.Ordinal);
        var objects = new List<(string Type, WorldPosition Point, int Turns, bool Start, Color Paint)>();
        var resolvers = new Dictionary<Locomotor, DestinationResolver>();
        foreach (var obj in source.Objects)
            objects.Add((UnitCatalog.MapTypeId(source.Id, obj), obj.Position, obj.QuarterTurns, false, owners.GetValueOrDefault(obj.Owner, Colors.White)));
        foreach (var slot in source.Slots)
        {
            if (slot.Position is not { } anchor) continue;
            Cross(anchor, new(0.85f, 0.85f, 1));
            if (starts is null || selectedStarts is not null && !selectedStarts.Contains(slot.Id)) continue;
            foreach (var unit in starts.Units)
            {
                var point = unit.At(anchor, slot.Reverse);
                startPositions.Add(point);
                startElevations.Add(InBounds(point) ? grid.SurfaceElevationAt(point) : 0);
                objects.Add((unit.Type, point, 0, true, owners[slot.Id]));
            }
        }
        StartPositions = startPositions.ToArray();
        StartElevations = startElevations.ToArray();
        foreach (var (id, point, turns, start, paint) in objects.OrderBy(entry => _projection.Project(entry.Point).Y))
        {
            if (!types.TryGetValue(id, out int index) || turns is < 0 or > 3)
            {
                Cross(point, Colors.OrangeRed);
                continue;
            }
            var type = catalog.Units[index].WithRotation(turns);
            bool valid = InBounds(point) && (type.Footprint is { } structure ? structure.AllowsConstruction(grid, point, type.Locomotor) :
                type.Flight is not null || Resolver(type.Locomotor).CanStop(point));
            if (grid.HasSurface && InBounds(point) && grid.SurfaceElevationAt(point) is var elevation && elevation != 0)
                _heights.Add((_projection.Project(point) + new Vector2(5, -4), elevation));
            var tint = new Color(paint, start ? 0.55f : valid ? 1 : 0.8f);
            if (catalog.Presentations[index].Model is { } model && models.Models.TryGetValue(model, out var definition))
                AddModel(definition, type, point, turns, tint, models.ScaleFor(type));
            AddLines(point, MapPreviewGeometry.Body(type.Body), !valid ? Colors.OrangeRed : Colors.Cyan);
            if (type.Footprint is not { } footprint) continue;
            foreach (var cell in footprint.Cells)
            {
                var box = MapPreviewGeometry.Cell(footprint, cell);
                AddLines(point, box, !valid ? Colors.OrangeRed : cell.Navigation.Traverse == false
                    ? new(0.36f, 0.58f, 1) : cell.Navigation.CanStop == false ? Colors.Orange : Colors.LightGreen);
                if (!cell.Targetable) AddLines(point, [box[0], box[3]], Colors.HotPink);
            }
        }
        _modelCanvas?.QueueRedraw();
        QueueRedraw();

        bool InBounds(WorldPosition point) => point.X >= 0 && point.Y >= 0 &&
            point.X < grid.Width * grid.CellSize && point.Y < grid.Height * grid.CellSize;
        DestinationResolver Resolver(Locomotor profile)
        {
            if (!resolvers.TryGetValue(profile, out var resolver)) resolvers.Add(profile, resolver = new(grid, profile));
            return resolver;
        }
    }

    private void AddModel(ModelDefinition model, UnitDefinition type, WorldPosition point, int turns, Color tint, float scale)
    {
        var idle = model.Animations[type.Animations[type.IdleAnimation]];
        float yaw = turns * Mathf.Pi / 2;
        var basis = new Transform2D(_projection.Column, _projection.Row, _projection.Project(point));
        Span<Vector2> pivots = stackalloc Vector2[model.Parts.Count];
        for (int i = 0; i < model.Parts.Count; i++)
        {
            var part = model.Parts[i];
            pivots[i] = (part.Parent < 0 ? Vector2.Zero : pivots[part.Parent]) +
                new Vector2((float)part.OffsetX, (float)part.OffsetY).Rotated(yaw) * scale;
        }
        foreach (int i in Enumerable.Range(0, model.Parts.Count).OrderBy(i => model.Parts[i].Layer))
        {
            var part = model.Parts[i];
            if (part.VisibleWhen is { Count: > 0 } || !idle.Parts.Contains(part.Id)) continue;
            if (!_frames.TryGetValue(part.Resource, out var frames))
            {
                frames = ResourceLoader.Load<SpriteFrames>(part.Resource, cacheMode: ResourceLoader.CacheMode.Ignore);
                if (frames is null) throw new ArgumentException("Missing SpriteFrames: " + part.Resource);
                _frames.Add(part.Resource, frames);
            }
            string sequence = idle.Sequence;
            if (part.Facings > 1 && !frames.HasAnimation(sequence))
                sequence += "-" + (int)MathF.Round(turns * part.Facings / 4f) % part.Facings;
            if (!frames.HasAnimation(sequence) || frames.GetFrameCount(sequence) == 0) continue;
            if (frames.GetFrameTexture(sequence, 0) is not { } texture) continue;
            var size = new Vector2((float)part.Width, (float)part.Height) * scale;
            var rect = new Rect2(-new Vector2((float)part.PivotX, (float)part.PivotY) * size, size);
            float rotation = (part.Facings == 0 ? yaw : 0) + Mathf.DegToRad((float)part.SpriteRotation);
            _sprites.Add(new(texture, rect, basis * new Transform2D(rotation, pivots[i]), tint));
        }
    }

    private void AddLines(WorldPosition origin, Vector3[] points, Color color)
    {
        for (int i = 0; i < points.Length; i += 2) _lines.Add(new(Project(points[i]), Project(points[i + 1]), color));
        Vector2 Project(Vector3 point) => _projection.Project(new(origin.X + Mathf.RoundToInt(point.X * 1024), origin.Y + Mathf.RoundToInt(point.Z * 1024)));
    }

    private void Cross(WorldPosition point, Color color)
    {
        Cross(_projection.Project(point), color);
    }

    private void Cross(Vector2 center, Color color)
    {
        _lines.Add(new(center - new Vector2(5, 0), center + new Vector2(5, 0), color));
        _lines.Add(new(center - new Vector2(0, 5), center + new Vector2(0, 5), color));
    }
    internal void MarkInvalid(Vector2 point)
    {
        Cross(point, Colors.OrangeRed);
        QueueRedraw();
    }

    internal void ShowRelief(IReadOnlyList<MapReliefDiagnostics.Edge> edges, Vector2I? focus = null)
    {
        _edges = edges; _focus = focus;
        QueueRedraw();
    }

    private void DrawModels()
    {
        foreach (var sprite in _sprites)
        {
            _modelCanvas!.DrawSetTransformMatrix(sprite.Transform);
            _modelCanvas.DrawTextureRect(sprite.Texture, sprite.Rect, false, sprite.Modulate);
        }
    }

    public override void _Draw()
    {
        foreach (var line in _lines) DrawLine(line.From, line.To, line.Color, line.Width, true);
        foreach (var (point, elevation) in _heights)
            DrawString(ThemeDB.FallbackFont, point, $"z {elevation}", fontSize: 12, modulate: new Color(1, 0.89f, 0.65f));
        foreach (var edge in _edges)
        {
            var color = edge.Ramp ? Colors.OrangeRed : new Color(0.42f, 0.75f, 1, 0.74f);
            DrawLine(_projection.Project(edge.From), _projection.Project(edge.To), color, edge.Cell == _focus ? 4 : 2, true);
        }
        if (_focus is { } cell)
        {
            var top = _projection.Origin + _projection.Column * cell.X + _projection.Row * cell.Y;
            var right = top + _projection.Column;
            var bottom = right + _projection.Row;
            var left = top + _projection.Row;
            DrawColoredPolygon([top, right, bottom, left], new(1, 0.62f, 0.15f, 0.18f));
            DrawPolyline([top, right, bottom, left, top], Colors.Orange, 2, true);
        }
    }
}
#endif
