using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Copilot.TeamsBotApi.Health;

public static class HealthCheckResponseWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static Task WriteResponseAsync(HttpContext httpContext, HealthReport report)
    {
        httpContext.Response.ContentType = "application/json; charset=utf-8";
        var payload = BuildPayload(report);
        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        return httpContext.Response.WriteAsync(json);
    }

    public static ServiceHealthPayload BuildPayload(HealthReport report)
    {
        var components = new Dictionary<string, ComponentHealthPayload>(StringComparer.Ordinal);

        foreach (var (name, entry) in report.Entries)
        {
            var componentStatus = MapComponentStatus(name, entry.Status);
            Dictionary<string, object>? details = null;

            if (!string.IsNullOrWhiteSpace(entry.Description))
            {
                details = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["reason"] = entry.Description
                };
            }

            components[name] = new ComponentHealthPayload(componentStatus, details);
        }

        return new ServiceHealthPayload(MapOverallStatus(report.Status), components);
    }

    public static string MapOverallStatus(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => "UP",
        HealthStatus.Degraded => "DEGRADED",
        _ => "DOWN"
    };

    public static string MapComponentStatus(string componentName, HealthStatus status)
    {
        if (status == HealthStatus.Healthy)
        {
            return "UP";
        }

        if (status == HealthStatus.Degraded &&
            string.Equals(componentName, "javaCore", StringComparison.Ordinal))
        {
            return "DEGRADED";
        }

        return "DOWN";
    }
}

public sealed record ComponentHealthPayload(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("details")] Dictionary<string, object>? Details = null);

public sealed record ServiceHealthPayload(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("components")] Dictionary<string, ComponentHealthPayload> Components);
