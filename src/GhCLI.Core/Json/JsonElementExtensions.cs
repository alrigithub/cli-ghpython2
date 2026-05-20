using System.Text.Json;
using GhCLI.Core.Errors;

namespace GhCLI.Core.Json;

public static class JsonElementExtensions
{
    public static bool TryGetPropertyIgnoreCase(this JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    public static string? GetOptionalString(this JsonElement element, string name)
    {
        if (!element.TryGetPropertyIgnoreCase(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => throw new CommandValidationException($"Field '{name}' must be a string.")
        };
    }

    public static string RequireString(this JsonElement element, string name)
    {
        var value = GetOptionalString(element, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CommandValidationException($"Missing required string field '{name}'.");
        }

        return value;
    }

    public static bool GetOptionalBool(this JsonElement element, string name, bool defaultValue = false)
    {
        if (!element.TryGetPropertyIgnoreCase(name, out var value))
        {
            return defaultValue;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => throw new CommandValidationException($"Field '{name}' must be a boolean.")
        };
    }

    public static double? GetOptionalDouble(this JsonElement element, string name)
    {
        if (!element.TryGetPropertyIgnoreCase(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    public static int? GetOptionalInt(this JsonElement element, string name)
    {
        if (!element.TryGetPropertyIgnoreCase(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetInt32(),
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    public static IReadOnlyList<JsonElement> GetArray(this JsonElement element, string name)
    {
        if (!element.TryGetPropertyIgnoreCase(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<JsonElement>();
        }

        return value.EnumerateArray().ToArray();
    }
}
