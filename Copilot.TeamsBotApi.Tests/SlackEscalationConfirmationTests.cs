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
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace Copilot.TeamsBotApi.Tests;

public class SlackEscalationConfirmationTests
{
    private const string PlaceholderTs = "1700000000.000100";
    private const string TestQuestionId = "123e4567-e89b-12d3-a456-426614174000";

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
        InMemoryPendingEscalationStore Store,
        ConcurrentQueue<(string Url, string Body, string? IdempotencyKey, string? CorrelationId)> Requests)
        CreateControllers(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? escalationResponse = null,
            UserQuestionResponse? questionResponse = null)
    {
        var requests = new ConcurrentQueue<(string Url, string Body, string? IdempotencyKey, string? CorrelationId)>();
        var store = new InMemoryPendingEscalationStore();

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
                var idemKey = req.Headers.TryGetValues("Idempotency-Key", out var idemVals) ? idemVals.FirstOrDefault() : null;
                var corrId = req.Headers.TryGetValues("X-Correlation-ID", out var corrVals) ? corrVals.FirstOrDefault() : null;
                requests.Enqueue((url, body, idemKey, corrId));

                if (url.Contains("users.info"))
                    return Json("{\"ok\":true,\"user\":{\"profile\":{\"email\":\"employee@company.com\"}}}");
                if (url.Contains("chat.postMessage"))
                    return Json($"{{\"ok\":true,\"ts\":\"{PlaceholderTs}\"}}");
                if (url.Contains("chat.update"))
                    return Json("{\"ok\":true}");

                if (url.Contains("/api/v1/escalations"))
                {
                    if (escalationResponse is not null)
                        return await escalationResponse(req, ct);

                    return Json(JsonSerializer.Serialize(new EscalationCreateResponse
                    {
                        EscalationId = "987e6543-e21b-12d3-a456-426614174000",
                        QuestionId = TestQuestionId,
                        CorrelationId = "req-esc-1",
                        Status = "PENDING_DESTINATION",
                        CreatedAt = "2026-11-22T19:00:00Z"
                    }), HttpStatusCode.Created);
                }

                var qResp = questionResponse ?? new UserQuestionResponse
                {
                    QueryId = "req-esc-1",
                    QuestionId = TestQuestionId,
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
                ["JavaCore:EscalationApiUrl"] = "http://java-service:8080/api/v1/escalations",
                ["Slack:BotToken"] = "xoxb-mock-token"
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
            localizerMock.Object,
            store)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var interactionsController = new SlackInteractionsController(
            NullLogger<SlackInteractionsController>.Instance,
            clientFactoryMock.Object,
            config,
            localizerMock.Object,
            store)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        return (slackController, interactionsController, store, requests);
    }

    private static string BuildBlockActionPayload(string actionId, string questionId, string userId = "U123", string channelId = "C123") =>
        JsonSerializer.Serialize(new
        {
            type = "block_actions",
            user = new { id = userId },
            channel = new { id = channelId },
            message = new { ts = PlaceholderTs },
            actions = new[]
            {
                new { action_id = actionId, value = questionId }
            }
        });

    [Fact]
    public async Task EscalatedQuestion_OffersConfirmationWithButtons_AndDoesNotCreateTicketBeforeConfirm()
    {
        var (slackController, _, store, requests) = CreateControllers();
        var mention = JsonDocument.Parse(
            "{\"type\":\"event_callback\",\"event_id\":\"req-esc-1\",\"event\":{\"type\":\"app_mention\",\"user\":\"U123\",\"channel\":\"C123\",\"text\":\"<@U01BOT> How do I access finance portal?\"}}")
            .RootElement;

        var result = slackController.ReceiveEvent(mention);

        Assert.IsType<OkResult>(result);
        await WaitUntilAsync(() => requests.Any(r => r.Url.Contains("chat.update")));

        var update = Assert.Single(requests, r => r.Url.Contains("chat.update"));
        Assert.Contains("EscalationOfferPrompt", update.Body);
        Assert.Contains(SlackMessageFormatter.EscalationConfirmActionId, update.Body);
        Assert.Contains(SlackMessageFormatter.EscalationDeclineActionId, update.Body);
        Assert.Contains(TestQuestionId, update.Body);

        Assert.DoesNotContain(requests, r => r.Url.Contains("/api/v1/escalations"));
        Assert.True(store.TryGet(TestQuestionId, out var pending));
        Assert.Equal("employee@company.com", pending!.UserEmail);
    }

    [Fact]
    public async Task DecliningEscalation_EndsFlowWithoutCreatingTicket_AndRemovesPendingContext()
    {
        var (slackController, interactionsController, store, requests) = CreateControllers();
        var mention = JsonDocument.Parse(
            "{\"type\":\"event_callback\",\"event_id\":\"req-esc-1\",\"event\":{\"type\":\"app_mention\",\"user\":\"U123\",\"channel\":\"C123\",\"text\":\"<@U01BOT> How do I access finance portal?\"}}")
            .RootElement;

        slackController.ReceiveEvent(mention);
        await WaitUntilAsync(() => store.TryGet(TestQuestionId, out _));

        var declineResult = interactionsController.ReceiveInteraction(
            BuildBlockActionPayload(SlackMessageFormatter.EscalationDeclineActionId, TestQuestionId, userId: "U123"));

        Assert.IsType<OkResult>(declineResult);
        await WaitUntilAsync(() => requests.Any(r => r.Url.Contains("chat.update") && r.Body.Contains("EscalationDeclined")));

        Assert.DoesNotContain(requests, r => r.Url.Contains("/api/v1/escalations"));
        Assert.False(store.TryGet(TestQuestionId, out _));
    }

    [Fact]
    public async Task ConfirmingEscalation_SubmitsEscalationWithIdempotencyKey_AndDisplaysReferenceId()
    {
        var (slackController, interactionsController, store, requests) = CreateControllers();
        var mention = JsonDocument.Parse(
            "{\"type\":\"event_callback\",\"event_id\":\"req-esc-1\",\"event\":{\"type\":\"app_mention\",\"user\":\"U123\",\"channel\":\"C123\",\"text\":\"<@U01BOT> How do I access finance portal?\"}}")
            .RootElement;

        slackController.ReceiveEvent(mention);
        await WaitUntilAsync(() => store.TryGet(TestQuestionId, out _));

        var confirmResult = interactionsController.ReceiveInteraction(
            BuildBlockActionPayload(SlackMessageFormatter.EscalationConfirmActionId, TestQuestionId, userId: "U123"));

        Assert.IsType<OkResult>(confirmResult);
        await WaitUntilAsync(() => requests.Any(r => r.Url.Contains("chat.update") && r.Body.Contains("987e6543-e21b-12d3-a456-426614174000")));

        var escalationCall = Assert.Single(requests, r => r.Url.Contains("/api/v1/escalations"));
        Assert.False(string.IsNullOrWhiteSpace(escalationCall.IdempotencyKey));
        Assert.Equal("req-esc-1", escalationCall.CorrelationId);
        Assert.Contains($"\"question_id\":\"{TestQuestionId}\"", escalationCall.Body);
        Assert.Contains("\"confirmed\":true", escalationCall.Body);
        Assert.Contains("\"user_email\":\"employee@company.com\"", escalationCall.Body);
        Assert.False(store.TryGet(TestQuestionId, out _));
    }

    [Fact]
    public async Task NonRequesterClickingConfirm_IsIgnoredWithoutCreatingTicket()
    {
        var (slackController, interactionsController, store, requests) = CreateControllers();
        var mention = JsonDocument.Parse(
            "{\"type\":\"event_callback\",\"event_id\":\"req-esc-1\",\"event\":{\"type\":\"app_mention\",\"user\":\"U123\",\"channel\":\"C123\",\"text\":\"<@U01BOT> How do I access finance portal?\"}}")
            .RootElement;

        slackController.ReceiveEvent(mention);
        await WaitUntilAsync(() => store.TryGet(TestQuestionId, out _));

        var result = interactionsController.ReceiveInteraction(
            BuildBlockActionPayload(SlackMessageFormatter.EscalationConfirmActionId, TestQuestionId, userId: "U999_OTHER"));

        Assert.IsType<OkResult>(result);
        await Task.Delay(150);

        Assert.DoesNotContain(requests, r => r.Url.Contains("/api/v1/escalations"));
        Assert.True(store.TryGet(TestQuestionId, out _));
    }

    [Fact]
    public async Task ConfirmingAlreadyHandledOrExpiredQuestion_ShowsExpiredAndDoesNotDuplicate()
    {
        var (_, interactionsController, _, requests) = CreateControllers();

        var result = interactionsController.ReceiveInteraction(
            BuildBlockActionPayload(SlackMessageFormatter.EscalationConfirmActionId, TestQuestionId, userId: "U123"));

        Assert.IsType<OkResult>(result);
        await WaitUntilAsync(() => requests.Any(r => r.Url.Contains("chat.update") && r.Body.Contains("EscalationExpired")));

        Assert.DoesNotContain(requests, r => r.Url.Contains("/api/v1/escalations"));
    }

    [Fact]
    public async Task EscalationBackendFailure503_ShowsSafeFallbackWithoutLeakingInternalDetails()
    {
        var (slackController, interactionsController, store, requests) = CreateControllers(
            escalationResponse: (_, _) => Task.FromResult(Json(
                "{\"error_code\":\"ESCALATION_SERVICE_UNAVAILABLE\",\"message\":\"Internal DB failure\"}",
                HttpStatusCode.ServiceUnavailable)));

        var mention = JsonDocument.Parse(
            "{\"type\":\"event_callback\",\"event_id\":\"req-esc-1\",\"event\":{\"type\":\"app_mention\",\"user\":\"U123\",\"channel\":\"C123\",\"text\":\"<@U01BOT> How do I access finance portal?\"}}")
            .RootElement;

        slackController.ReceiveEvent(mention);
        await WaitUntilAsync(() => store.TryGet(TestQuestionId, out _));

        interactionsController.ReceiveInteraction(
            BuildBlockActionPayload(SlackMessageFormatter.EscalationConfirmActionId, TestQuestionId, userId: "U123"));

        await WaitUntilAsync(() => requests.Any(r => r.Url.Contains("chat.update") && r.Body.Contains("EscalationSubmitFailed")));

        var failureUpdate = Assert.Single(requests, r => r.Url.Contains("chat.update") && r.Body.Contains("EscalationSubmitFailed"));
        Assert.DoesNotContain("Internal DB failure", failureUpdate.Body);
        Assert.DoesNotContain("ESCALATION_SERVICE_UNAVAILABLE", failureUpdate.Body);
    }

    [Fact]
    public void ReceiveInteraction_MissingOrInvalidPayload_ReturnsBadRequest()
    {
        var (_, interactionsController, _, _) = CreateControllers();

        Assert.IsType<BadRequestObjectResult>(interactionsController.ReceiveInteraction(null));
        Assert.IsType<BadRequestObjectResult>(interactionsController.ReceiveInteraction("{not-json"));
    }

    [Fact]
    public async Task Transient503Failure_RestoresPendingContextAndRetriesWithSameIdempotencyKey()
    {
        int attempt = 0;
        var (slackController, interactionsController, store, requests) = CreateControllers(
            escalationResponse: (_, _) =>
            {
                if (Interlocked.Increment(ref attempt) == 1)
                {
                    return Task.FromResult(Json(
                        "{\"error_code\":\"ESCALATION_SERVICE_UNAVAILABLE\",\"message\":\"Temporary outage\"}",
                        HttpStatusCode.ServiceUnavailable));
                }

                return Task.FromResult(Json(JsonSerializer.Serialize(new EscalationCreateResponse
                {
                    EscalationId = "987e6543-e21b-12d3-a456-426614174000",
                    QuestionId = TestQuestionId,
                    CorrelationId = "req-esc-1",
                    Status = "PENDING_DESTINATION",
                    CreatedAt = "2026-11-22T19:00:00Z"
                }), HttpStatusCode.Created));
            });

        var mention = JsonDocument.Parse(
            "{\"type\":\"event_callback\",\"event_id\":\"req-esc-1\",\"event\":{\"type\":\"app_mention\",\"user\":\"U123\",\"channel\":\"C123\",\"text\":\"<@U01BOT> How do I access finance portal?\"}}")
            .RootElement;

        slackController.ReceiveEvent(mention);
        await WaitUntilAsync(() => store.TryGet(TestQuestionId, out _));

        interactionsController.ReceiveInteraction(
            BuildBlockActionPayload(SlackMessageFormatter.EscalationConfirmActionId, TestQuestionId, userId: "U123"));

        await WaitUntilAsync(() => requests.Any(r => r.Url.Contains("chat.update") && r.Body.Contains("EscalationSubmitFailed")));
        Assert.True(store.TryGet(TestQuestionId, out _));

        interactionsController.ReceiveInteraction(
            BuildBlockActionPayload(SlackMessageFormatter.EscalationConfirmActionId, TestQuestionId, userId: "U123"));

        await WaitUntilAsync(() => requests.Any(r => r.Url.Contains("chat.update") && r.Body.Contains("987e6543-e21b-12d3-a456-426614174000")));

        var escalationCalls = requests.Where(r => r.Url.Contains("/api/v1/escalations")).ToList();
        Assert.Equal(2, escalationCalls.Count);
        Assert.Equal(escalationCalls[0].IdempotencyKey, escalationCalls[1].IdempotencyKey);
        Assert.False(store.TryGet(TestQuestionId, out _));
    }

    [Fact]
    public async Task WrongChannelClickingConfirm_IsIgnoredWithoutCreatingTicket()
    {
        var (slackController, interactionsController, store, requests) = CreateControllers();
        var mention = JsonDocument.Parse(
            "{\"type\":\"event_callback\",\"event_id\":\"req-esc-1\",\"event\":{\"type\":\"app_mention\",\"user\":\"U123\",\"channel\":\"C123\",\"text\":\"<@U01BOT> How do I access finance portal?\"}}")
            .RootElement;

        slackController.ReceiveEvent(mention);
        await WaitUntilAsync(() => store.TryGet(TestQuestionId, out _));

        interactionsController.ReceiveInteraction(
            BuildBlockActionPayload(SlackMessageFormatter.EscalationConfirmActionId, TestQuestionId, userId: "U123", channelId: "C999_OTHER"));

        await Task.Delay(150);

        Assert.DoesNotContain(requests, r => r.Url.Contains("/api/v1/escalations"));
        Assert.True(store.TryGet(TestQuestionId, out _));
    }

    [Fact]
    public async Task EscalatedQuestion_FiltersInvalidChunkIdAndSanitizesInvalidUrl()
    {
        var validChunkId = "223e4567-e89b-12d3-a456-426614174000";
        var (slackController, _, store, requests) = CreateControllers(
            questionResponse: new UserQuestionResponse
            {
                QueryId = "req-esc-1",
                QuestionId = TestQuestionId,
                Status = "ESCALATED_TO_EXPERT",
                ReplyText = "Low confidence",
                Citations = new List<Citation>
                {
                    new() { DocumentId = "doc-bad", ChunkId = "not-a-uuid", Title = "Bad Chunk", Url = "https://example.com/bad", Score = 0.4f },
                    new() { DocumentId = "doc-good", ChunkId = validChunkId, Title = "Good Chunk", Url = "not-a-valid-uri", Score = 0.5f }
                }
            });

        var mention = JsonDocument.Parse(
            "{\"type\":\"event_callback\",\"event_id\":\"req-esc-1\",\"event\":{\"type\":\"app_mention\",\"user\":\"U123\",\"channel\":\"C123\",\"text\":\"<@U01BOT> Question?\"}}")
            .RootElement;

        slackController.ReceiveEvent(mention);
        await WaitUntilAsync(() => requests.Any(r => r.Url.Contains("chat.update")));

        Assert.True(store.TryGet(TestQuestionId, out var pending));
        var sourceRef = Assert.Single(pending!.SourceReferences);
        Assert.Equal("doc-good", sourceRef.DocumentId);
        Assert.Equal(validChunkId, sourceRef.ChunkId);
        Assert.Null(sourceRef.Url);
        Assert.Equal("LOW_CONFIDENCE", pending.EscalationReason);
    }
}
