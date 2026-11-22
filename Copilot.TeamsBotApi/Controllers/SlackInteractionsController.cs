using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Copilot.Contracts;
using Copilot.TeamsBotApi.Helpers;
using Copilot.TeamsBotApi.Resources;
using Copilot.TeamsBotApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Copilot.TeamsBotApi.Controllers;

[ApiController]
[Route("api/slack/interactions")]
public class SlackInteractionsController : ControllerBase
{
    private const int DefaultBackendTimeoutSeconds = 60;
    private const string DefaultEscalationApiUrl = "http://java-service:8080/api/v1/escalations";

    private readonly ILogger<SlackInteractionsController> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IStringLocalizer<BotMessages> _localizer;
    private readonly IPendingEscalationStore _pendingEscalationStore;

    public SlackInteractionsController(
        ILogger<SlackInteractionsController> logger,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IStringLocalizer<BotMessages> localizer,
        IPendingEscalationStore pendingEscalationStore)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _localizer = localizer;
        _pendingEscalationStore = pendingEscalationStore;
    }

    [HttpPost]
    [Consumes("application/x-www-form-urlencoded")]
    public IActionResult ReceiveInteraction([FromForm] string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return BadRequest(new { error = "Missing interaction payload." });
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return BadRequest(new { error = "Invalid interaction payload JSON." });
        }

        using (doc)
        {
            var root = doc.RootElement;
            var interactionType = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
            if (!string.Equals(interactionType, "block_actions", StringComparison.Ordinal))
            {
                return Ok();
            }

            if (!root.TryGetProperty("actions", out var actionsProp) ||
                actionsProp.ValueKind != JsonValueKind.Array ||
                actionsProp.GetArrayLength() == 0)
            {
                return Ok();
            }

            var firstAction = actionsProp[0];
            var actionId = firstAction.TryGetProperty("action_id", out var actionIdProp) ? actionIdProp.GetString() : null;
            var questionId = firstAction.TryGetProperty("value", out var valueProp) ? valueProp.GetString() : null;

            if (actionId != SlackMessageFormatter.EscalationConfirmActionId &&
                actionId != SlackMessageFormatter.EscalationDeclineActionId)
            {
                return Ok();
            }

            var userId = root.TryGetProperty("user", out var userProp) && userProp.TryGetProperty("id", out var userIdProp)
                ? userIdProp.GetString()
                : null;
            var channelId = root.TryGetProperty("channel", out var channelProp) && channelProp.TryGetProperty("id", out var channelIdProp)
                ? channelIdProp.GetString()
                : null;
            var messageTs = root.TryGetProperty("message", out var msgProp) && msgProp.TryGetProperty("ts", out var tsProp)
                ? tsProp.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(userId) ||
                string.IsNullOrWhiteSpace(channelId) ||
                string.IsNullOrWhiteSpace(questionId))
            {
                return BadRequest(new { error = "Missing required interaction fields." });
            }

            _ = HandleInteractionAsync(actionId, questionId, userId, channelId, messageTs);
            return Ok();
        }
    }

    private async Task HandleInteractionAsync(
        string actionId,
        string questionId,
        string slackUserId,
        string channelId,
        string? messageTs)
    {
        if (!_pendingEscalationStore.TryGet(questionId, out var existing) || existing is null)
        {
            await UpdateOrPostSlackMessageAsync(channelId, messageTs, _localizer["EscalationExpired"]);
            return;
        }

        if (!string.Equals(existing.SlackUserId, slackUserId, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "Ignoring escalation action {ActionId} for question {QuestionId} from non-requester user {UserId}.",
                actionId, questionId, slackUserId);
            return;
        }

        if (actionId == SlackMessageFormatter.EscalationDeclineActionId)
        {
            _pendingEscalationStore.Remove(questionId);
            _logger.LogInformation("[{CorrelationId}] User declined escalation for question {QuestionId}.",
                existing.CorrelationId, questionId);
            await UpdateOrPostSlackMessageAsync(channelId, messageTs, _localizer["EscalationDeclined"]);
            return;
        }

        if (!_pendingEscalationStore.TryTake(questionId, out var pending) || pending is null)
        {
            await UpdateOrPostSlackMessageAsync(channelId, messageTs, _localizer["EscalationExpired"]);
            return;
        }

        await UpdateOrPostSlackMessageAsync(channelId, messageTs, _localizer["EscalationSubmitting"]);

        CancellationTokenSource? timeoutCts = null;
        try
        {
            var timeoutSeconds = GetBackendTimeoutSeconds();
            timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

            var escalationRequest = new EscalationCreateRequest
            {
                QuestionId = pending.QuestionId,
                CorrelationId = pending.CorrelationId,
                Question = new EscalationQuestionPayload
                {
                    Text = pending.QuestionText,
                    Channel = "slack",
                    AskedAt = pending.AskedAt.ToString("yyyy-MM-ddTHH:mm:ssZ")
                },
                Requester = new EscalationRequesterPayload
                {
                    UserEmail = pending.UserEmail
                },
                UserConfirmation = new EscalationUserConfirmationPayload
                {
                    Confirmed = true,
                    ConfirmedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    ConfirmedBy = pending.UserEmail,
                    ConfirmedVia = "slack"
                },
                AnswerContext = new EscalationAnswerContextPayload
                {
                    AnswerText = pending.AnswerText,
                    ConfidenceScore = pending.ConfidenceScore,
                    SourceReferences = pending.SourceReferences
                },
                EscalationReason = pending.EscalationReason
            };

            var apiUrl = ResolveEscalationApiUrl(_configuration);
            using var backendRequest = new HttpRequestMessage(HttpMethod.Post, apiUrl)
            {
                Content = JsonContent.Create(escalationRequest)
            };
            backendRequest.Headers.TryAddWithoutValidation("Idempotency-Key", pending.IdempotencyKey);
            backendRequest.Headers.TryAddWithoutValidation("X-Correlation-ID", pending.CorrelationId);

            var client = _httpClientFactory.CreateClient("JavaCore");
            using var response = await client.SendAsync(backendRequest, timeoutCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "[{CorrelationId}] Escalation creation failed for question {QuestionId} with HTTP {StatusCode}.",
                    pending.CorrelationId, pending.QuestionId, (int)response.StatusCode);
                await UpdateOrPostSlackMessageAsync(channelId, messageTs, _localizer["EscalationSubmitFailed"]);
                return;
            }

            EscalationCreateResponse? created = null;
            try
            {
                created = await response.Content.ReadFromJsonAsync<EscalationCreateResponse>(timeoutCts.Token);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "[{CorrelationId}] Failed to parse EscalationCreateResponse.", pending.CorrelationId);
            }

            var refId = !string.IsNullOrWhiteSpace(created?.EscalationId)
                ? created.EscalationId
                : pending.QuestionId;

            var confirmedMessage = string.Format(_localizer["EscalationConfirmed"], refId);
            await UpdateOrPostSlackMessageAsync(channelId, messageTs, confirmedMessage);
        }
        catch (OperationCanceledException ex) when (timeoutCts?.IsCancellationRequested == true || ex.InnerException is TimeoutException)
        {
            _logger.LogWarning("[{CorrelationId}] Escalation request timed out for question {QuestionId}.",
                pending.CorrelationId, pending.QuestionId);
            await UpdateOrPostSlackMessageAsync(channelId, messageTs, _localizer["RequestTimeout"]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{CorrelationId}] Unexpected error while submitting escalation for question {QuestionId}.",
                pending.CorrelationId, pending.QuestionId);
            await UpdateOrPostSlackMessageAsync(channelId, messageTs, _localizer["EscalationSubmitFailed"]);
        }
        finally
        {
            timeoutCts?.Dispose();
        }
    }

    private int GetBackendTimeoutSeconds()
    {
        var raw = _configuration["JavaCore:TimeoutSeconds"];
        return int.TryParse(raw, out var parsed)
            ? Math.Max(1, parsed)
            : DefaultBackendTimeoutSeconds;
    }

    public static string ResolveEscalationApiUrl(IConfiguration configuration)
    {
        var explicitUrl = configuration["JavaCore:EscalationApiUrl"];
        if (!string.IsNullOrWhiteSpace(explicitUrl))
        {
            return explicitUrl.Trim();
        }

        var chatApiUrl = configuration["JavaCore:ChatApiUrl"];
        if (!string.IsNullOrWhiteSpace(chatApiUrl) && Uri.TryCreate(chatApiUrl, UriKind.Absolute, out var chatUri))
        {
            return new Uri(chatUri, "/api/v1/escalations").ToString();
        }

        return DefaultEscalationApiUrl;
    }

    private async Task UpdateOrPostSlackMessageAsync(string channel, string? ts, string text)
    {
        if (!string.IsNullOrWhiteSpace(ts))
        {
            var updated = await CallSlackApiAsync("chat.update", new { channel, ts, text, blocks = Array.Empty<object>() });
            if (updated is not null)
            {
                return;
            }
        }

        await CallSlackApiAsync("chat.postMessage", new { channel, text });
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
}
