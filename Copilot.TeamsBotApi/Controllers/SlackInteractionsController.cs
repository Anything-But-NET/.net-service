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
    private const string DefaultEscalationApiUrl = "http://java-service:8080/api/v1/chat/escalations";

    private readonly ILogger<SlackInteractionsController> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IStringLocalizer<BotMessages> _localizer;
    private readonly ISlackApiClient _slackApiClient;

    public SlackInteractionsController(
        ILogger<SlackInteractionsController> logger,
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
            var actionValue = firstAction.TryGetProperty("value", out var valueProp) ? valueProp.GetString() : null;

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
                string.IsNullOrWhiteSpace(actionValue))
            {
                return BadRequest(new { error = "Missing required interaction fields." });
            }

            var signingSecret = EscalationActionToken.ResolveSigningSecret(_configuration);
            if (!EscalationActionToken.TryDecode(actionValue, signingSecret, out var actionContext) || actionContext is null)
            {
                _logger.LogWarning("Rejected escalation action {ActionId}: invalid or tampered action value.", actionId);
                return BadRequest(new { error = "Invalid escalation action value." });
            }

            if (!string.Equals(actionContext.SlackUserId, userId, StringComparison.Ordinal) ||
                !string.Equals(actionContext.Channel, channelId, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Ignoring escalation action {ActionId} for query {QueryId} due to user/channel mismatch.",
                    actionId, actionContext.QueryId);
                return Ok();
            }

            _ = HandleInteractionAsync(actionId, actionContext, actionValue, channelId, messageTs);
            return Ok();
        }
    }

    private async Task HandleInteractionAsync(
        string actionId,
        EscalationActionContext actionContext,
        string actionValue,
        string channelId,
        string? messageTs)
    {
        try
        {
            if (actionId == SlackMessageFormatter.EscalationDeclineActionId)
            {
                _logger.LogInformation("[{QueryId}] User declined escalation.", actionContext.QueryId);
                await _slackApiClient.DeliverReplyAsync(channelId, messageTs, _localizer["EscalationDeclined"], Array.Empty<object>());
                return;
            }

            await _slackApiClient.DeliverReplyAsync(channelId, messageTs, _localizer["EscalationSubmitting"], Array.Empty<object>());

            CancellationTokenSource? timeoutCts = null;
            try
            {
                var timeoutSeconds = GetBackendTimeoutSeconds();
                timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

                var escalationRequest = new CreateEscalationRequest
                {
                    QueryId = actionContext.QueryId
                };

                var apiUrl = ResolveEscalationApiUrl(_configuration);
                using var backendRequest = new HttpRequestMessage(HttpMethod.Post, apiUrl)
                {
                    Content = JsonContent.Create(escalationRequest)
                };
                backendRequest.Headers.TryAddWithoutValidation("X-Correlation-ID", actionContext.QueryId);

                var client = _httpClientFactory.CreateClient("JavaCore");
                using var response = await client.SendAsync(backendRequest, timeoutCts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "[{QueryId}] Escalation creation failed with HTTP {StatusCode}.",
                        actionContext.QueryId, (int)response.StatusCode);

                    string fallbackMessage = response.StatusCode == HttpStatusCode.Gone
                        ? _localizer["EscalationExpired"]
                        : _localizer["EscalationSubmitFailed"];

                    try
                    {
                        var errorResult = await response.Content.ReadFromJsonAsync<GatewayErrorResponse>(timeoutCts.Token);
                        if (!string.IsNullOrWhiteSpace(errorResult?.UserMessage))
                        {
                            fallbackMessage = errorResult.UserMessage;
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "[{QueryId}] Escalation endpoint returned invalid JSON error (Status: {StatusCode}).",
                            actionContext.QueryId, response.StatusCode);
                    }

                    if ((int)response.StatusCode >= 500)
                    {
                        await OfferRetryAsync(actionValue, channelId, messageTs, fallbackMessage);
                        return;
                    }

                    await _slackApiClient.DeliverReplyAsync(channelId, messageTs, fallbackMessage, Array.Empty<object>());
                    return;
                }

                EscalationResponse? created = null;
                try
                {
                    created = await response.Content.ReadFromJsonAsync<EscalationResponse>(timeoutCts.Token);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "[{QueryId}] Failed to parse EscalationResponse.", actionContext.QueryId);
                }

                var refId = !string.IsNullOrWhiteSpace(created?.EscalationId)
                    ? created.EscalationId
                    : actionContext.QueryId;

                var confirmedMessage = string.Format(_localizer["EscalationConfirmed"], refId);
                await _slackApiClient.DeliverReplyAsync(channelId, messageTs, confirmedMessage, Array.Empty<object>());
            }
            catch (OperationCanceledException ex) when (timeoutCts?.IsCancellationRequested == true || ex.InnerException is TimeoutException)
            {
                _logger.LogWarning("[{QueryId}] Escalation request timed out.", actionContext.QueryId);
                await OfferRetryAsync(actionValue, channelId, messageTs, _localizer["RequestTimeout"]);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "[{QueryId}] Network error while submitting escalation.", actionContext.QueryId);
                await OfferRetryAsync(actionValue, channelId, messageTs, _localizer["EscalationSubmitFailed"]);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{QueryId}] Unexpected error while submitting escalation.", actionContext.QueryId);
                await _slackApiClient.DeliverReplyAsync(channelId, messageTs, _localizer["EscalationSubmitFailed"], Array.Empty<object>());
            }
            finally
            {
                timeoutCts?.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{QueryId}] Unhandled error in Slack interaction handler.", actionContext.QueryId);
        }
    }

    private async Task OfferRetryAsync(
        string actionValue,
        string channelId,
        string? messageTs,
        string errorMessage)
    {
        var retryBlocks = SlackMessageFormatter.BuildEscalationConfirmationBlocks(
            errorMessage,
            _localizer["EscalationConfirmButton"],
            _localizer["EscalationDeclineButton"],
            actionValue);

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
            return new Uri(chatUri, "/api/v1/chat/escalations").ToString();
        }

        return DefaultEscalationApiUrl;
    }
}
