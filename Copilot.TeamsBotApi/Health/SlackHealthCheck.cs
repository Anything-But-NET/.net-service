using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Copilot.TeamsBotApi.Health;

public sealed class SlackHealthCheck : IHealthCheck
{
    private const int DefaultTimeoutMs = 1500;
    private static readonly HashSet<string> PlaceholderTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "",
        "xoxb-token-is-in-env-variables",
        "xoxb-xxxx",
        "your-slack-token",
        "changeme",
        "placeholder"
    };

    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeSpan _cacheTtl;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;
    private HealthCheckResult? _cachedResult;

    public SlackHealthCheck(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        TimeSpan? cacheTtl = null)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _cacheTtl = cacheTtl ?? TimeSpan.FromSeconds(60);
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var failureStatus = context.Registration?.FailureStatus ?? HealthStatus.Degraded;
        var token = _configuration["Slack:BotToken"]?.Trim() ?? string.Empty;
        if (!IsTokenConfigured(token))
        {
            return new HealthCheckResult(failureStatus, "Slack:BotToken is not configured");
        }

        var verifyRemote = bool.TryParse(_configuration["Slack:VerifyAuthTest"]?.Trim(), out var parsedVerify) && parsedVerify;
        if (!verifyRemote)
        {
            return HealthCheckResult.Healthy();
        }

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (_cachedResult is { } cached && (now - _cachedAt) < _cacheTtl)
            {
                return cached;
            }

            var timeoutMs = HealthCheckResponseWriter.ResolveTimeoutMs(
                _configuration,
                "HealthChecks:TimeoutMs",
                DefaultTimeoutMs);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

            HealthCheckResult result;
            try
            {
                var client = _httpClientFactory.CreateClient();
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/auth.test");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using var response = await client.SendAsync(request, timeoutCts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    result = new HealthCheckResult(failureStatus, "Slack auth.test returned non-success status");
                }
                else
                {
                    var body = await response.Content.ReadFromJsonAsync<JsonElement>(timeoutCts.Token);
                    var ok = body.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.True;
                    result = ok
                        ? HealthCheckResult.Healthy()
                        : new HealthCheckResult(failureStatus, "Slack auth.test verification failed");
                }
            }
            catch (Exception)
            {
                result = new HealthCheckResult(failureStatus, "Slack API is unreachable");
            }

            _cachedAt = now;
            _cachedResult = result;
            return result;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public static bool IsTokenConfigured(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var trimmed = token.Trim();
        return !PlaceholderTokens.Contains(trimmed);
    }
}
