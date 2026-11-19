using System.Net.Http.Headers;
using Copilot.IngestionWorker.Adapters;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Copilot.TeamsBotApi.Health;

public sealed class GraphHealthCheck : IHealthCheck
{
    private const int DefaultTimeoutMs = 1500;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    public GraphHealthCheck(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var failureStatus = context.Registration?.FailureStatus ?? HealthStatus.Unhealthy;
        var tenantId = _configuration["GraphApi:TenantId"]?.Trim();
        var clientId = _configuration["GraphApi:ClientId"]?.Trim();
        var clientSecret = _configuration["GraphApi:ClientSecret"]?.Trim();
        var configuredBaseUrl = _configuration["GraphApi:BaseUrl"]?.Trim();

        bool anyRealCredProvided =
            !string.IsNullOrWhiteSpace(tenantId) ||
            !string.IsNullOrWhiteSpace(clientId) ||
            !string.IsNullOrWhiteSpace(clientSecret);

        bool allRealCredsProvided =
            !string.IsNullOrWhiteSpace(tenantId) &&
            !string.IsNullOrWhiteSpace(clientId) &&
            !string.IsNullOrWhiteSpace(clientSecret);

        if (anyRealCredProvided && !allRealCredsProvided)
        {
            return new HealthCheckResult(failureStatus, "GraphApi tenant credentials are incomplete");
        }

        if (allRealCredsProvided)
        {
            return HealthCheckResult.Healthy();
        }

        var baseUrl = !string.IsNullOrWhiteSpace(configuredBaseUrl)
            ? configuredBaseUrl.TrimEnd('/')
            : GraphClientFactory.DefaultMockGraphBaseUrl;

        var timeoutMs = _configuration.GetValue("HealthChecks:TimeoutMs", DefaultTimeoutMs);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

        try
        {
            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/sites");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "app");

            using var response = await client.SendAsync(request, timeoutCts.Token);
            if (response.IsSuccessStatusCode)
            {
                return HealthCheckResult.Healthy();
            }

            return new HealthCheckResult(failureStatus, "Graph/SharePoint endpoint returned non-success status");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HealthCheckResult(failureStatus, "Graph/SharePoint health check timed out");
        }
        catch (Exception)
        {
            return new HealthCheckResult(failureStatus, "Graph/SharePoint endpoint is unreachable");
        }
    }
}
