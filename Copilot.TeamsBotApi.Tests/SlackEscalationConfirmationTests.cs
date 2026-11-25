using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Copilot.Contracts;
using Copilot.TeamsBotApi.Controllers;
using Copilot.TeamsBotApi.Helpers;
using Copilot.TeamsBotApi.Resources;
using Copilot.TeamsBotApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace Copilot.TeamsBotApi.Tests;

public class SlackEscalationConfirmationTests
{
    private const string PlaceholderTs = "1700000000.000100";
    private const string TestQueryId = "req-esc-1";
    private const string TestEscalationId = "550e8400-e29b-41d4-a716-446655440000";
    private const string TestSigningSecret = "test-signing-secret";

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(25);
        }
    }

    private static (
        SlackController SlackController,
        SlackInteractionsController InteractionsController,
        IConfiguration Config,
        ConcurrentQueue<(string Url, string Body, string? CorrelationId)> Requests)
        CreateControllers(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? escalationResponse = null,
            UserQuestionResponse? questionResponse = null,
            bool failChatUpdate = false)
    {
        var requests = new ConcurrentQueue<(string Url, string Body, string? CorrelationId)>();

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns(async (HttpRequestMessage req, CancellationToken ct) =>
            {
                var url = req.RequestUri!.ToString();
                var body = req.Content is null ? string.Empty : await req.Content.ReadAsStringAsync(ct);
                var corrId = req.Headers.TryGetValues("X-Correlation-ID", out var corrVals) ? corrVals.FirstOrDefault() : null;
                requests.Enqueue((url, body, corrId));

                if (url.Contains("users.info"))
                    return Json("{\"ok\":true,\"user\":{\"profile\":{\"email\":\"employee@company.com\"}}}");
                if (url.Contains("chat.postMessage"))
                    return Json($"{{\"ok\":true,\"ts\":\"{PlaceholderTs}\"}}");
                if (url.Contains("chat.update"))
                    return failChatUpdate ? Json("{\"ok\":false,\"error\":\"message_not_found\"}") : Json("{\"ok\":true}");

                if (url.Contains("/api/v1/chat/escalations"))
                {
                    if (escalationResponse is not null)
                        return await escalationResponse(req, ct);

                    return Json(JsonSerializer.Serialize(new EscalationResponse
                    {
                        EscalationId = TestEscalationId,
                        QueryId = TestQueryId,
                        Status = "PENDING",
                        CreatedAt = "2026-11-23T10:15:30Z",
                        UpdatedAt = "2026-11-23T10:15:30Z"
                    }), HttpStatusCode.OK);
                }

                var qResp = questionResponse ?? new UserQuestionResponse
                {
                    QueryId = TestQueryId,
                    Status = "ESCALATED_TO_EXPERT",
                    ReplyText = "Could not answer reliably.",
                    Citations = new List<Citation>()
                };

                return Json(JsonSerializer.Serialize(qResp));
            });

        var clientFactoryMock = new Mock<IHttpClientFactory>();
        clientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handlerMock.Object));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JavaCore:ChatApiUrl"] = "http://java-service:8080/api/v1/chat/question",
                ["JavaCore:EscalationApiUrl"] = "http://java-service:8080/api/v1/chat/escalations",
                ["Slack:BotToken"] = "xoxb-mock-token",
                ["Slack:SigningSecret"] = TestSigningSecret
            })
            .Build();

        var localizerMock = new Mock<IStringLocalizer<BotMessages>>();
        localizerMock.Setup(l => l[It.IsAny<string>()]).Returns((string key) =>
            key == "EscalationConfirmed"
                ? new LocalizedString(key, "Recorded for expert review (Reference ID: {0}).")
                : new LocalizedString(key, key));

        var slackController = new SlackController(
            NullLogger<SlackController>.Instance,
            clientFactoryMock.Object,
            config,
            localizerMock.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var interactionsController = new SlackInteractionsController(
            NullLogger<SlackInteractionsController>.Instance,
            clientFactoryMock.Object,
            config,
            localizerMock.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        return (slackController, interactionsController, config, requests);
    }

    private static string BuildActionToken(
        string queryId = TestQueryId,
        string userId = "U123",
        string channelId = "C123",
        string? secret = TestSigningSecret) =>
        EscalationActionToken.Encode(new EscalationActionContext(queryId, userId, channelId), secret);

    private static string ExtractButtonValue(string chatUpdateBody, string actionId)
    {
        using var doc = JsonDocument.Parse(chatUpdateBody);
        var blocks = doc.RootElement.GetProperty("blocks");
        foreach (var block in blocks.EnumerateArray())
        {
            if (block.TryGetProperty("elements", out var elements))
            {
                foreach (var element in elements.EnumerateArray())
                {
                    if (element.TryGetProperty("action_id", out var idProp) &&
                        idProp.GetString() == actionId)
                    {
                        return element.GetProperty("value").GetString()!;
                    }
                }
            }
        }

        throw new InvalidOperationException($"Action {actionId} not found in blocks.");
    }

    private static string BuildBlockActionPayload(string actionId, string actionValue, string userId = "U123", string channelId = "C123") =>
        JsonSerializer.Serialize(new
        {
            type = "block_actions",
            user = new { id = userId },
            channel = new { id = channelId },
            message = new { ts = PlaceholderTs },
            actions = new[]
            {
                new { action_id = actionId, value = actionValue }
            }
        });

    [Fact]
    public async Task EscalatedQuestion_OffersConfirmationWithSignedActionToken_AndDoesNotCreateTicketBeforeConfirm()
    {
        var (slackController, _, config, requests) = CreateControllers();
        var mention = JsonDocument.Parse(
            $"{{\"type\":\"event_callback\",\"event_id\":\"{TestQueryId}\",\"event\":{{\"type\":\"app_mention\",\"user\":\"U123\",\"channel\":\"C123\",\"text\":\"<@U01BOT> How do I access finance portal?\"}}}}")
            .RootElement;

        var result = slackController.ReceiveEvent(mention);

        Assert.IsType<OkResult>(result);
        await WaitUntilAsync(() => requests.Any(r => r.Url.Contains("chat.update")));

        var update = Assert.Single(requests, r => r.Url.Contains("chat.update"));
        Assert.Contains("EscalationOfferPrompt", update.Body);
        Assert.Contains(SlackMessageFormatter.EscalationConfirmActionId, update.Body);
        Assert.Contains(SlackMessageFormatter.EscalationDeclineActionId, update.Body);

        var token = ExtractButtonValue(update.Body, SlackMessageFormatter.EscalationConfirmActionId);
        Assert.True(EscalationActionToken.TryDecode(token, EscalationActionToken.ResolveSigningSecret(config), out var decoded));
        Assert.Equal(TestQueryId, decoded!.QueryId);
        Assert.Equal("U123", decoded.SlackUserId);
        Assert.Equal("C123", decoded.Channel);

        Assert.DoesNotContain(requests, r => r.Url.Contains("/api/v1/chat/escalations"));
    }

    [Fact]
    public async Task DecliningEscalation_EndsFlowWithoutCreatingTicket_AndClearsButtons()
    {
        var (slackController, interactionsController, _, requests) = CreateControllers();
        var mention = JsonDocument.Parse(
            $"{{\"type\":\"event_callback\",\"event_id\":\"{TestQueryId}\",\"event\":{{\"type\":\"app_mention\",\"user\":\"U123\",\"channel\":\"C123\",\"text\":\"<@U01BOT> How do I access finance portal?\"}}}}")
            .RootElement;

        slackController.ReceiveEvent(mention);
        await WaitUntilAsync(() => requests.Any(r => r.Url.Contains("chat.update")));
        var token = ExtractButtonValue(
            requests.First(r => r.Url.Contains("chat.update")).Body,
            SlackMessageFormatter.EscalationDeclineActionId);

        var declineResult = interactionsController.ReceiveInteraction(
            BuildBlockActionPayload(SlackMessageFormatter.EscalationDeclineActionId, token, userId: "U123"));

        Assert.IsType<OkResult>(declineResult);
        await WaitUntilAsync(() => requests.Any(r => r.Url.Contains("chat.update") && r.Body.Contains("EscalationDeclined")));

        var declineUpdate = Assert.Single(requests, r => r.Url.Contains("chat.update") && r.Body.Contains("EscalationDeclined"));
        Assert.Contains("\"blocks\":[]", declineUpdate.Body);
        Assert.DoesNotContain(requests, r => r.Url.Contains("/api/v1/chat/escalations"));
    }

    [Fact]
    public async Task ConfirmingEscalation_PostsQueryIdToMergedEndpoint_AndDisplaysEscalationId()
    {
        var (slackController, interactionsController, _, requests) = CreateControllers();
        var mention = JsonDocument.Parse(
            $"{{\"type\":\"event_callback\",\"event_id\":\"{TestQueryId}\",\"event\":{{\"type\":\"app_mention\",\"user\":\"U123\",\"channel\":\"C123\",\"text\":\"<@U01BOT> How do I access finance portal?\"}}}}")
            .RootElement;

        slackController.ReceiveEvent(mention);
        await WaitUntilAsync(() => requests.Any(r => r.Url.Contains("chat.update")));
        var token = ExtractButtonValue(
            requests.First(r => r.Url.Contains("chat.update")).Body,
            SlackMessageFormatter.EscalationConfirmActionId);

        var confirmResult = interactionsController.ReceiveInteraction(
            BuildBlockActionPayload(SlackMessageFormatter.EscalationConfirmActionId, token, userId: "U123"));

        Assert.IsType<OkResult>(confirmResult);
        await WaitUntilAsync(() => requests.Any(r => r.Url.Contains("chat.update") && r.Body.Contains(TestEscalationId)));

        var escalationCall = Assert.Single(requests, r => r.Url.Contains("/api/v1/chat/escalations"));
        Assert.Equal(TestQueryId, escalationCall.CorrelationId);
        Assert.Equal($"{{\"query_id\":\"{TestQueryId}\"}}", escalationCall.Body);

        var confirmedUpdate = Assert.Single(requests, r => r.Url.Contains("chat.update") && r.Body.Contains(TestEscalationId));
        Assert.Contains("\"blocks\":[]", confirmedUpdate.Body);
    }

    [Fact]
    public async Task DoubleClickConfirm_IsStatelessAndIdempotent_WithoutShowingExpired()
    {
        var (_, interactionsController, _, requests) = CreateControllers();
        var token = BuildActionToken();

        var first = interactionsController.ReceiveInteraction(
            BuildBlockActionPayload(SlackMessageFormatter.EscalationConfirmActionId, token, userId: "U123"));
        var second = interactionsController.ReceiveInteraction(
            BuildBlockActionPayload(SlackMessageFormatter.EscalationConfirmActionId, token, userId: "U123"));

        Assert.IsType<OkResult>(first);
        Assert.IsType<OkResult>(second);

        await WaitUntilAsync(() => requests.Count(r => r.Url.Contains("chat.update") && r.Body.Contains(TestEscalationId)) == 2);

        Assert.DoesNotContain(requests, r => r.Body.Contains("EscalationExpired"));
        Assert.Equal(2, requests.Count(r => r.Url.Contains("/api/v1/chat/escalations")));
    }

    [Fact]
    public async Task NonRequesterClickingConfirm_IsIgnoredWithoutUpdatingSlackOrCreatingTicket()
    {
        var (_, interactionsController, _, requests) = CreateControllers();
        var token = BuildActionToken(userId: "U123", channelId: "C123");

        var result = interactionsController.ReceiveInteraction(
            BuildBlockActionPayload(SlackMessageFormatter.EscalationConfirmActionId, token, userId: "U999_OTHER", channelId: "C123"));

        Assert.IsType<OkResult>(result);
        await Task.Delay(150);

        Assert.Empty(requests);
    }

    [Fact]
    public async Task WrongChannelClickingConfirm_IsIgnoredWithoutUpdatingSlackOrCreatingTicket()
    {
        var (_, interactionsController, _, requests) = CreateControllers();
        var token = BuildActionToken(userId: "U123", channelId: "C123");

        var result = interactionsController.ReceiveInteraction(
            BuildBlockActionPayload(SlackMessageFormatter.EscalationConfirmActionId, token, userId: "U123", channelId: "C999_OTHER"));

        Assert.IsType<OkResult>(result);
        await Task.Delay(150);

        Assert.Empty(requests);
    }

    [Fact]
    public void TamperedOrInvalidActionToken_ReturnsBadRequestWithoutTouchingSlackOrBackend()
    {
        var (_, interactionsController, _, requests) = CreateControllers();
        var tamperedToken = BuildActionToken() + "tampered";

        var tamperedResult = interactionsController.ReceiveInteraction(
            BuildBlockActionPayload(SlackMessageFormatter.EscalationConfirmActionId, tamperedToken));
        var plainResult = interactionsController.ReceiveInteraction(
            BuildBlockActionPayload(SlackMessageFormatter.EscalationConfirmActionId, TestQueryId));

        Assert.IsType<BadRequestObjectResult>(tamperedResult);
        Assert.IsType<BadRequestObjectResult>(plainResult);
        Assert.Empty(requests);
    }

    [Fact]
    public async Task Backend410Gone_ShowsUserMessageAndClearsButtons()
    {
        var (_, interactionsController, _, requests) = CreateControllers(
            escalationResponse: (_, _) => Task.FromResult(Json(
                JsonSerializer.Serialize(new GatewayErrorResponse
                {
                    QueryId = TestQueryId,
                    ErrorCode = "BAD_REQUEST",
                    UserMessage = "Escalation confirmation window (15m) has expired. Please ask your question again."
                }),
                HttpStatusCode.Gone)));

        var token = BuildActionToken();
        interactionsController.ReceiveInteraction(
            BuildBlockActionPayload(SlackMessageFormatter.EscalationConfirmActionId, token));

        await WaitUntilAsync(() => requests.Any(r => r.Url.Contains("chat.update") && r.Body.Contains("15m")));

        var goneUpdate = Assert.Single(requests, r => r.Url.Contains("chat.update") && r.Body.Contains("15m"));
        Assert.DoesNotContain("BAD_REQUEST", goneUpdate.Body);
        Assert.Contains("\"blocks\":[]", goneUpdate.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Transient5xxFailure_KeepsRetryButtonsAndRetriesIdempotently(HttpStatusCode failureStatus)
    {
        int attempt = 0;
        var (_, interactionsController, _, requests) = CreateControllers(
            escalationResponse: (_, _) =>
            {
                if (Interlocked.Increment(ref attempt) == 1)
                {
                    return Task.FromResult(Json(
                        JsonSerializer.Serialize(new GatewayErrorResponse
                        {
                            QueryId = TestQueryId,
                            ErrorCode = "INTERNAL_ERROR",
                            UserMessage = "Temporary backend failure."
                        }),
                        failureStatus));
                }

                return Task.FromResult(Json(JsonSerializer.Serialize(new EscalationResponse
                {
                    EscalationId = TestEscalationId,
                    QueryId = TestQueryId,
                    Status = "PENDING",
                    CreatedAt = "2026-11-23T10:15:30Z",
                    UpdatedAt = "2026-11-23T10:15:30Z"
                }), HttpStatusCode.OK));
            });

        var token = BuildActionToken();
        interactionsController.ReceiveInteraction(
            BuildBlockActionPayload(SlackMessageFormatter.EscalationConfirmActionId, token));

        await WaitUntilAsync(() => requests.Any(r => r.Url.Contains("chat.update") && r.Body.Contains("Temporary backend failure.")));
        var retryUpdate = Assert.Single(requests, r => r.Url.Contains("chat.update") && r.Body.Contains("Temporary backend failure."));
        var retryToken = ExtractButtonValue(retryUpdate.Body, SlackMessageFormatter.EscalationConfirmActionId);
        Assert.Equal(token, retryToken);

        interactionsController.ReceiveInteraction(
            BuildBlockActionPayload(SlackMessageFormatter.EscalationConfirmActionId, retryToken));

        await WaitUntilAsync(() => requests.Any(r => r.Url.Contains("chat.update") && r.Body.Contains(TestEscalationId)));

        var escalationCalls = requests.Where(r => r.Url.Contains("/api/v1/chat/escalations")).ToList();
        Assert.Equal(2, escalationCalls.Count);
        Assert.All(escalationCalls, c => Assert.Equal($"{{\"query_id\":\"{TestQueryId}\"}}", c.Body));
    }

    [Fact]
    public async Task DeliverReplyFallback_WhenChatUpdateFails_PostMessageOmitsEmptyBlocks()
    {
        var (_, interactionsController, _, requests) = CreateControllers(failChatUpdate: true);
        var token = BuildActionToken();

        interactionsController.ReceiveInteraction(
            BuildBlockActionPayload(SlackMessageFormatter.EscalationDeclineActionId, token));

        await WaitUntilAsync(() => requests.Any(r => r.Url.Contains("chat.postMessage") && r.Body.Contains("EscalationDeclined")));

        var updateCall = Assert.Single(requests, r => r.Url.Contains("chat.update") && r.Body.Contains("EscalationDeclined"));
        Assert.Contains("\"blocks\":[]", updateCall.Body);

        var postCall = Assert.Single(requests, r => r.Url.Contains("chat.postMessage") && r.Body.Contains("EscalationDeclined"));
        Assert.DoesNotContain("\"blocks\"", postCall.Body);
    }

    [Fact]
    public void ReceiveInteraction_MissingOrInvalidPayload_ReturnsBadRequest()
    {
        var (_, interactionsController, _, _) = CreateControllers();

        Assert.IsType<BadRequestObjectResult>(interactionsController.ReceiveInteraction(null));
        Assert.IsType<BadRequestObjectResult>(interactionsController.ReceiveInteraction("{not-json"));
    }

    [Fact]
    public void BotMessagesLocalizer_ResolvesEmbeddedEscalationResourcesWithoutMock()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalization();

        using var provider = services.BuildServiceProvider();
        var localizer = provider.GetRequiredService<IStringLocalizer<BotMessages>>();

        var requiredKeys = new[]
        {
            "Processing",
            "RequestTimeout",
            "EscalationOfferPrompt",
            "EscalationConfirmButton",
            "EscalationDeclineButton",
            "EscalationSubmitting",
            "EscalationConfirmed",
            "EscalationDeclined",
            "EscalationExpired",
            "EscalationSubmitFailed"
        };

        foreach (var key in requiredKeys)
        {
            var localized = localizer[key];
            Assert.False(localized.ResourceNotFound, $"Resource key '{key}' was not found.");
            Assert.NotEqual(key, localized.Value);
        }

        Assert.Contains(localizer["EscalationConfirmButton"].Value, localizer["EscalationOfferPrompt"].Value);
        Assert.Contains(TestEscalationId, string.Format(localizer["EscalationConfirmed"].Value, TestEscalationId));
    }
}
