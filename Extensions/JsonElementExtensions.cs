namespace OtelDashboardApi.Extensions;

using System.Text.Json;

/// <summary>
/// Extension methods for JsonElement to safely get properties with defaults.
/// </summary>
public static class JsonElementExtensions
{
    public static JsonElement GetPropertyOrDefault(this JsonElement element, string propertyName, JsonElement defaultValue)
    {
        if (element.TryGetProperty(propertyName, out var property))
        {
            return property;
        }
        return defaultValue;
    }

    public static string? GetStringOrDefault(this JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var property))
        {
            return property.GetString();
        }
        return null;
    }
}
