using System.Net.Sockets;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Copilot.TeamsBotApi.Health;

public sealed class KafkaHealthCheck : IHealthCheck
{
    private const string DefaultKafkaHost = "kafka:9094";
    private const int DefaultKafkaPort = 9092;
    private const int DefaultTimeoutMs = 1500;

    private readonly IConfiguration _configuration;
    private readonly Func<string, int, CancellationToken, Task<bool>> _tcpProbe;

    public KafkaHealthCheck(
        IConfiguration configuration,
        Func<string, int, CancellationToken, Task<bool>>? tcpProbe = null)
    {
        _configuration = configuration;
        _tcpProbe = tcpProbe ?? DefaultTcpProbeAsync;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var failureStatus = context.Registration?.FailureStatus ?? HealthStatus.Unhealthy;
        var rawHost = _configuration["Kafka:Host"];
        if (string.IsNullOrWhiteSpace(rawHost))
        {
            rawHost = DefaultKafkaHost;
        }

        var timeoutMs = HealthCheckResponseWriter.ResolveTimeoutMs(
            _configuration,
            "HealthChecks:TimeoutMs",
            DefaultTimeoutMs);
        var brokers = rawHost.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (brokers.Length == 0)
        {
            return new HealthCheckResult(failureStatus, "Kafka broker host is not configured");
        }

        foreach (var broker in brokers)
        {
            if (!TryParseHostPort(broker, out var host, out var port))
            {
                continue;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

            try
            {
                if (await _tcpProbe(host, port, timeoutCts.Token))
                {
                    return HealthCheckResult.Healthy();
                }
            }
            catch (Exception)
            {
                // Try next broker if multiple are configured
            }
        }

        return new HealthCheckResult(failureStatus, "Kafka broker is unreachable");
    }

    public static bool TryParseHostPort(string endpoint, out string host, out int port)
    {
        host = string.Empty;
        port = DefaultKafkaPort;

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }

        var trimmed = endpoint.Trim();
        var colonIndex = trimmed.LastIndexOf(':');
        if (colonIndex <= 0)
        {
            host = trimmed;
            return !string.IsNullOrWhiteSpace(host);
        }

        host = trimmed[..colonIndex].Trim();
        var portPart = trimmed[(colonIndex + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(host) || !int.TryParse(portPart, out port) || port is <= 0 or > 65535)
        {
            return false;
        }

        return true;
    }

    private static async Task<bool> DefaultTcpProbeAsync(string host, int port, CancellationToken ct)
    {
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(host, port, ct);
        return tcpClient.Connected;
    }
}
