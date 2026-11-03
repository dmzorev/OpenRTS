using System.ComponentModel.DataAnnotations;
using System.Globalization;
using DeclarativeContent;

int passed = 0;
void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
void Check(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
DefinitionSet Load(string yaml) => DefinitionSet.Load([new("test.yaml", yaml)]);
void Reject(Action action, params string[] fragments)
{
    try { action(); }
    catch (ContentException e) { foreach (var part in fragments) Check(e.Message.Contains(part), e.Message + " missing " + part); return; }
    throw new Exception("Expected ContentException");
}
ContentNode Body(string yaml) => Load("entity:\n" + yaml).All["entity"];
var baseText = "^base:\n  Stats:\n    HP: 100\n    Speed: 4\nchild:\n  Stats:\n    HP: 200\n  Inherits: ^base\n";
Test("Local overrides win even when Inherits is written last; origin survives", () => {
    var set = Load(baseText); var child = set.All["child"].Fields!["Stats"].Fields!;
    Check(child["HP"].Scalar == "200" && child["Speed"].Scalar == "4");
    Check(child["HP"].Source.Line == 7 && child["Speed"].Source.Line == 4);
    Check(set.Concrete.Count() == 1 && set.All["^base"].Fields!["Stats"].Fields!["HP"].Scalar == "100");
});
Test("Cross-file inheritance and multiple-parent precedence", () => {
    var set = DefinitionSet.Load([new("a.yaml", "^a:\n  Stats: {HP: 100, Speed: 4}\n"),
        new("b.yaml", "^b:\n  Stats: {HP: 300}\nchild:\n  Inherits: ^a\n  Inherits@B: ^b\n")]);
    var stats = set.All["child"].Fields!["Stats"].Fields!;
    Check(stats["HP"].Scalar == "300" && stats["Speed"].Scalar == "4" && stats["HP"].Source.File == "b.yaml");
});
Test("Removal, nested removal, and independent named instances", () => {
    var set = Load("^a:\n  Gun@left: {Damage: 5, Ammo: 3}\n  Gun@right: {Damage: 7}\nx:\n  Inherits: ^a\n  -Gun@right:\n  Gun@left:\n    -Ammo:\n    Damage: 9\n");
    var x = set.All["x"].Fields!;
    Check(x.Count == 1 && x["Gun@left"].Fields!.Count == 1 && x["Gun@left"].Fields!["Damage"].Scalar == "9");
});
Test("Sequences replace instead of silently concatenate", () => {
    var set = Load("^a:\n  Stats: {Values: [one, two]}\nx:\n  Inherits: ^a\n  Stats: {Values: []}\n");
    Check(set.All["x"].Fields!["Stats"].Fields!["Values"].Items!.Count == 0);
});
Test("Quoted # and colon, Unicode, comments, block text", () => {
    var x = Body("  Text: 'Орион #2: корабль' # comment\n  More: |\n    first\n    second\n");
    Check(x.Fields!["Text"].Scalar == "Орион #2: корабль" && x.Fields["More"].Scalar == "first\nsecond\n");
});
Test("Unknown parent gives location", () => Reject(() => Load("x:\n  Inherits: missing\n"), "test.yaml:2", "missing"));
Test("Cycles show inheritance chain", () => Reject(() => Load("a: {Inherits: b}\nb: {Inherits: a}\n"), "Цикл", "a", "b"));
Test("Duplicate definitions across files rejected", () => Reject(() => DefinitionSet.Load([new("a", "x: {}"), new("b", "x: {}")]), "Повторное", "a"));
Test("Duplicate YAML fields rejected", () => Reject(() => Load("x:\n  HP: 1\n  HP: 2\n"), "test.yaml"));
Test("Missing removal is an error", () => Reject(() => Load("x:\n  -Absent:\n"), "Absent"));
Test("Anchors and aliases rejected deliberately", () => Reject(() => Load("x: &x {HP: 1}\ny: *x\n"), "Anchors"));
Test("Custom CLR tags rejected", () => Reject(() => Load("x: !SomeType {}\n"), "tags"));
Test("Tabs and multiple documents rejected", () => {
    Reject(() => Load("x:\n\tHP: 1\n"), "test.yaml");
    Reject(() => Load("x: {}\n---\ny: {}\n"), "один YAML");
});
Test("Missing and unknown option fields fail loudly", () => {
    Reject(() => Options.Bind<Stats>(Body("  Typo: 2\n"), "entity.Stats"), "Typo", "HP");
    Reject(() => Options.Bind<Stats>(Body("  Speed: 2\n"), "entity.Stats"), "HP", "Обязательное");
});
Test("Validation uses invariant finite numbers and strict named enums", () => {
    var culture = CultureInfo.CurrentCulture;
    try { CultureInfo.CurrentCulture = new("ru-RU"); Check(Options.Bind<Stats>(Body("  HP: 4\n  Speed: 1.5\n  Mode: Fast\n"), "entity").Speed == 1.5); }
    finally { CultureInfo.CurrentCulture = culture; }
    Reject(() => Options.Bind<Stats>(Body("  HP: -1\n"), "entity"), "HP");
    Reject(() => Options.Bind<Stats>(Body("  HP: 2\n  Speed: NaN\n"), "entity"), "Speed");
    Reject(() => Options.Bind<Stats>(Body("  HP: 2\n  Mode: 1\n"), "entity"), "Fast");
});
Test("Named traits get distinct mutable instances per entity", () => {
    var registry = new TraitRegistry<int>().Register<Stats>("Stats", (_, _, s) => s, multiple: true);
    var plan = registry.Compile("x", Body("  Stats@one: {HP: 5}\n  Stats@two: {HP: 6}\n"));
    var a = plan.Create(0); var b = plan.Create(0);
    a.All<Stats>().First().HP = 99;
    Check(b.All<Stats>().First().HP == 5 && a.Entries[1].Key == "Stats@two");
});
Test("Required traits, singleton multiplicity and unknown trait checks", () => {
    var registry = new TraitRegistry<int>().Register<Stats>("Stats", (_, _, o) => o, required: true);
    Reject(() => registry.Compile("x", Load("x: {}").All["x"]), "Stats");
    Reject(() => registry.Compile("x", Body("  Typo: {}\n")), "Typo");
    Reject(() => registry.Compile("x", Body("  Stats@a: {HP: 1}\n  Stats@b: {HP: 2}\n")), "один экземпляр");
});
Test("Factories follow dependencies, not YAML order", () => {
    var registry = new TraitRegistry<List<string>>()
        .Register<Stats>("Dependent", (l, _, o) => { Check(l.Contains("Base")); l.Add("Dependent"); return o; }, requires: ["Base"])
        .Register<Stats>("Base", (l, _, o) => { l.Add("Base"); return o; });
    var trace = new List<string>(); registry.Compile("x", Body("  Dependent: {HP: 2}\n  Base: {HP: 1}\n")).Create(trace);
    Check(trace.SequenceEqual(new[] { "Base", "Dependent" }));
    Reject(() => registry.Compile("x", Body("  Dependent: {HP: 2}\n")), "Base");
});
Test("Trait dependency cycles rejected before spawning", () => {
    var registry = new TraitRegistry<int>().Register<Stats>("A", (_, _, o) => o, requires: ["B"])
        .Register<Stats>("B", (_, _, o) => o, requires: ["A"]);
    Reject(() => registry.Compile("x", Body("  A: {HP: 1}\n  B: {HP: 1}\n")), "Цикл зависимостей");
});
Test("Generated schema contains enums, limits and field help", () => {
    var schema = ContentSchema.Generate(new Dictionary<string, Type> { ["Stats"] = typeof(Stats) }).ToJsonString();
    Check(schema.Contains("Fast") && schema.Contains("minimum") && schema.Contains("additionalProperties"));
});
Console.WriteLine($"{passed} DeclarativeContent checks passed.");

public enum Mode { Slow, Fast }
public sealed class Stats
{
    [Required, Range(1, 100)] public int HP { get; set; }
    public double Speed { get; set; }
    public Mode Mode { get; set; }
}
