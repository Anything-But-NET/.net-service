using System.Net;
using System.Text.Json;
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
using Xunit;

namespace Copilot.TeamsBotApi.Tests
{
    public class SlackControllerTests
    {
        private (SlackController Controller, Mock<HttpMessageHandler> HttpHandlerMock) CreateController(
            string? apiUrl = "http://java-service:8080/api/v1/chat/question")
        {
            var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
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

            var httpClient = new HttpClient(handlerMock.Object);
            var clientFactoryMock = new Mock<IHttpClientFactory>();
            clientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

            var configData = new Dictionary<string, string?>
            {
                ["JavaCore:ChatApiUrl"] = apiUrl,
                ["Slack:BotToken"] = "xoxb-mock-token"
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
    }
}
