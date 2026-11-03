using System.Text.Json;
using DeclarativeContent;
using OpenRTS.Content;

if (args.Length > 1)
{
    Console.Error.WriteLine("Usage: OpenRTS.Headless [units.yaml]");
    return 2;
}

try
{
    var path = args.Length == 1 ? args[0] : "sandbox/content/units.yaml";
    var catalog = UnitCatalog.Load([new ContentSource(path, File.ReadAllText(path))]);
    Console.WriteLine("OPENRTS_CONTENT:" + JsonSerializer.Serialize(catalog.Summary));
    return 0;
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or ContentException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
