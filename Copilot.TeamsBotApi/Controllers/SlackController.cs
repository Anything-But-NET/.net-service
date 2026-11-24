using Copilot.Contracts;
using Copilot.TeamsBotApi.Helpers;
using Copilot.TeamsBotApi.Resources;
using Copilot.TeamsBotApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Copilot.TeamsBotApi.Controllers
{
    [ApiController]
    [Route("api/slack/events")]
    public class SlackController : ControllerBase
    {
        private const int MaxQueryLength = 4000;
        private const int DefaultBackendTimeoutSeconds = 60;

        private readonly ILogger<SlackController> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
        private readonly IStringLocalizer<BotMessages> _localizer;
        private readonly ISlackApiClient _slackApiClient;

        public SlackController(
            ILogger<SlackController> logger,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            IStringLocalizer<BotMessages> localizer,
            ISlackApiClient? slackApiClient = null)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _localizer = localizer;
            _slackApiClient = slackApiClient ?? new SlackApiClient(httpClientFactory, configuration, logger);
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

        private int GetBackendTimeoutSeconds()
        {
            var raw = _configuration["JavaCore:TimeoutSeconds"];
            return int.TryParse(raw, out var parsed)
                ? Math.Max(1, parsed)
                : DefaultBackendTimeoutSeconds;
        }

        private async Task ProcessMessageAndRespondAsync(string queryId, string channel, string userId, string question)
        {
            var placeholderTs = await _slackApiClient.PostMessageAsync(channel, _localizer["Processing"]);

            CancellationTokenSource? timeoutCts = null;
            var timeoutSeconds = DefaultBackendTimeoutSeconds;

            try
            {
                timeoutSeconds = GetBackendTimeoutSeconds();
                timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

                var email = await _slackApiClient.GetUserEmailAsync(userId);
                if (string.IsNullOrEmpty(email))
                {
                    await _slackApiClient.DeliverReplyAsync(channel, placeholderTs, _localizer["EmailNotFound"]);
                    return;
                }

                var coreRequest = new UserQuestionRequest
                {
                    QueryId = queryId,
                    Channel = "slack",
                    UserEmail = email,
                    Query = question
                };

                var apiUrl = _configuration["JavaCore:ChatApiUrl"];

                using var backendRequest = new HttpRequestMessage(HttpMethod.Post, apiUrl)
                {
                    Content = JsonContent.Create(coreRequest)
                };
                backendRequest.Headers.TryAddWithoutValidation("X-Correlation-ID", queryId);

                var client = _httpClientFactory.CreateClient("JavaCore");
                using var coreResponse = await client.SendAsync(backendRequest, timeoutCts.Token);

                if (!coreResponse.IsSuccessStatusCode)
                {
                    string fallbackMessage = _localizer["ProcessingError"];
                    try
                    {
                        var errorResult = await coreResponse.Content.ReadFromJsonAsync<GatewayErrorResponse>(timeoutCts.Token);
                        if (!string.IsNullOrWhiteSpace(errorResult?.UserMessage))
                        {
                            fallbackMessage = errorResult.UserMessage;
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "[{QueryId}] Backend returned invalid JSON error (Status: {StatusCode}).", queryId, coreResponse.StatusCode);
                    }

                    await _slackApiClient.DeliverReplyAsync(channel, placeholderTs, fallbackMessage);
                    return;
                }

                UserQuestionResponse? coreResult;
                try
                {
                    coreResult = await coreResponse.Content.ReadFromJsonAsync<UserQuestionResponse>(timeoutCts.Token);
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "[{QueryId}] Failed to deserialize the response from the backend.", queryId);
                    await _slackApiClient.DeliverReplyAsync(channel, placeholderTs, _localizer["ProcessingError"]);
                    return;
                }

                if (string.Equals(coreResult?.Status, "ESCALATED_TO_EXPERT", StringComparison.OrdinalIgnoreCase))
                {
                    var escalationQueryId = !string.IsNullOrWhiteSpace(coreResult!.QueryId)
                        ? coreResult.QueryId
                        : queryId;

                    var actionValue = EscalationActionToken.Encode(
                        new EscalationActionContext(escalationQueryId, userId, channel),
                        EscalationActionToken.ResolveSigningSecret(_configuration));

                    var promptText = _localizer["EscalationOfferPrompt"];
                    var confirmText = _localizer["EscalationConfirmButton"];
                    var declineText = _localizer["EscalationDeclineButton"];
                    var blocks = SlackMessageFormatter.BuildEscalationConfirmationBlocks(
                        promptText,
                        confirmText,
                        declineText,
                        actionValue);

                    await _slackApiClient.DeliverReplyAsync(channel, placeholderTs, promptText, blocks);
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

                await _slackApiClient.DeliverReplyAsync(channel, placeholderTs, finalSlackMessage);
            }
            catch (OperationCanceledException ex) when (timeoutCts?.IsCancellationRequested == true || ex.InnerException is TimeoutException)
            {
                _logger.LogWarning("[{QueryId}] Backend did not respond within {TimeoutSeconds}s.", queryId, timeoutSeconds);
                await _slackApiClient.DeliverReplyAsync(channel, placeholderTs, _localizer["RequestTimeout"]);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{QueryId}] Unexpected Gateway integration error.", queryId);
                await _slackApiClient.DeliverReplyAsync(channel, placeholderTs, _localizer["ServiceUnavailable"]);
            }
            finally { timeoutCts?.Dispose(); }
        }
    }
}
