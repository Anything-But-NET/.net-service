using Copilot.Contracts;
using Copilot.TeamsBotApi.Helpers;
using Copilot.TeamsBotApi.Resources;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Copilot.TeamsBotApi.Controllers
{
    [ApiController]
    [Route("api/slack/events")]
    public class SlackController : ControllerBase
    {
        private readonly ILogger<SlackController> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;

        private readonly IStringLocalizer<BotMessages> _localizer;

        public SlackController(ILogger<SlackController> logger, IHttpClientFactory httpClientFactory, IConfiguration configuration, IStringLocalizer<BotMessages> localizer)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _localizer = localizer;
        }

        [HttpPost]
        public IActionResult ReceiveEvent([FromBody] JsonElement payload)
        {
            if (payload.TryGetProperty("type", out var type) && type.GetString() == "url_verification")
            {
                return Ok(new { challenge = payload.GetProperty("challenge").GetString() });
            }

            if (payload.TryGetProperty("event", out var eventObj))
            {
                var eventType = eventObj.GetProperty("type").GetString();

                if (eventType == "app_mention" || eventType == "message")
                {
                    if (eventObj.TryGetProperty("bot_id", out _) ||
                       (eventObj.TryGetProperty("subtype", out var subtype) && subtype.GetString() == "bot_message"))
                    {
                        return Ok();
                    }

                    var text = eventObj.GetProperty("text").GetString();
                    var channel = eventObj.GetProperty("channel").GetString();
                    var userId = eventObj.GetProperty("user").GetString();
                    var queryId = Guid.NewGuid().ToString();

                    _ = ProcessMessageAndRespondAsync(queryId, channel, userId, text);
                }
            }

            return Ok();
        }

        private async Task ProcessMessageAndRespondAsync(string queryId, string channel, string userId, string question)
        {
            try
            {
                var email = await GetUserEmailFromSlackAsync(userId);
                if (string.IsNullOrEmpty(email))
                {
                    await SendMessageToSlackAsync(channel, _localizer["EmailNotFound"]);
                    return;
                }

                var coreRequest = new UserQuestionRequest
                {
                    QueryId = queryId,
                    Channel = "slack",
                    UserEmail = email,
                    Query = question
                };

                var client = _httpClientFactory.CreateClient();
                var apiUrl = _configuration["JavaCore:ChatApiUrl"];
                var coreResponse = await client.PostAsJsonAsync(apiUrl, coreRequest);

                if (!coreResponse.IsSuccessStatusCode)
                {
                    var errorResult = await coreResponse.Content.ReadFromJsonAsync<GatewayErrorResponse>();
                    var fallbackMessage = errorResult?.UserMessage ?? _localizer["ProcessingError"];
                    await SendMessageToSlackAsync(channel, fallbackMessage);
                    return;
                }

                var coreResult = await coreResponse.Content.ReadFromJsonAsync<UserQuestionResponse>();

                var missingSourceText = _localizer["SourceUnavailable"];

                var formattedReplyText = SlackMessageFormatter.FormatLinksForSlack(coreResult.ReplyText, missingSourceText);

                var messageBuilder = new StringBuilder(formattedReplyText);

                if (coreResult.Citations != null && coreResult.Citations.Any())
                {
                    messageBuilder.AppendLine($"\n\n*{_localizer["AvailableSources"]}*");
                    for (int i = 0; i < coreResult.Citations.Count; i++)
                    {
                        var citation = coreResult.Citations[i];
                        messageBuilder.AppendLine($"[{i + 1}] <{citation.Url}|{citation.Title}>");
                    }
                }

                await SendMessageToSlackAsync(channel, messageBuilder.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{QueryId}] Непередбачена помилка інтеграції Gateway.", queryId);
                await SendMessageToSlackAsync(channel, _localizer["ServiceUnavailable"]);
            }
        }

        private async Task<string> GetUserEmailFromSlackAsync(string userId)
        {
            var token = _configuration["Slack:BotToken"];
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await client.GetAsync($"https://slack.com/api/users.info?user={userId}");
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadFromJsonAsync<JsonElement>();
                if (content.TryGetProperty("ok", out var ok) && ok.GetBoolean())
                {
                    var user = content.GetProperty("user");
                    if (user.TryGetProperty("profile", out var profile) && profile.TryGetProperty("email", out var email))
                    {
                        return email.GetString();
                    }
                }
            }

            return null;
        }

        private async Task SendMessageToSlackAsync(string channel, string text)
        {
            var token = _configuration["Slack:BotToken"];
            var client = _httpClientFactory.CreateClient();

            var request = new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/chat.postMessage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var payload = new { channel = channel, text = text };
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var response = await client.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();
            _logger.LogInformation("Відповідь від Slack: {Content}", content);
        }
    }
}
