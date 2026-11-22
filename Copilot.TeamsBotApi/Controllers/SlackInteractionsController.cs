using System.Net;
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
    private readonly ISlackApiClient _slackApiClient;

    public SlackInteractionsController(
        ILogger<SlackInteractionsController> logger,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IStringLocalizer<BotMessages> localizer,
        IPendingEscalationStore pendingEscalationStore,
        ISlackApiClient? slackApiClient = null)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _localizer = localizer;
        _pendingEscalationStore = pendingEscalationStore;
        _slackApiClient = slackApiClient ?? new SlackApiClient(httpClientFactory, configuration, logger);
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
            await _slackApiClient.DeliverReplyAsync(channelId, messageTs, _localizer["EscalationExpired"], Array.Empty<object>());
            return;
        }

        if (!string.Equals(existing.SlackUserId, slackUserId, StringComparison.Ordinal) ||
            !string.Equals(existing.Channel, channelId, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "Ignoring escalation action {ActionId} for question {QuestionId} due to user/channel mismatch.",
                actionId, questionId);
            return;
        }

        if (actionId == SlackMessageFormatter.EscalationDeclineActionId)
        {
            _pendingEscalationStore.Remove(questionId);
            _logger.LogInformation("[{CorrelationId}] User declined escalation for question {QuestionId}.",
                existing.CorrelationId, questionId);
            await _slackApiClient.DeliverReplyAsync(channelId, messageTs, _localizer["EscalationDeclined"], Array.Empty<object>());
            return;
        }

        if (!_pendingEscalationStore.TryTake(questionId, out var pending) || pending is null)
        {
            await _slackApiClient.DeliverReplyAsync(channelId, messageTs, _localizer["EscalationExpired"], Array.Empty<object>());
            return;
        }

        await _slackApiClient.DeliverReplyAsync(channelId, messageTs, _localizer["EscalationSubmitting"], Array.Empty<object>());

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

                if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    await RestoreAndOfferRetryAsync(pending, channelId, messageTs, _localizer["EscalationSubmitFailed"]);
                    return;
                }

                await _slackApiClient.DeliverReplyAsync(channelId, messageTs, _localizer["EscalationSubmitFailed"], Array.Empty<object>());
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
            await _slackApiClient.DeliverReplyAsync(channelId, messageTs, confirmedMessage, Array.Empty<object>());
        }
        catch (OperationCanceledException ex) when (timeoutCts?.IsCancellationRequested == true || ex.InnerException is TimeoutException)
        {
            _logger.LogWarning("[{CorrelationId}] Escalation request timed out for question {QuestionId}.",
                pending.CorrelationId, pending.QuestionId);
            await RestoreAndOfferRetryAsync(pending, channelId, messageTs, _localizer["RequestTimeout"]);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "[{CorrelationId}] Network error while submitting escalation for question {QuestionId}.",
                pending.CorrelationId, pending.QuestionId);
            await RestoreAndOfferRetryAsync(pending, channelId, messageTs, _localizer["EscalationSubmitFailed"]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{CorrelationId}] Unexpected error while submitting escalation for question {QuestionId}.",
                pending.CorrelationId, pending.QuestionId);
            await _slackApiClient.DeliverReplyAsync(channelId, messageTs, _localizer["EscalationSubmitFailed"], Array.Empty<object>());
        }
        finally
        {
            timeoutCts?.Dispose();
        }
    }

    private async Task RestoreAndOfferRetryAsync(
        PendingEscalationContext pending,
        string channelId,
        string? messageTs,
        string errorMessage)
    {
        _pendingEscalationStore.Save(pending);

        var retryBlocks = SlackMessageFormatter.BuildEscalationConfirmationBlocks(
            errorMessage,
            _localizer["EscalationConfirmButton"],
            _localizer["EscalationDeclineButton"],
            pending.QuestionId);

        await _slackApiClient.DeliverReplyAsync(channelId, messageTs, errorMessage, retryBlocks);
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
}
