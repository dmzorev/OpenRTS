using DeclarativeContent;
using OpenRTS.Content;
using OpenRTS.Core;

internal static class DefinitionChecks
{
    private const string Gameplay = """
        target: {Health: {HP: 100}, Buildable: {Ticks: 50, Cost: 80}}
        actor:
          Health: {HP: 100}
          Armaments: {Mounts: {grenade: {Weapon: grenade}}}
          TaskQueues: {Queues: {units: {Capacity: 20}}}
          Presentation: {Model: arbitrary, Animations: [idle, move, fire]}
          Actions:
            Definitions:
              grenade: {Mount: grenade, GroundTarget: true}
              train: {Unit: target, Ticks: 3, Cost: 7}
        """;
    private static UnitCatalog Load(string yaml) => UnitCatalog.Load([new("definitions", yaml)], weapons: LocomotionChecks.Weapons());
    private static void Check(bool value, string message) => NavigationChecks.Check(value, message);

    public static void Run(Action<string, Action> test)
    {
        test("Definitions: capabilities can describe nonspatial data without fake health or presentation", () =>
        {
            var definition = new EntityDefinition("participant-settings");
            using var store = new Frent.World();
            var entity = store.Create(definition);
            Check(ReferenceEquals(entity.Get<EntityDefinition>(), definition) && definition.Health is null && definition.Spatial is null && definition.Presentation is null, "Recipe forced spatial-unit defaults");
            NavigationChecks.Reject<ArgumentException>(() => new UnitDefinition(definition));
        });
        test("Definitions: unit facade and composed recipe preserve immutable physical and semantic capabilities", () =>
        {
            var names = new[] { "move", "idle" };
            var queues = new List<TaskQueueDefinition> { new("units") };
            var recipe = new EntityDefinition("unit", new(120, ArmorKind.Mechanical), new(body: BodyShape.Circle(384)),
                new(names), production: new(queues));
            var unit = new UnitDefinition(recipe);
            names[0] = "changed"; queues.Clear();
            Check(ReferenceEquals(unit.Definition, recipe) && unit.MaxHealth == 120 && unit.Body.HalfWidth == 384 && unit.Animations.SequenceEqual(new[] { "idle", "move" }) && unit.TaskQueues.Count == 1, "Definition retained mutable caller collections or facade lost its recipe");
        });
        test("Definitions: missing capability dependencies fail during preparation", () =>
        {
            var mounts = new ArmamentDefinition([new("main", new WeaponDefinition(1000, 1000, 1, 1), "fire")]);
            NavigationChecks.Reject<ArgumentException>(() => new EntityDefinition("bad", armament: mounts, presentation: new(["fire"])));
            NavigationChecks.Reject<ArgumentException>(() => new EntityDefinition("bad", spatial: new(), armament: mounts));
            NavigationChecks.Reject<ArgumentException>(() => new EntityDefinition("bad", production: new([new("q")], [new("train", "u", "q")])));
            NavigationChecks.Reject<ArgumentException>(() => new EntityDefinition("bad", production: new([new("q", workingState: "work")])));
            NavigationChecks.Reject<ArgumentException>(() => new ProducerDefinition([new("q")], [new("train", "u", "missing")]));
        });
        test("Definitions: removing or changing a panel leaves actions and gameplay fingerprint unchanged", () =>
        {
            string panel = "\n  ActionPanel:\n    Buttons:\n      grenade: {Label: Throw, Hotkey: A, Cursor: grenade, Order: 1}\n      train: {Label: Train, Hotkey: Q, Order: 0}\n";
            var hidden = Load(Gameplay); var visible = Load(Gameplay + panel);
            var other = Load(Gameplay + panel.Replace("Throw", "Grenade").Replace("Hotkey: A", "Hotkey: E").Replace("Order: 1", "Order: -1"));
            Check(hidden.Summary == visible.Summary && hidden.Summary == other.Summary, "UI affected gameplay fingerprint");
            Check(hidden.Units[0].Abilities.Count == 1 && hidden.Units[0].Products.Count == 1 && hidden.Presentations[0].Actions!.Count == 0, "Hidden panel removed gameplay actions");
            Check(visible.Presentations[0].Actions!.Select(b => b.Id).SequenceEqual(new[] { "train", "grenade" }), "Panel order was not applied independently");
        });
        test("Definitions: hidden actions execute through the public command boundary", () =>
        {
            var catalog = Load(Gameplay);
            using var world = new SimulationWorld(catalog.Units, 20000, 12000, players: [new(1, "blue", 10000)]);
            world.AddInitialUnit(0, new(4096, 4096), 1);
            world.Step([new(1, 0, 1, CommandKind.EnqueueTask, new(1), Action: "train"), new(1, 0, 2, CommandKind.UseAbility, new(1), new(8192, 4096), Action: "grenade")]);
            Check(world.TaskCount == 1 && !world.Events.ToArray().Any(e => e.Kind == WorldEventKind.ActionUnavailable), "Action required a UI button");
            bool fired = false;
            for (int i = 0; i < 7; i++) { world.Step(); fired |= world.Events.ToArray().Any(e => e.Kind == WorldEventKind.WeaponFired); }
            Check(fired && world.Count == 2 && world.TaskCount == 0, "Hidden ability or production recipe was not executed");
        });
        test("Definitions: invalid action references and fields fail before a match", () =>
        {
            foreach (string yaml in new[]
            {
                Gameplay + "\n  ActionPanel: {Buttons: {missing: {}}}",
                Gameplay.Replace("Mount: grenade, GroundTarget: true", "Mount: absent"),
                Gameplay.Replace("Unit: target", "Unit: missing"),
                Gameplay.Replace("Unit: target", "Unit: target, Queue: missing"),
                Gameplay.Replace("GroundTarget: true", "Label: invalid"),
                Gameplay + "\n  ActionPanel: {Buttons: {train: {Cost: 1}}}",
                Gameplay + "\n  ActionPanel: {Buttons: {train: {Hotkey: A}, grenade: {Hotkey: a}}}",
                Gameplay.Replace("Mount: grenade, GroundTarget: true", "Mount: grenade, Unit: target"),
                Gameplay.Replace("Unit: target", "Unit: target, AutoCast: true"),
                Gameplay.Replace("Ticks: 50, Cost: 80", "Ticks: 50, Kind: PlaceBuilding"),
                Gameplay.Replace("Mount: grenade, GroundTarget: true", "Mount: grenade, Cost: 1")
            }) NavigationChecks.Reject<ContentException>(() => Load(yaml));
        });
        test("Definitions: legacy declarations keep gameplay and can use an independent replacement panel", () =>
        {
            string old = "unit: {Health: {HP: 100}, Buildable: {Ticks: 2}}\nfactory: {Health: {HP: 100}, TaskQueues: {Queues: {q: {}}}, Actions: {Buttons: {train: {Unit: unit, Label: Train, Hotkey: A}}}}";
            var a = Load(old);
            string modern = "unit: {Health: {HP: 100}, Buildable: {Ticks: 2}}\nfactory: {Health: {HP: 100}, TaskQueues: {Queues: {q: {}}}, Actions: {Definitions: {train: {Unit: unit}}}, ActionPanel: {Buttons: {train: {Label: Train, Hotkey: A}}}}";
            var c = Load(modern);
            Check(a.Summary == c.Summary, "Legacy/new declarations changed gameplay");
            Check(a.Presentations[0].Actions!.SequenceEqual(c.Presentations[0].Actions!), "Legacy panel mapping changed");
            var hidden = Load(old.Replace("Actions: {Buttons:", "ActionPanel: {Buttons: {}}, Actions: {Buttons:"));
            Check(hidden.Summary == a.Summary && hidden.Presentations[0].Actions!.Count == 0, "Replacement panel changed legacy gameplay");
            NavigationChecks.Reject<ContentException>(() => Load(old.Replace("Actions: {Buttons:", "Actions: {Definitions: {train: {Unit: unit}}, Buttons:")));
        });
        test("Definitions: shared recipes do not share mutable production state", () =>
        {
            var catalog = Load(Gameplay);
            using var a = new SimulationWorld(catalog.Units, 20000, 12000, players: [new(1, "blue", 10000)]);
            using var b = new SimulationWorld(catalog.Units, 20000, 12000, players: [new(1, "blue", 10000)]);
            a.AddInitialUnit(0, new(4096, 4096), 1); b.AddInitialUnit(0, new(4096, 4096), 1);
            a.Step([new(1, 0, 1, CommandKind.EnqueueTask, new(1), Action: "train")]); b.Step();
            Check(a.TaskCount == 1 && b.TaskCount == 0 && catalog.Units[0].MaxHealth == 100 && catalog.Units[0].TaskQueues.Count == 1, "Runtime state leaked into the shared definition");
        });
        test("Definitions: inherited actions survive panel removal and reject UI fields in gameplay templates", () =>
        {
            string yaml = """
                ^base:
                  Health: {HP: 100}
                  TaskQueues: {Queues: {q: {}}}
                  Actions: {Definitions: {train: {Unit: unit}}}
                  ActionPanel: {Buttons: {train: {Label: Train}}}
                factory:
                  Inherits: ^base
                  ActionPanel: {Buttons: {}}
                unit: {Health: {HP: 100}, Buildable: {Ticks: 5}}
                """;
            var catalog = Load(yaml);
            Check(catalog.Definitions.Count == catalog.Units.Count && ReferenceEquals(catalog.Definitions[0], catalog.Units[0].Definition) && catalog.Units[0].Products.Count == 1, "Inheritance lost compiled gameplay");
            Check(catalog.Presentations[0].Actions!.Count == 1, "Empty map unexpectedly erased inherited bindings");
            var hidden = Load(yaml.Replace("ActionPanel: {Buttons: {}}", "-ActionPanel:"));
            Check(hidden.Summary == catalog.Summary && hidden.Presentations[0].Actions!.Count == 0 && hidden.Units[0].Products.Count == 1, "Panel removal changed inherited gameplay");
            NavigationChecks.Reject<ContentException>(() => Load(yaml.Replace("Unit: unit", "Unit: unit, Label: Wrong layer")));
        });
    }
}
