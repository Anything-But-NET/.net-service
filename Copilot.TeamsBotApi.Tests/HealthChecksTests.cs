using System.Net;
using System.Text;
using System.Text.Json;
using Copilot.TeamsBotApi.Health;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using Moq.Protected;

namespace Copilot.TeamsBotApi.Tests;

public class HealthChecksTests
{
    private static IConfiguration BuildConfig(Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

    private static IHttpClientFactory CreateMockHttpClientFactory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns(responder);

        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handlerMock.Object));

        return factoryMock.Object;
    }

    [Fact]
    public async Task JavaCoreHealthCheck_ReturnsHealthy_WhenActuatorReturnsUp()
    {
        Uri? capturedUri = null;
        var factory = CreateMockHttpClientFactory((req, _) =>
        {
            capturedUri = req.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"UP\",\"components\":{\"db\":{\"status\":\"UP\"}}}", Encoding.UTF8, "application/json")
            });
        });

        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["JavaCore:ChatApiUrl"] = "http://java-service:8080/api/v1/chat/question"
        });

        var check = new JavaCoreHealthCheck(factory, config);
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("http://java-service:8080/actuator/health", capturedUri?.ToString());
    }

    [Fact]
    public async Task JavaCoreHealthCheck_ReturnsDegraded_WhenActuatorReturnsDegraded()
    {
        var factory = CreateMockHttpClientFactory((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"DEGRADED\"}", Encoding.UTF8, "application/json")
            }));

        var check = new JavaCoreHealthCheck(factory, BuildConfig(new()));
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task JavaCoreHealthCheck_ReturnsUnhealthy_WhenUnreachable()
    {
        var factory = CreateMockHttpClientFactory((_, _) =>
            throw new HttpRequestException("Connection refused"));

        var check = new JavaCoreHealthCheck(factory, BuildConfig(new()));
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task KafkaHealthCheck_ReturnsHealthy_WhenBrokerReachable()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Kafka:Host"] = "kafka:9094"
        });

        var check = new KafkaHealthCheck(config, (host, port, _) =>
        {
            Assert.Equal("kafka", host);
            Assert.Equal(9094, port);
            return Task.FromResult(true);
        });

        var result = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task KafkaHealthCheck_ReturnsDegraded_WhenRegisteredAsDegradedAndBrokerUnreachable()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Kafka:Host"] = "kafka:9094"
        });

        var check = new KafkaHealthCheck(config, (_, _, _) => Task.FromResult(false));
        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("kafka", check, HealthStatus.Degraded, null)
        };
        var result = await check.CheckHealthAsync(context);

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("xoxb-token-is-in-env-variables")]
    [InlineData("xoxb-xxxx")]
    [InlineData("placeholder")]
    public async Task SlackHealthCheck_ReturnsUnhealthy_ForMissingOrPlaceholderTokens(string token)
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Slack:BotToken"] = token
        });
        var factory = CreateMockHttpClientFactory((_, _) =>
            throw new InvalidOperationException("Should not call Slack API when token is invalid"));

        var check = new SlackHealthCheck(config, factory);
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task SlackHealthCheck_ReturnsHealthy_ForConfiguredTokenWithoutSpammingApi()
    {
        int apiCalls = 0;
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Slack:BotToken"] = "xoxb-real-secret-token-12345"
        });
        var factory = CreateMockHttpClientFactory((_, _) =>
        {
            apiCalls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var check = new SlackHealthCheck(config, factory);
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(0, apiCalls);
    }

    [Fact]
    public async Task SlackHealthCheck_CachesAuthTest_WhenRemoteVerificationEnabled()
    {
        int apiCalls = 0;
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Slack:BotToken"] = "xoxb-real-secret-token-12345",
            ["Slack:VerifyAuthTest"] = "true"
        });
        var factory = CreateMockHttpClientFactory((_, _) =>
        {
            apiCalls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json")
            });
        });

        var check = new SlackHealthCheck(config, factory, TimeSpan.FromMinutes(1));
        var first = await check.CheckHealthAsync(new HealthCheckContext());
        var second = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, first.Status);
        Assert.Equal(HealthStatus.Healthy, second.Status);
        Assert.Equal(1, apiCalls);
    }

    [Fact]
    public async Task GraphHealthCheck_ReturnsHealthy_WhenMockGraphSitesReachable()
    {
        Uri? capturedUri = null;
        var factory = CreateMockHttpClientFactory((req, _) =>
        {
            capturedUri = req.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"value\":[]}", Encoding.UTF8, "application/json")
            });
        });

        var check = new GraphHealthCheck(factory, BuildConfig(new()));
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("http://mock-graph:8080/v1.0/sites", capturedUri?.ToString());
    }

    [Fact]
    public async Task GraphHealthCheck_ReturnsHealthy_WhenAllRealTenantCredsAndGraphUrlConfigured()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["GraphApi:BaseUrl"] = "https://graph.microsoft.com/v1.0",
            ["GraphApi:TenantId"] = "tenant-123",
            ["GraphApi:ClientId"] = "client-123",
            ["GraphApi:ClientSecret"] = "super-secret-value"
        });
        var factory = CreateMockHttpClientFactory((_, _) =>
            throw new InvalidOperationException("Should not send static mock bearer token to real Graph"));

        var check = new GraphHealthCheck(factory, config);
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task GraphHealthCheck_ReturnsDegraded_WhenRegisteredAsDegradedAndCredentialsIncomplete()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["GraphApi:TenantId"] = "tenant-123",
            ["GraphApi:ClientId"] = "",
            ["GraphApi:ClientSecret"] = "super-secret-value"
        });
        var factory = CreateMockHttpClientFactory((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        var check = new GraphHealthCheck(factory, config);
        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("graph", check, HealthStatus.Degraded, null)
        };
        var result = await check.CheckHealthAsync(context);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.DoesNotContain("super-secret-value", result.Description ?? string.Empty);
    }

    [Fact]
    public async Task HealthCheckResponseWriter_FormatsDegradedAndDownComponentsAccordingToContract()
    {
        var entries = new Dictionary<string, HealthReportEntry>
        {
            ["javaCore"] = new(HealthStatus.Healthy, null, TimeSpan.FromMilliseconds(5), null, null),
            ["kafka"] = new(HealthStatus.Degraded, "Kafka broker is unreachable", TimeSpan.FromMilliseconds(10), null, null),
            ["slack"] = new(HealthStatus.Healthy, null, TimeSpan.FromMilliseconds(1), null, null),
            ["graph"] = new(HealthStatus.Healthy, null, TimeSpan.FromMilliseconds(4), null, null)
        };
        var report = new HealthReport(entries, TimeSpan.FromMilliseconds(20));

        var httpContext = new DefaultHttpContext();
        using var bodyStream = new MemoryStream();
        httpContext.Response.Body = bodyStream;

        await HealthCheckResponseWriter.WriteResponseAsync(httpContext, report);

        bodyStream.Position = 0;
        using var doc = await JsonDocument.ParseAsync(bodyStream);
        var root = doc.RootElement;

        Assert.Equal("DEGRADED", root.GetProperty("status").GetString());
        var components = root.GetProperty("components");
        Assert.Equal("UP", components.GetProperty("javaCore").GetProperty("status").GetString());
        Assert.False(components.GetProperty("javaCore").TryGetProperty("details", out _));
        Assert.Equal("DOWN", components.GetProperty("kafka").GetProperty("status").GetString());
        Assert.Equal(
            "Kafka broker is unreachable",
            components.GetProperty("kafka").GetProperty("details").GetProperty("reason").GetString());
        Assert.Equal("UP", components.GetProperty("slack").GetProperty("status").GetString());
        Assert.Equal("UP", components.GetProperty("graph").GetProperty("status").GetString());
    }
}
