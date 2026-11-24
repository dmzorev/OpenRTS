using Godot;
using OpenRTS.Core;
using OpenRTS.Core.Navigation;

namespace OpenRTS.Godot.Rendering;

internal sealed class TerrainSurface
{
    private readonly record struct Face(Vector3 A, Vector3 B, Vector3 C, int Cell);
    private sealed record FaceGroup(Aabb Bounds, int Start, int End, FaceGroup? Left = null, FaceGroup? Right = null);
    private readonly NavigationGrid _grid;
    private readonly Face[] _faces;
    private readonly FaceGroup _faceIndex;
    public ArrayMesh Mesh { get; }

    public TerrainSurface(NavigationGrid grid, Func<int, int, Texture2D>? textureAt = null, Texture2D? cliff = null,
        Func<int, int, Texture2D?>? upperTextureAt = null)
    {
        _grid = grid;
        int size = grid.CellSize;
        var faces = new List<Face>(2 * grid.CellCount);
        using var untextured = Builder(null);
        using var untexturedCliffs = cliff is null ? Builder(null, doubleSided: true) : null;
        var builders = new Dictionary<Texture2D, SurfaceTool>();
        bool hasUntextured = false, hasCliffs = false;
        Mesh = new ArrayMesh();
        try
        {
            for (int y = 0; y < grid.Height; y++)
                for (int x = 0; x < grid.Width; x++)
                {
                    int cell = grid.Index(new(x, y)), left = x * size, top = y * size, right = left + size, bottom = top + size;
                    var cut = grid.SurfaceCutAt(cell);
                    bool rising = grid.SurfaceRiseAt(cell) != default;
                    bool raised = grid.SurfaceAt(cell).Elevation > 0;
                    var texture = textureAt?.Invoke(x, y);
                    var color = texture is null ? rising ? new Color("#64854c") : raised ? new Color("#527256") : new Color("#394950") :
                        raised && !rising ? new Color("#c3cdaf") : Colors.White;
                    var surface = Surface(texture);
                    if (cut == default)
                    {
                        var a = Vertex(cell, left, top);
                        var b = Vertex(cell, right, top);
                        var c = Vertex(cell, left, bottom);
                        var d = Vertex(cell, right, bottom);
                        Add(surface, a, b, c, cell, color);
                        Add(surface, b, d, c, cell, color);
                    }
                    else
                    {
                        int lower = grid.SurfaceAt(cell).Elevation;
                        var a = VertexAt(left, top, lower);
                        var b = VertexAt(right, top, lower);
                        var c = VertexAt(left, bottom, lower);
                        var d = VertexAt(right, bottom, lower);
                        var lift = Vector3.Up * ((cut.UpperElevation - lower) / (float)WorldPosition.UnitsPerCell);
                        var upperTexture = upperTextureAt?.Invoke(x, y);
                        var upperSurface = upperTexture is null ? surface : Surface(upperTexture);
                        if (cut.DescendingDiagonal)
                        {
                            Top(a, b, d, cut.UpperCorner == SurfaceCorner.NorthEast);
                            Top(a, d, c, cut.UpperCorner == SurfaceCorner.SouthWest);
                            AddCliff(a, d, a + lift, d + lift);
                        }
                        else
                        {
                            Top(a, b, c, cut.UpperCorner == SurfaceCorner.NorthWest);
                            Top(b, d, c, cut.UpperCorner == SurfaceCorner.SouthEast);
                            AddCliff(b, c, b + lift, c + lift);
                        }

                        void Top(Vector3 p, Vector3 q, Vector3 r, bool upper)
                        {
                            var offset = upper ? lift : Vector3.Zero;
                            var shade = upper && upperTexture is not null ? Colors.White :
                                upper && lift != Vector3.Zero ? color.Darkened(0.14f) : color;
                            Add(upper ? upperSurface : surface, p + offset, q + offset, r + offset, cell, shade);
                        }
                    }
                    if (x + 1 < grid.Width)
                        Join(cell, cell + 1, right, top, right, bottom);
                    if (y + 1 < grid.Height)
                        Join(cell, cell + grid.Width, left, bottom, right, bottom);
                }
            if (hasUntextured) Finish(untextured);
            if (hasCliffs && untexturedCliffs is not null) Finish(untexturedCliffs);
            foreach (var builder in builders.Values) Finish(builder);
        }
        finally
        {
            foreach (var builder in builders.Values) builder.Dispose();
        }
        _faces = faces.ToArray();
        _faceIndex = IndexFaces(0, _faces.Length);

        Vector3 Vertex(int cell, int x, int y) => VertexAt(x, y, grid.SurfaceElevationAt(cell, x, y));
        static Vector3 VertexAt(int x, int y, int elevation) => new Vector3(x, elevation, y) / WorldPosition.UnitsPerCell;
        void Join(int cell, int neighbor, int x0, int y0, int x1, int y1)
        {
            AddCliff(
                VertexAt(x0, y0, grid.SurfaceElevationAtEdge(cell, x0, y0, x1, y1)),
                VertexAt(x1, y1, grid.SurfaceElevationAtEdge(cell, x1, y1, x0, y0)),
                VertexAt(x0, y0, grid.SurfaceElevationAtEdge(neighbor, x0, y0, x1, y1)),
                VertexAt(x1, y1, grid.SurfaceElevationAtEdge(neighbor, x1, y1, x0, y0)));
        }

        SurfaceTool Surface(Texture2D? texture)
        {
            if (texture is null)
            {
                hasUntextured = true;
                return untextured;
            }
            if (!builders.TryGetValue(texture, out var builder))
            {
                builder = Builder(texture);
                builders.Add(texture, builder);
            }
            return builder;
        }

        void Finish(SurfaceTool builder)
        {
            builder.GenerateNormals();
            builder.Commit(Mesh);
        }

        void Add(SurfaceTool builder, Vector3 a, Vector3 b, Vector3 c, int cell, Color color, bool wallAlongX = false)
        {
            if (a == b || b == c || a == c) return;
            faces.Add(new(a, b, c, cell));
            builder.SetColor(color);
            Vertex(a); Vertex(b); Vertex(c);

            void Vertex(Vector3 point)
            {
                var uv = cell >= 0 ? new Vector2(point.X, point.Z)
                    : new Vector2((wallAlongX ? point.X : point.Z) * 0.5f, -point.Y * 0.5f);
                builder.SetUV(uv);
                builder.AddVertex(point);
            }
        }

        void AddCliff(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            float start = a.Y - c.Y, end = b.Y - d.Y;
            if (start == 0 && end == 0) return;
            hasCliffs = true;
            var surface = untexturedCliffs ?? Surface(cliff);
            bool alongX = a.X != b.X;
            var rock = cliff is null ? new Color("#a47351") : alongX ? new Color("#e3d6c3") : new Color("#b9b6ac");
            if (start * end < 0)
            {
                var crossing = a.Lerp(b, start / (start - end));
                Add(surface, a, c, crossing, -1, rock, alongX);
                Add(surface, crossing, d, b, -1, rock, alongX);
            }
            else
            {
                Add(surface, a, c, b, -1, rock, alongX);
                Add(surface, b, c, d, -1, rock, alongX);
            }
        }
    }

