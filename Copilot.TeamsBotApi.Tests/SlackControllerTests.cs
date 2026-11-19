using Copilot.Contracts;
using Copilot.TeamsBotApi.Controllers;
using Copilot.TeamsBotApi.Resources;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Copilot.TeamsBotApi.Tests
{
    public class SlackControllerTests
    {
        private (SlackController Controller, Mock<HttpMessageHandler> HttpHandlerMock) CreateController(
    string? apiUrl = "http://java-service:8080/api/v1/chat/question",
    Mock<HttpMessageHandler>? handlerMock = null,
    int? backendTimeoutSeconds = null)
        {
            if (handlerMock is null)
            {
                handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
                handlerMock
                    .Protected()
                    .Setup<Task<HttpResponseMessage>>(
                        "SendAsync",
                        ItExpr.IsAny<HttpRequestMessage>(),
                        ItExpr.IsAny<CancellationToken>())
                    .ReturnsAsync((HttpRequestMessage req, CancellationToken ct) =>
                    {
                        if (req.RequestUri!.ToString().Contains("slack.com"))
                        {
                            return new HttpResponseMessage
                            {
                                StatusCode = HttpStatusCode.OK,
                                Content = new StringContent("{\"ok\":true,\"user\":{\"profile\":{\"email\":\"test@company.com\"}}}")
                            };
                        }

                        var mockResponse = new UserQuestionResponse
                        {
                            QueryId = "req-123",
                            Status = "ANSWERED",
                            ReplyText = "To get compensation, fill out the form.",
                            Citations = new List<Citation>
                            {
                                new Citation
                                {
                                    Title = "IT Guidelines",
                                    Url = "https://company.sharepoint.com/docs/it",
                                    DocumentId = "doc-456",
                                    ChunkId = "123e4567-e89b-12d3-a456-426614174000",
                                    Score = 0.95f
                                }
                            }
                        };

                        return new HttpResponseMessage
                        {
                            StatusCode = HttpStatusCode.OK,
                            Content = new StringContent(JsonSerializer.Serialize(mockResponse), System.Text.Encoding.UTF8, "application/json")
                        };
                    });
            }

            var httpClient = new HttpClient(handlerMock.Object);
            var clientFactoryMock = new Mock<IHttpClientFactory>();
            clientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

            var configData = new Dictionary<string, string?>
            {
                ["JavaCore:ChatApiUrl"] = apiUrl,
                ["Slack:BotToken"] = "xoxb-mock-token",
                ["JavaCore:TimeoutSeconds"] = backendTimeoutSeconds?.ToString()
            };

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(configData)
                .Build();

            var localizerMock = new Mock<IStringLocalizer<BotMessages>>();
            localizerMock.Setup(l => l[It.IsAny<string>()]).Returns((string key) => new LocalizedString(key, key));

            var controller = new SlackController(
                NullLogger<SlackController>.Instance,
                clientFactoryMock.Object,
                configuration,
                localizerMock.Object)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext()
                }
            };

            return (controller, handlerMock);
        }

        private const string PlaceholderTs = "1700000000.000100";
        private static JsonElement ValidMentionPayload() => JsonDocument.Parse(
            "{\"type\":\"event_callback\",\"event_id\":\"req-123\",\"event\":{\"type\":\"app_mention\",\"user\":\"U123\",\"channel\":\"C123\",\"text\":\"<@BOT_ID> How to get compensation?\"}}")
            .RootElement;
        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        private static Mock<HttpMessageHandler> CreateRecordingHandler(
            ConcurrentQueue<(string Url, string Body)> requests,
            Func<CancellationToken, Task<HttpResponseMessage>>? backendResponse = null)
        {
            var handlerMock = new Mock<HttpMessageHandler>();
            handlerMock
                .Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .Returns(async (HttpRequestMessage req, CancellationToken ct) =>
                {
                    var url = req.RequestUri!.ToString();
                    var body = req.Content is null ? string.Empty : await req.Content.ReadAsStringAsync(ct);
                    requests.Enqueue((url, body));
                    if (url.Contains("users.info"))
                        return Json("{\"ok\":true,\"user\":{\"profile\":{\"email\":\"test@company.com\"}}}");
                    if (url.Contains("chat.postMessage"))
                        return Json($"{{\"ok\":true,\"ts\":\"{PlaceholderTs}\"}}");
                    if (url.Contains("chat.update"))
                        return Json("{\"ok\":true}");
                    return backendResponse is not null
                        ? await backendResponse(ct)
                        : Json(JsonSerializer.Serialize(new UserQuestionResponse
                        {
                            QueryId = "req-123",
                            Status = "ANSWERED",
                            ReplyText = "To get compensation, fill out the form.",
                            Citations = new List<Citation>()
                        }));
                });
            return handlerMock;
        }

        private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
        {
            var sw = Stopwatch.StartNew();
            while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
            {
                await Task.Delay(25);
            }
        }
        [Fact]
        public async Task ReceiveEvent_ValidUserMessage_PostsProcessingStateBeforeBackendCallAndReplacesIt()
        {
            var requests = new ConcurrentQueue<(string Url, string Body)>();
            var (controller, _) = CreateController(handlerMock: CreateRecordingHandler(requests));
            var result = controller.ReceiveEvent(ValidMentionPayload());
            Assert.IsType<OkResult>(result);
            await WaitUntilAsync(() => requests.Any(r => r.Url.Contains("chat.update")));
            var calls = requests.ToList();
            var placeholderIndex = calls.FindIndex(r => r.Url.Contains("chat.postMessage"));
            var backendIndex = calls.FindIndex(r => r.Url.Contains("java-service"));
            Assert.True(placeholderIndex >= 0 && placeholderIndex < backendIndex, "Processing state must be posted before the backend call.");
            Assert.Contains("Processing", calls[placeholderIndex].Body);
            var update = Assert.Single(calls, r => r.Url.Contains("chat.update"));
            Assert.Contains(PlaceholderTs, update.Body);
            Assert.Contains("To get compensation", update.Body);
            Assert.Single(calls, r => r.Url.Contains("chat.postMessage"));
        }
        [Fact]
        public async Task ReceiveEvent_BackendTimeout_ReplacesProcessingStateWithTimeoutMessage()
        {
            var requests = new ConcurrentQueue<(string Url, string Body)>();
            var handler = CreateRecordingHandler(requests, async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                throw new UnreachableException();
            });
            var (controller, _) = CreateController(handlerMock: handler, backendTimeoutSeconds: 1);
            controller.ReceiveEvent(ValidMentionPayload());
            await WaitUntilAsync(() => requests.Any(r => r.Url.Contains("chat.update")));
            var update = Assert.Single(requests, r => r.Url.Contains("chat.update"));
            Assert.Contains("RequestTimeout", update.Body);
        }
        [Fact]
        public async Task ReceiveEvent_BackendServiceError_ShowsUserMessageWithoutInternalDetails()
        {
            var requests = new ConcurrentQueue<(string Url, string Body)>();
            var handler = CreateRecordingHandler(requests, _ => Task.FromResult(Json(
                JsonSerializer.Serialize(new GatewayErrorResponse
                {
                    QueryId = "req-123",
                    ErrorCode = "INTERNAL_ERROR",
                    UserMessage = "The assistant is temporarily unavailable."
                }),
                HttpStatusCode.InternalServerError)));
            var (controller, _) = CreateController(handlerMock: handler);
            controller.ReceiveEvent(ValidMentionPayload());
            await WaitUntilAsync(() => requests.Any(r => r.Url.Contains("chat.update")));
            var update = Assert.Single(requests, r => r.Url.Contains("chat.update"));
            Assert.Contains("The assistant is temporarily unavailable.", update.Body);
            Assert.DoesNotContain("INTERNAL_ERROR", update.Body);
        }

        [Fact]
        public void ReceiveEvent_UrlVerification_ReturnsChallenge()
        {
            var (controller, _) = CreateController();
            var payload = JsonDocument.Parse("{\"type\":\"url_verification\",\"challenge\":\"test_challenge_123\"}").RootElement;

            var result = controller.ReceiveEvent(payload);

            var okResult = Assert.IsType<OkObjectResult>(result);
            Assert.Contains("test_challenge_123", okResult.Value!.ToString());
        }

        [Fact]
        public void ReceiveEvent_BotMessage_IgnoredWithoutBackendCall()
        {
            var (controller, handlerMock) = CreateController();
            var payload = JsonDocument.Parse("{\"type\":\"event_callback\",\"event\":{\"type\":\"message\",\"bot_id\":\"B12345\",\"text\":\"hello\",\"channel\":\"C123\"}}").RootElement;

            var result = controller.ReceiveEvent(payload);

            Assert.IsType<OkResult>(result);
            handlerMock.Protected().Verify(
                "SendAsync",
                Times.Never(),
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());
        }

        [Fact]
        public void ReceiveEvent_UnsupportedEventType_Ignored()
        {
            var (controller, handlerMock) = CreateController();
            var payload = JsonDocument.Parse("{\"type\":\"event_callback\",\"event\":{\"type\":\"reaction_added\",\"user\":\"U123\"}}").RootElement;

            var result = controller.ReceiveEvent(payload);

            Assert.IsType<OkResult>(result);
            handlerMock.Protected().Verify(
                "SendAsync",
                Times.Never(),
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());
        }

        [Fact]
        public void ReceiveEvent_ValidUserMessage_TriggersAsyncPipeline()
        {
            var (controller, handlerMock) = CreateController();
            var payload = JsonDocument.Parse("{\"type\":\"event_callback\",\"event_id\":\"req-123\",\"event\":{\"type\":\"app_mention\",\"user\":\"U123\",\"channel\":\"C123\",\"text\":\"<@BOT_ID> How to get compensation?\"}}").RootElement;

            var result = controller.ReceiveEvent(payload);

            Assert.IsType<OkResult>(result);

            Task.Delay(200).Wait();

            handlerMock.Protected().Verify(
                "SendAsync",
                Times.AtLeast(2),
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());
        }

        [Theory]
        [InlineData("")]
        [InlineData("not-a-number")]
        [InlineData("-5")]
        public async Task ReceiveEvent_InvalidTimeoutConfig_DoesNotHangOnProcessingState(string invalidTimeout)
        {
            var requests = new ConcurrentQueue<(string Url, string Body)>();
            var handlerMock = CreateRecordingHandler(requests);
            var httpClient = new HttpClient(handlerMock.Object);
            var clientFactoryMock = new Mock<IHttpClientFactory>();
            clientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["JavaCore:ChatApiUrl"] = "http://java-service:8080/api/v1/chat/question",
                    ["Slack:BotToken"] = "xoxb-mock-token",
                    ["JavaCore:TimeoutSeconds"] = invalidTimeout
                })
                .Build();
            var localizerMock = new Mock<IStringLocalizer<BotMessages>>();
            localizerMock.Setup(l => l[It.IsAny<string>()]).Returns((string key) => new LocalizedString(key, key));
            var controller = new SlackController(
                NullLogger<SlackController>.Instance,
                clientFactoryMock.Object,
                configuration,
                localizerMock.Object)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
            controller.ReceiveEvent(ValidMentionPayload());
            await WaitUntilAsync(() => requests.Any(r => r.Url.Contains("chat.update")));
            var update = Assert.Single(requests, r => r.Url.Contains("chat.update"));
            Assert.Contains("To get compensation", update.Body);
        }

        [Fact]
        public async Task ReceiveEvent_BackendRetrievalTimeout503_ReplacesProcessingStateWithServiceUnavailableMessage()
        {
            var requests = new ConcurrentQueue<(string Url, string Body)>();
            var handler = CreateRecordingHandler(requests, _ => Task.FromResult(Json(
                JsonSerializer.Serialize(new GatewayErrorResponse
                {
                    QueryId = "req-123",
                    ErrorCode = "SERVICE_UNAVAILABLE",
                    UserMessage = "The knowledge assistant is temporarily unavailable. Please try again in a few minutes."
                }),
                HttpStatusCode.ServiceUnavailable)));

            var (controller, _) = CreateController(handlerMock: handler);
            controller.ReceiveEvent(ValidMentionPayload());

            await WaitUntilAsync(() => requests.Any(r => r.Url.Contains("chat.update")));

            var update = Assert.Single(requests, r => r.Url.Contains("chat.update"));
            Assert.Contains("The knowledge assistant is temporarily unavailable.", update.Body);
            Assert.DoesNotContain("SERVICE_UNAVAILABLE", update.Body);
        }

        [Fact]
        public async Task ReceiveEvent_BackendReturnsNonJsonStackTrace_FallsBackToProcessingErrorWithoutLeaking()
        {
            var requests = new ConcurrentQueue<(string Url, string Body)>();
            var handler = CreateRecordingHandler(requests, _ => Task.FromResult(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.BadGateway,
                Content = new StringContent("java.lang.NullPointerException at com.internal.SecretRagService:42", Encoding.UTF8, "text/plain")
            }));

            var (controller, _) = CreateController(handlerMock: handler);
            controller.ReceiveEvent(ValidMentionPayload());

            await WaitUntilAsync(() => requests.Any(r => r.Url.Contains("chat.update")));

            var update = Assert.Single(requests, r => r.Url.Contains("chat.update"));
            Assert.Contains("ProcessingError", update.Body);
            Assert.DoesNotContain("NullPointerException", update.Body);
            Assert.DoesNotContain("SecretRagService", update.Body);
        }

        [Fact]
        public async Task ReceiveEvent_BackendConnectionFailure_ReplacesProcessingStateWithServiceUnavailable()
        {
            var requests = new ConcurrentQueue<(string Url, string Body)>();
            var handler = CreateRecordingHandler(requests, _ =>
                throw new HttpRequestException("Connection refused to http://java-service:8080/internal"));

            var (controller, _) = CreateController(handlerMock: handler);
            controller.ReceiveEvent(ValidMentionPayload());

            await WaitUntilAsync(() => requests.Any(r => r.Url.Contains("chat.update")));

            var update = Assert.Single(requests, r => r.Url.Contains("chat.update"));
            Assert.Contains("ServiceUnavailable", update.Body);
            Assert.DoesNotContain("Connection refused", update.Body);
        }

        [Fact]
        public async Task ReceiveEvent_ChatUpdateFails_FallsBackToPostingNewSlackMessage()
        {
            var requests = new ConcurrentQueue<(string Url, string Body)>();
            var handlerMock = new Mock<HttpMessageHandler>();
            handlerMock
                .Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .Returns(async (HttpRequestMessage req, CancellationToken ct) =>
                {
                    var url = req.RequestUri!.ToString();
                    var body = req.Content is null ? string.Empty : await req.Content.ReadAsStringAsync(ct);
                    requests.Enqueue((url, body));

                    if (url.Contains("users.info"))
                        return Json("{\"ok\":true,\"user\":{\"profile\":{\"email\":\"test@company.com\"}}}");
                    if (url.Contains("chat.postMessage"))
                        return Json($"{{\"ok\":true,\"ts\":\"{PlaceholderTs}\"}}");
                    if (url.Contains("chat.update"))
                        return new HttpResponseMessage(HttpStatusCode.BadGateway);

                    return Json(JsonSerializer.Serialize(new GatewayErrorResponse
                    {
                        QueryId = "req-123",
                        ErrorCode = "SERVICE_UNAVAILABLE",
                        UserMessage = "Knowledge service is temporarily busy."
                    }), HttpStatusCode.ServiceUnavailable);
                });

            var (controller, _) = CreateController(handlerMock: handlerMock);
            controller.ReceiveEvent(ValidMentionPayload());

            await WaitUntilAsync(() => requests.Count(r => r.Url.Contains("chat.postMessage")) == 2);

            var posts = requests.Where(r => r.Url.Contains("chat.postMessage")).ToList();
            Assert.Equal(2, posts.Count);
            Assert.Contains("Processing", posts[0].Body);
            Assert.Contains("Knowledge service is temporarily busy.", posts[1].Body);
        }

        [Fact]
        public async Task ReceiveEvent_RetryWithSameEventId_ForwardsIdenticalQueryIdAndHandlesDuplicateRejection()
        {
            var requests = new ConcurrentQueue<(string Url, string Body, string? CorrelationId)>();
            int backendCallCount = 0;

            var handlerMock = new Mock<HttpMessageHandler>();
            handlerMock
                .Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .Returns(async (HttpRequestMessage req, CancellationToken ct) =>
                {
                    var url = req.RequestUri!.ToString();
                    var body = req.Content is null ? string.Empty : await req.Content.ReadAsStringAsync(ct);
                    var corrId = req.Headers.TryGetValues("X-Correlation-ID", out var vals) ? vals.FirstOrDefault() : null;
                    requests.Enqueue((url, body, corrId));

                    if (url.Contains("users.info"))
                        return Json("{\"ok\":true,\"user\":{\"profile\":{\"email\":\"test@company.com\"}}}");
                    if (url.Contains("chat.postMessage"))
                        return Json($"{{\"ok\":true,\"ts\":\"{PlaceholderTs}\"}}");
                    if (url.Contains("chat.update"))
                        return Json("{\"ok\":true}");

                    var attempt = Interlocked.Increment(ref backendCallCount);
                    if (attempt == 1)
                    {
                        return Json(JsonSerializer.Serialize(new UserQuestionResponse
                        {
                            QueryId = "ev-retry-1",
                            Status = "ESCALATED_TO_EXPERT",
                            ReplyText = "I couldn't find a confident answer. I have created a support ticket.",
                            Citations = new List<Citation>()
                        }));
                    }

                    return Json(JsonSerializer.Serialize(new GatewayErrorResponse
                    {
                        QueryId = "ev-retry-1",
                        ErrorCode = "INTERNAL_ERROR",
                        UserMessage = "An unexpected error occurred while processing your request."
                    }), HttpStatusCode.InternalServerError);
                });

            var (controller, _) = CreateController(handlerMock: handlerMock);
            var retryPayload = JsonDocument.Parse(
                "{\"type\":\"event_callback\",\"event_id\":\"ev-retry-1\",\"event\":{\"type\":\"app_mention\",\"user\":\"U123\",\"channel\":\"C123\",\"text\":\"<@BOT_ID> How to get compensation?\"}}")
                .RootElement;

            controller.ReceiveEvent(retryPayload);
            await WaitUntilAsync(() => requests.Count(r => r.Url.Contains("chat.update")) == 1);

            controller.ReceiveEvent(retryPayload);
            await WaitUntilAsync(() => requests.Count(r => r.Url.Contains("chat.update")) == 2);

            var backendCalls = requests.Where(r => r.Url.Contains("java-service")).ToList();
            Assert.Equal(2, backendCalls.Count);
            Assert.All(backendCalls, call =>
            {
                Assert.Equal("ev-retry-1", call.CorrelationId);
                Assert.Contains("\"query_id\":\"ev-retry-1\"", call.Body);
            });

            var updates = requests.Where(r => r.Url.Contains("chat.update")).Select(r => r.Body).ToList();
            Assert.Single(updates, body => body.Contains("created a support ticket"));
            Assert.Single(updates, body => body.Contains("An unexpected error occurred while processing your request."));
        }
    }
}
