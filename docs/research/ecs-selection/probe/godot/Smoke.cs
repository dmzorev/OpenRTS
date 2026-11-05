using Frent;
using Frent.Systems;
using Godot;

public partial class Smoke : Node
{
    public override void _Ready()
    {
        using var world = new World();
        var entity = world.Create(new Position { X = 1024 }, new Velocity { X = 256 });
        foreach (var (position, velocity) in world.Query<Position, Velocity>())
            position.Value.X += velocity.Value.X;
        var success = entity.Get<Position>().X == 1280;
        GD.Print(success ? "PASS: Godot4.7.2/net10 Frent0.9.1-beta query" : "FAIL: Frent query");
        GetTree().Quit(success ? 0 : 1);
    }

    struct Position { public int X; }
    struct Velocity { public int X; }
}
