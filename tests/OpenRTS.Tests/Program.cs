using DeclarativeContent;
using OpenRTS.Content;
using OpenRTS.Core;

int passed = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); }
void Test(string name, Action check) { check(); passed++; Console.WriteLine("PASS " + name); }
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
SimulationChecks.Run(Test);
Console.WriteLine($"{passed} OpenRTS checks passed.");
