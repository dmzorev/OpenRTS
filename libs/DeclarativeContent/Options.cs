using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;

namespace DeclarativeContent;

public interface IValidateContent
{
    IEnumerable<string> ValidateContent();
}

/// <summary>Strict, opt-in by registered option type. No arbitrary CLR type names in content.</summary>
public static class Options
{
    public static T Bind<T>(ContentNode node, string path, bool partial = false) where T : new() => (T)Bind(typeof(T), node, path, partial);

    private static object Bind(Type type, ContentNode node, string path, bool partial)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null) type = underlying;
        if (type == typeof(string))
        {
            if (node.Fields is not null || node.Items is not null) throw node.Error(path, "Ожидалась строка.");
            return node.Scalar ?? "";
        }
        if (type.IsEnum)
        {
            if (node.Scalar is null || !Enum.GetNames(type).Contains(node.Scalar))
                throw node.Error(path, $"Ожидалось одно из: {string.Join(", ", Enum.GetNames(type))}.");
            return Enum.Parse(type, node.Scalar);
        }
        if (type == typeof(bool) || type == typeof(int) || type == typeof(double))
        {
            try
            {
                if (string.IsNullOrWhiteSpace(node.Scalar)) throw new FormatException();
                object value = type == typeof(bool) ? bool.Parse(node.Scalar) : type == typeof(int) ?
                    (object)int.Parse(node.Scalar, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) :
                    double.Parse(node.Scalar, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (value is double d && !double.IsFinite(d)) throw new FormatException();
                return value;
            }
            catch (Exception e) when (e is FormatException or OverflowException)
            { throw node.Error(path, $"Некорректное значение «{node.Scalar}» для {type.Name}; десятичный разделитель — точка."); }
        }
        if (type.IsArray)
        {
            if (node.Items is null) throw node.Error(path, "Ожидался список: [a, b] или строки с '- '.");
            var element = type.GetElementType()!; var result = Array.CreateInstance(element, node.Items.Count);
            for (int i = 0; i < result.Length; i++) result.SetValue(Bind(element, node.Items[i], $"{path}[{i}]", partial), i);
            return result;
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>) && type.GetGenericArguments()[0] == typeof(string))
        {
            var result = (System.Collections.IDictionary)Activator.CreateInstance(type)!;
            foreach (var (key, value) in node.Map(path)) result.Add(key, Bind(type.GetGenericArguments()[1], value, $"{path}.{key}", partial));
            return result;
        }
        var fields = node.Fields ?? (string.IsNullOrEmpty(node.Scalar) && node.Items is null ?
            new Dictionary<string, ContentNode>() : throw node.Error(path, "Ожидался блок параметров."));
        var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(p => p.SetMethod is not null).ToDictionary(p => p.Name);
        foreach (var field in fields.Keys)
            if (!properties.ContainsKey(field)) throw fields[field].Error($"{path}.{field}", $"Неизвестное поле. Допустимы: {string.Join(", ", properties.Keys)}.");
        var options = Activator.CreateInstance(type) ?? throw node.Error(path, "Тип параметров должен иметь пустой конструктор.");
        foreach (var (name, property) in properties)
        {
            if (partial && !fields.ContainsKey(name)) continue;
            if (fields.TryGetValue(name, out var child)) property.SetValue(options, Bind(property.PropertyType, child, $"{path}.{name}", partial));
            else if (property.IsDefined(typeof(RequiredAttribute))) throw node.Error($"{path}.{name}", "Обязательное поле отсутствует.");
            foreach (var rule in property.GetCustomAttributes<ValidationAttribute>())
                if (!rule.IsValid(property.GetValue(options))) throw (child ?? node).Error($"{path}.{name}", rule.FormatErrorMessage(name));
        }
        if (!partial && options is IValidateContent validated)
            foreach (var message in validated.ValidateContent()) throw node.Error(path, message);
        return options;
    }
}
