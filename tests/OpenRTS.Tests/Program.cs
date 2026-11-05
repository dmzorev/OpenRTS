using DeclarativeContent;
using OpenRTS.Content;
using OpenRTS.Core;

string? filter = args.FirstOrDefault(a => a.StartsWith("--filter=", StringComparison.Ordinal))?[9..];
int passed = 0;
var failures = new List<Exception>();
void Check(bool value, string message) { if (!value) throw new Exception(message); }
void Test(string name, Action check)
{
    if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) return;
    try { check(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures.Add(error); Console.WriteLine($"FAIL {name}: {error.Message}"); }
}
void Reject<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
UnitCatalog Load(string text) => UnitCatalog.Load([new("units.yaml", text)]);

Test("Core definition rejects invalid IDs and health", () =>
{
    Reject<ArgumentException>(() => new UnitDefinition(" ", 10));
    Reject<ArgumentOutOfRangeException>(() => new UnitDefinition("scout", 0));
});
Test("Inherited content becomes typed gameplay definitions", () =>
{
    var catalog = Load("^base: {Health: {HP: 100}}\nscout: {Inherits: ^base}\ntank: {Inherits: ^base, Health: {HP: 300}}\n");
    Check(catalog.Units.Select(u => u.Id).SequenceEqual(["scout", "tank"]), "Stable ordinal order");
    Check(catalog.Units[0].MaxHealth == 100 && catalog.Units[1].MaxHealth == 300, "Inheritance");
});
Test("Fingerprint ignores file order and YAML formatting", () =>
{
    var first = Load("b: {Health: {HP: 20}}\na: {Health: {HP: 10}}\n");
    var second = UnitCatalog.Load([new("a", "a:\n  Health:\n    HP: 10\n"), new("b", "b: {Health: {HP: 20}}")]);
    Check(first.Summary == second.Summary, "Equivalent content must match");
    Check(first.Summary != Load("a: {Health: {HP: 11}}\nb: {Health: {HP: 20}}\n").Summary, "Changed gameplay data must differ");
});
Test("Invalid concrete and abstract definitions fail before use", () =>
{
    Reject<ContentException>(() => Load("x: {Health: {HP: 0}}"));
    Reject<ContentException>(() => Load("x: {Health: {Hp: 10}}"));
    Reject<ContentException>(() => Load("x: {}"));
    Reject<ContentException>(() => Load("^unused: {Health: {Typo: 10}}\nx: {Health: {HP: 1}}"));
    Reject<ArgumentException>(() => Load("^base: {Health: {HP: 1}}"));
});
PresentationChecks.Run(Test);
LocomotionChecks.Run(Test);
OrderChecks.Run(Test);
WeaponChecks.Run(Test);
FootprintChecks.Run(Test);
ActionProductionChecks.Run(Test);
DefinitionChecks.Run(Test);
EcsMigrationChecks.Run(Test);
EcsStorageChecks.Run(Test);
SimulationChecks.Run(Test);
ExactMovementChecks.Run(Test);
NavigationChecks.Run(Test);
GroupNavigationChecks.Run(Test);
PlaybackChecks.Run(Test);
GroupPlanningChecks.Run(Test);
Console.WriteLine($"{passed} OpenRTS checks passed; {failures.Count} failed.");
if (failures.Count > 0) throw new AggregateException(failures);