    private static SurfaceTool Builder(Texture2D? texture, bool doubleSided = false)
    {
        if (texture is AtlasTexture atlas)
        {
            if (atlas.Region.Size == Vector2.Zero && atlas.Margin == default) texture = atlas.Atlas;
            else
            {
                using var image = atlas.GetImage();
                texture = ImageTexture.CreateFromImage(image);
            }
        }
        var builder = new SurfaceTool();
        builder.Begin(global::Godot.Mesh.PrimitiveType.Triangles);
        builder.SetSmoothGroup(uint.MaxValue);
        if (texture is not null || doubleSided)
            builder.SetMaterial(new StandardMaterial3D
            {
                AlbedoTexture = texture,
                VertexColorUseAsAlbedo = true,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled
            });
        return builder;
    }

    public Vector3 NormalAt(WorldPosition point)
    {
        var rise = _grid.SurfaceRiseAt(_grid.CellAt(point));
        return new Vector3(-rise.X, _grid.CellSize, -rise.Y).Normalized();
    }

    private FaceGroup IndexFaces(int start, int end)
    {
        // Keep mesh order so coincident ground and cliff hits resolve as before.
        if (end - start > 16)
        {
            int middle = start + (end - start) / 2;
            var left = IndexFaces(start, middle);
            var right = IndexFaces(middle, end);
            return new(left.Bounds.Merge(right.Bounds), start, end, left, right);
        }

        var bounds = new Aabb(_faces[start].A, Vector3.Zero);
        for (int i = start; i < end; i++)
        {
            var face = _faces[i];
            bounds = bounds.Expand(face.A).Expand(face.B).Expand(face.C);
        }
        // Include shared edges despite float rounding in the triangle intersection.
        return new(bounds.Grow(1f / WorldPosition.UnitsPerCell), start, end);
    }

