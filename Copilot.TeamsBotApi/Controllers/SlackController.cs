using Copilot.Contracts;
using Copilot.TeamsBotApi.Helpers;
using Copilot.TeamsBotApi.Resources;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Copilot.TeamsBotApi.Controllers
{
    [ApiController]
    [Route("api/slack/events")]
    public class SlackController : ControllerBase
    {
        private const int MaxQueryLength = 4000;

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
                if (payload.TryGetProperty("challenge", out var challenge))
                {
                    return Ok(new { challenge = challenge.GetString() });
                }
                return BadRequest(new { error = "Missing challenge field." }); 
            }

            if (!payload.TryGetProperty("event", out var eventObj) || eventObj.ValueKind != JsonValueKind.Object)
            {
                return Ok();
            }

            var eventType = eventObj.TryGetProperty("type", out var et) ? et.GetString() : null;

            if (eventType != "app_mention" && eventType != "message")
            {
                return Ok();
            }

            if (eventObj.TryGetProperty("bot_id", out _) ||
                eventObj.TryGetProperty("app_id", out _) ||
                (eventObj.TryGetProperty("subtype", out var subtype) && !string.IsNullOrWhiteSpace(subtype.GetString())))
            {
                _logger.LogInformation("System event or message from a bot ignored.");
                return Ok();
            }

            var rawText = eventObj.TryGetProperty("text", out var textProp) ? textProp.GetString() : null;
            var channel = eventObj.TryGetProperty("channel", out var channelProp) ? channelProp.GetString() : null;
            var userId = eventObj.TryGetProperty("user", out var userProp) ? userProp.GetString() : null;

            var cleanQuestion = CleanBotMention(rawText);

            if (string.IsNullOrWhiteSpace(userId) ||
                string.IsNullOrWhiteSpace(channel) ||
                string.IsNullOrWhiteSpace(cleanQuestion) ||
                cleanQuestion.Length > MaxQueryLength)
            {
                _logger.LogWarning("Slack event rejected: required fields are missing or the length limit has been exceeded.");
                return BadRequest(new GatewayErrorResponse
                {
                    QueryId = null,
                    ErrorCode = "BAD_REQUEST",
                    UserMessage = "Your question is empty or exceeds the maximum allowed length."
                });
            }

            var queryId = Request?.Headers != null && Request.Headers.TryGetValue("X-Correlation-ID", out var corrHeader) && !string.IsNullOrWhiteSpace(corrHeader)
                ? corrHeader.ToString()
                : (payload.TryGetProperty("event_id", out var eventIdProp) && !string.IsNullOrWhiteSpace(eventIdProp.GetString())
                    ? eventIdProp.GetString()!
                    : Guid.NewGuid().ToString());

            _ = ProcessMessageAndRespondAsync(queryId, channel, userId, cleanQuestion);

            return Ok();
        }

        private static string CleanBotMention(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            return Regex.Replace(text, @"<@[A-Z0-9]+>", string.Empty).Trim();
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
                client.DefaultRequestHeaders.TryAddWithoutValidation("X-Correlation-ID", queryId);

                var apiUrl = _configuration["JavaCore:ChatApiUrl"];
                var coreResponse = await client.PostAsJsonAsync(apiUrl, coreRequest);

                if (!coreResponse.IsSuccessStatusCode)
                {
                    string fallbackMessage = _localizer["ProcessingError"];
                    try
                    {
                        var errorResult = await coreResponse.Content.ReadFromJsonAsync<GatewayErrorResponse>();
                        if (!string.IsNullOrWhiteSpace(errorResult?.UserMessage))
                        {
                            fallbackMessage = errorResult.UserMessage;
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "[{QueryId}] Backend returned invalid JSON error (Status: {StatusCode}).", queryId, coreResponse.StatusCode);
                    }

                    await SendMessageToSlackAsync(channel, fallbackMessage);
                    return;
                }

                UserQuestionResponse? coreResult = null;
                try
                {
                    coreResult = await coreResponse.Content.ReadFromJsonAsync<UserQuestionResponse>();
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "[{QueryId}] Failed to deserialize the response from the backend.", queryId);
                    await SendMessageToSlackAsync(channel, _localizer["ProcessingError"]);
                    return;
                }

                var missingSourceText = _localizer["SourceUnavailable"];
                var availableSourcesText = _localizer["AvailableSources"];

                var noAnswerLoc = _localizer["NoAnswerFound"];
                var noAnswerText = noAnswerLoc.ResourceNotFound
                    ? "Unfortunately, no confirmed information regarding your request was found in the available documents."
                    : noAnswerLoc.Value;

                var finalSlackMessage = SlackMessageFormatter.BuildFullSlackResponse(
                    coreResult,
                    availableSourcesText,
                    missingSourceText,
                    noAnswerText);

                await SendMessageToSlackAsync(channel, finalSlackMessage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{QueryId}] Unexpected Gateway integration error.", queryId);
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
