using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json.Nodes;

namespace DeclarativeContent;

/// <summary>Editor hints for partial definitions; required fields are checked after inheritance by the loader.</summary>
public static class ContentSchema
{
    public static JsonObject Generate(IReadOnlyDictionary<string, Type> traits)
    {
        var definitions = new JsonObject();
        JsonObject Shape(Type type)
        {
            if (type == typeof(string)) return new() { ["type"] = "string" };
            if (type == typeof(bool)) return new() { ["type"] = "boolean" };
            if (type == typeof(int)) return new() { ["type"] = "integer" };
            if (type == typeof(double)) return new() { ["type"] = "number" };
            if (type.IsEnum) return new() { ["type"] = "string", ["enum"] = new JsonArray(Enum.GetNames(type).Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()) };
            if (type.IsArray) return new() { ["type"] = "array", ["items"] = Shape(type.GetElementType()!) };
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                return new() { ["type"] = "object", ["patternProperties"] = new JsonObject {
                    ["^[^-].*$"] = Shape(type.GetGenericArguments()[1]), ["^-.+$"] = new JsonObject { ["type"] = "null" }
                }, ["additionalProperties"] = false };
            string key = type.FullName!;
            if (!definitions.ContainsKey(key))
            {
                definitions.Add(key, new JsonObject());
                var properties = new JsonObject();
                var example = Activator.CreateInstance(type);
                foreach (var p in type.GetProperties().Where(p => p.SetMethod is not null))
                {
                    var value = Shape(p.PropertyType);
                    var description = p.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
                    if (p.IsDefined(typeof(RequiredAttribute))) description += " Обязательно после наследования.";
                    value["description"] = description.Trim();
                    if (p.GetCustomAttribute<RangeAttribute>() is { } range)
                    {
                        value["minimum"] = Convert.ToDouble(range.Minimum, System.Globalization.CultureInfo.InvariantCulture);
                        value["maximum"] = Convert.ToDouble(range.Maximum, System.Globalization.CultureInfo.InvariantCulture);
                    }
                    if (p.GetCustomAttribute<MinLengthAttribute>() is { } min)
                        value[p.PropertyType.IsArray ? "minItems" : p.PropertyType == typeof(string) ? "minLength" : "minProperties"] = min.Length;
                    if (!p.IsDefined(typeof(RequiredAttribute)) && p.GetValue(example) is { } defaultValue &&
                        (p.PropertyType.IsPrimitive || p.PropertyType == typeof(string) || p.PropertyType.IsEnum))
                        value["default"] = p.PropertyType.IsEnum ? JsonValue.Create(defaultValue.ToString()) : System.Text.Json.JsonSerializer.SerializeToNode(defaultValue);
                    properties.Add(p.Name, value);
                }
                definitions[key] = new JsonObject { ["type"] = "object", ["properties"] = properties,
                    ["patternProperties"] = new JsonObject { ["^-.+$"] = new JsonObject { ["type"] = "null" } }, ["additionalProperties"] = false };
            }
            return new() { ["$ref"] = "#/$defs/" + key };
        }
        var patterns = new JsonObject
        {
            ["^Inherits(@[^@]+)?$"] = new JsonObject { ["type"] = "string" },
            ["^-.+$"] = new JsonObject { ["type"] = "null" }
        };
        foreach (var (name, type) in traits) patterns.Add("^" + System.Text.RegularExpressions.Regex.Escape(name) + "(@[^@]+)?$", Shape(type));
        return new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = "DeclarativeContent: partial definitions with inheritance",
            ["type"] = "object", ["$defs"] = definitions,
            ["additionalProperties"] = new JsonObject { ["type"] = "object", ["patternProperties"] = patterns, ["additionalProperties"] = false }
        };
    }
}