    private static bool IntersectsRay(Aabb bounds, Vector3 origin, Vector3 direction)
    {
        double enter = 0, exit = double.PositiveInfinity;
        var min = bounds.Position;
        var max = bounds.End;
        return Clip(origin.X, direction.X, min.X, max.X, ref enter, ref exit) &&
            Clip(origin.Y, direction.Y, min.Y, max.Y, ref enter, ref exit) &&
            Clip(origin.Z, direction.Z, min.Z, max.Z, ref enter, ref exit);

        static bool Clip(double origin, double direction, double min, double max, ref double enter, ref double exit)
        {
            if (direction == 0) return origin >= min && origin <= max;
            double a = (min - origin) / direction, b = (max - origin) / direction;
            enter = Math.Max(enter, Math.Min(a, b));
            exit = Math.Min(exit, Math.Max(a, b));
            return enter <= exit;
        }
    }

    public WorldPosition? Pick(Camera3D camera, Vector2 pixel)
    {
        var origin = camera.ProjectRayOrigin(pixel);
        var direction = camera.ProjectRayNormal(pixel);
        float nearest = float.PositiveInfinity;
        Vector3 hit = default;
        int cell = -1;
        TestFaces(_faceIndex);
        if (cell < 0) return null;

        int size = _grid.CellSize, left = cell % _grid.Width * size, top = cell / _grid.Width * size;
        // Keep rounding on the surface we hit even when the next cell starts at a cliff.
        int x = Math.Clamp((int)MathF.Round(hit.X * WorldPosition.UnitsPerCell), left, left + size - 1);
        int y = Math.Clamp((int)MathF.Round(hit.Z * WorldPosition.UnitsPerCell), top, top + size - 1);
        var cut = _grid.SurfaceCutAt(cell);
        int elevation = _grid.SurfaceElevationAt(cell, x, y);
        if (cut == default) return new WorldPosition(x, y, elevation);
        int pickedHeight = hit.Y * WorldPosition.UnitsPerCell > (cut.UpperElevation + _grid.SurfaceAt(cell).Elevation) * 0.5f
            ? cut.UpperElevation : _grid.SurfaceAt(cell).Elevation;
        if (elevation == pickedHeight) return new WorldPosition(x, y, elevation);

        // Rounding adjacent to the diagonal can land on the other triangle.
        WorldPosition? corrected = null;
        float error = float.PositiveInfinity;
        foreach (var offset in new[] { Vector2I.Left, Vector2I.Right, Vector2I.Up, Vector2I.Down })
        {
            int px = x + offset.X, py = y + offset.Y;
            if ((uint)(px - left) >= size || (uint)(py - top) >= size ||
                _grid.SurfaceElevationAt(cell, px, py) != pickedHeight) continue;
            float distance = new Vector2(px, py).DistanceSquaredTo(new Vector2(hit.X, hit.Z) * WorldPosition.UnitsPerCell);
            if (distance >= error) continue;
            corrected = new(px, py, pickedHeight);
            error = distance;
        }
        return corrected;

        void TestFaces(FaceGroup group)
        {
            if (!IntersectsRay(group.Bounds, origin, direction)) return;
            if (group.Left is { } left && group.Right is { } right)
            {
                TestFaces(left);
                TestFaces(right);
                return;
            }
            for (int i = group.Start; i < group.End; i++)
            {
                var face = _faces[i];
                var intersection = Geometry3D.RayIntersectsTriangle(origin, direction, face.A, face.B, face.C);
                if (intersection.VariantType != Variant.Type.Vector3) continue;
                var point = intersection.AsVector3();
                float distance = origin.DistanceSquaredTo(point);
                if (distance > nearest || distance == nearest && cell >= 0) continue;
                nearest = distance;
                hit = point;
                cell = face.Cell;
            }
        }
    }
}
