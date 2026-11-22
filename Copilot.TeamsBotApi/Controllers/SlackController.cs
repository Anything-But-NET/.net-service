using Copilot.Contracts;
using Copilot.TeamsBotApi.Helpers;
using Copilot.TeamsBotApi.Resources;
using Copilot.TeamsBotApi.Services;
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
        private const int DefaultBackendTimeoutSeconds = 60;
        private static readonly TimeSpan PendingEscalationTtl = TimeSpan.FromMinutes(15);

        private readonly ILogger<SlackController> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
        private readonly IStringLocalizer<BotMessages> _localizer;
        private readonly IPendingEscalationStore _pendingEscalationStore;

        public SlackController(
            ILogger<SlackController> logger,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            IStringLocalizer<BotMessages> localizer,
            IPendingEscalationStore? pendingEscalationStore = null)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _localizer = localizer;
            _pendingEscalationStore = pendingEscalationStore ?? new InMemoryPendingEscalationStore();
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
            var askedAt = DateTimeOffset.UtcNow;
            var placeholderTs = await PostSlackMessageAsync(channel, _localizer["Processing"]);

            CancellationTokenSource? timeoutCts = null;
            var timeoutSeconds = DefaultBackendTimeoutSeconds;

            try
            {
                timeoutSeconds = GetBackendTimeoutSeconds();
                timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

                var email = await GetUserEmailFromSlackAsync(userId);
                if (string.IsNullOrEmpty(email))
                {
                    await DeliverReplyAsync(channel, placeholderTs, _localizer["EmailNotFound"]);
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

                    await DeliverReplyAsync(channel, placeholderTs, fallbackMessage);
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
                    await DeliverReplyAsync(channel, placeholderTs, _localizer["ProcessingError"]);
                    return;
                }

                if (string.Equals(coreResult?.Status, "ESCALATED_TO_EXPERT", StringComparison.OrdinalIgnoreCase))
                {
                    var questionId = Guid.TryParse(coreResult!.QuestionId, out _)
                        ? coreResult.QuestionId!
                        : Guid.NewGuid().ToString();

                    var sourceRefs = (coreResult.Citations ?? new List<Citation>())
                        .Where(c => c != null && !string.IsNullOrWhiteSpace(c.DocumentId) && !string.IsNullOrWhiteSpace(c.ChunkId))
                        .Select(c => new EscalationSourceReference
                        {
                            DocumentId = c.DocumentId,
                            ChunkId = c.ChunkId,
                            Title = string.IsNullOrWhiteSpace(c.Title) ? null : c.Title,
                            Url = string.IsNullOrWhiteSpace(c.Url) ? null : c.Url,
                            Score = c.Score
                        })
                        .ToList();

                    var pending = new PendingEscalationContext(
                        QuestionId: questionId,
                        CorrelationId: queryId,
                        IdempotencyKey: Guid.NewGuid().ToString(),
                        QuestionText: question,
                        AskedAt: askedAt,
                        UserEmail: email,
                        SlackUserId: userId,
                        Channel: channel,
                        AnswerText: null,
                        ConfidenceScore: null,
                        SourceReferences: sourceRefs,
                        EscalationReason: sourceRefs.Count == 0 ? "NO_RELEVANT_CONTEXT" : "LOW_CONFIDENCE",
                        ExpiresAt: DateTimeOffset.UtcNow.Add(PendingEscalationTtl));

                    _pendingEscalationStore.Save(pending);

                    var promptText = _localizer["EscalationOfferPrompt"];
                    var confirmText = _localizer["EscalationConfirmButton"];
                    var declineText = _localizer["EscalationDeclineButton"];
                    var blocks = SlackMessageFormatter.BuildEscalationConfirmationBlocks(
                        promptText,
                        confirmText,
                        declineText,
                        questionId);

                    await DeliverReplyAsync(channel, placeholderTs, promptText, blocks);
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

                await DeliverReplyAsync(channel, placeholderTs, finalSlackMessage);
            }
            catch (OperationCanceledException ex) when (timeoutCts?.IsCancellationRequested == true || ex.InnerException is TimeoutException)
            {
                _logger.LogWarning("[{QueryId}] Backend did not respond within {TimeoutSeconds}s.", queryId, timeoutSeconds);
                await DeliverReplyAsync(channel, placeholderTs, _localizer["RequestTimeout"]);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{QueryId}] Unexpected Gateway integration error.", queryId);
                await DeliverReplyAsync(channel, placeholderTs, _localizer["ServiceUnavailable"]);
            }
            finally { timeoutCts?.Dispose(); }
        }

        private async Task DeliverReplyAsync(string channel, string? placeholderTs, string text, object[]? blocks = null)
        {
            if (placeholderTs is not null && await UpdateSlackMessageAsync(channel, placeholderTs, text, blocks))
            {
                return;
            }

            await PostSlackMessageAsync(channel, text, blocks);
        }

        private async Task<string?> PostSlackMessageAsync(string channel, string text, object[]? blocks = null)
        {
            object payload = blocks is not null
                ? new { channel, text, blocks }
                : new { channel, text };

            var result = await CallSlackApiAsync("chat.postMessage", payload);

            if (result is { } content && content.TryGetProperty("ts", out var ts))
            {
                return ts.GetString();
            }

            return null;
        }

        private async Task<bool> UpdateSlackMessageAsync(string channel, string ts, string text, object[]? blocks = null)
        {
            object payload = blocks is not null
                ? new { channel, ts, text, blocks }
                : new { channel, ts, text };

            return await CallSlackApiAsync("chat.update", payload) is not null;
        }

        private async Task<JsonElement?> CallSlackApiAsync(string method, object payload)
        {
            try
            {
                var token = _configuration["Slack:BotToken"];
                var client = _httpClientFactory.CreateClient();

                using var request = new HttpRequestMessage(HttpMethod.Post, $"https://slack.com/api/{method}");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                using var response = await client.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Slack API {Method} failed with HTTP status {StatusCode}.", method, (int)response.StatusCode);
                    return null;
                }

                var content = await response.Content.ReadFromJsonAsync<JsonElement>();

                if (content.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                {
                    return content;
                }

                var error = content.TryGetProperty("error", out var errorProp) ? errorProp.GetString() : "unknown_error";
                _logger.LogWarning("Slack API {Method} failed: {SlackError}", method, error);
                return null;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
            {
                _logger.LogWarning(ex, "Slack API {Method} call failed.", method);
                return null;
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
    }
}
