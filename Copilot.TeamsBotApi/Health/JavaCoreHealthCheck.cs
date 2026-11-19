using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Copilot.TeamsBotApi.Health;

public sealed class JavaCoreHealthCheck : IHealthCheck
{
    private const string DefaultHealthUrl = "http://java-service:8080/actuator/health";
    private const int DefaultTimeoutMs = 4000;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    public JavaCoreHealthCheck(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var healthUrl = ResolveHealthUrl(_configuration);
        var timeoutMs = _configuration.GetValue("HealthChecks:JavaCoreTimeoutMs", DefaultTimeoutMs);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

        try
        {
            var client = _httpClientFactory.CreateClient();
            using var response = await client.GetAsync(healthUrl, timeoutCts.Token);
            var content = await response.Content.ReadAsStringAsync(timeoutCts.Token);

            if (!string.IsNullOrWhiteSpace(content))
            {
                using var doc = JsonDocument.Parse(content);
                if (doc.RootElement.TryGetProperty("status", out var statusProp))
                {
                    var upstreamStatus = statusProp.GetString()?.Trim().ToUpperInvariant();
                    if (upstreamStatus is "UP" or "OK")
                    {
                        return HealthCheckResult.Healthy();
                    }

                    if (upstreamStatus == "DEGRADED")
                    {
                        return HealthCheckResult.Degraded("Java Core service is operating in DEGRADED mode");
                    }

                    return HealthCheckResult.Unhealthy("Java Core service reported status DOWN");
                }
            }

            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Java Core service is unavailable");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Java Core health check timed out");
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("Java Core service is unreachable");
        }
    }

    public static string ResolveHealthUrl(IConfiguration configuration)
    {
        var explicitHealthUrl = configuration["JavaCore:HealthUrl"];
        if (!string.IsNullOrWhiteSpace(explicitHealthUrl))
        {
            return explicitHealthUrl;
        }

        var chatApiUrl = configuration["JavaCore:ChatApiUrl"];
        if (!string.IsNullOrWhiteSpace(chatApiUrl) &&
            Uri.TryCreate(chatApiUrl, UriKind.Absolute, out var chatUri))
        {
            return new Uri(chatUri, "/actuator/health").ToString();
        }

        return DefaultHealthUrl;
    }
}
