using System.Collections.Concurrent;
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

namespace Copilot.TeamsBotApi.Tests;

// SCRUM-98: exercise the real Slack controllers and pending store, with HTTP
// and Slack as test doubles. No external services or live tickets are used.
public class EscalationConfirmationGateRegressionTests
{
    private const string QuestionId = "123e4567-e89b-12d3-a456-426614174000";
    private const string EscalationId = "987e6543-e21b-12d3-a456-426614174000";
    private const string CorrelationId = QuestionId;
    private const string UserId = "U123";
    private const string ChannelId = "C123";
    private const string Email = "employee@example.com";
    private const string MessageTs = "1700000000.000100";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task NoUserResponse_OffersConfirmationButNeverSubmitsEscalation()
    {
        using var fixture = new Fixture();
        await fixture.OfferAsync();

        var offer = Assert.Single(fixture.Slack.Replies.Where(r => r.Text == "EscalationOfferPrompt"));
        var blocks = JsonSerializer.Serialize(offer.Blocks);
        Assert.Contains(SlackMessageFormatter.EscalationConfirmActionId, blocks);
        Assert.Contains(SlackMessageFormatter.EscalationDeclineActionId, blocks);
        Assert.True(fixture.Store.TryGet(QuestionId, out _));
        Assert.Empty(fixture.Backend.EscalationCalls);
    }

    [Fact]
    public async Task Decline_AndLateConfirmation_DoNotCreateEscalation()
    {
        using var fixture = new Fixture();
        await fixture.OfferAsync();

        fixture.Click(SlackMessageFormatter.EscalationDeclineActionId);
        await fixture.Slack.WaitForAsync("EscalationDeclined");
        Assert.False(fixture.Store.TryGet(QuestionId, out _));
        Assert.Empty(fixture.Backend.EscalationCalls);

        // A stale confirmation button must not resurrect a declined request.
        fixture.Click(SlackMessageFormatter.EscalationConfirmActionId);
        await fixture.Slack.WaitForAsync("EscalationExpired");
        Assert.Empty(fixture.Backend.EscalationCalls);
    }

    [Fact]
    public async Task ExpiredPendingConfirmation_DoesNotCreateEscalation()
    {
        using var fixture = new Fixture();
        await fixture.OfferAsync();
        Assert.True(fixture.Store.TryGet(QuestionId, out var pending));

        // Represent an elapsed TTL without sleeping for 15 minutes.
        fixture.Store.Save(pending! with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        fixture.Click(SlackMessageFormatter.EscalationConfirmActionId);
        await fixture.Slack.WaitForAsync("EscalationExpired");

        Assert.False(fixture.Store.TryGet(QuestionId, out _));
        Assert.Empty(fixture.Backend.EscalationCalls);
    }

    [Fact]
    public async Task ExplicitConfirmation_SubmitsExactlyOneRequestWithExplicitConsent()
    {
        using var fixture = new Fixture();
        await fixture.OfferAsync();
        Assert.Empty(fixture.Backend.EscalationCalls);
        Assert.True(fixture.Store.TryGet(QuestionId, out var pending));

        fixture.Click(SlackMessageFormatter.EscalationConfirmActionId);
        await fixture.Slack.WaitForAsync($"Confirmed: {EscalationId}");

        var call = Assert.Single(fixture.Backend.EscalationCalls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.False(string.IsNullOrWhiteSpace(call.IdempotencyKey));
        Assert.Equal(pending!.IdempotencyKey, call.IdempotencyKey);
        Assert.Equal(CorrelationId, call.CorrelationId);
        using var body = JsonDocument.Parse(call.Body);
        AssertRequestIdentityAndConsent(body.RootElement);
        Assert.False(fixture.Store.TryGet(QuestionId, out _));
    }

    [Fact]
    public async Task RepeatedConfirmationAfterSuccess_DoesNotSubmitAgain()
    {
        using var fixture = new Fixture();
        await fixture.OfferAsync();
        fixture.Click(SlackMessageFormatter.EscalationConfirmActionId);
        await fixture.Slack.WaitForAsync($"Confirmed: {EscalationId}");

        fixture.Click(SlackMessageFormatter.EscalationConfirmActionId);
        await fixture.Slack.WaitForAsync("EscalationExpired");

        Assert.Single(fixture.Backend.EscalationCalls);
        Assert.False(fixture.Store.TryGet(QuestionId, out _));
    }

    [Fact]
    public async Task DuplicateConfirmationWhileFirstRequestIsInFlight_SubmitsOnlyOnce()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(async (_, ct) =>
        {
            entered.TrySetResult(true);
            await release.Task.WaitAsync(ct);
            return SuccessResponse();
        });
        await fixture.OfferAsync();
        fixture.Click(SlackMessageFormatter.EscalationConfirmActionId);
        try
        {
            await entered.Task.WaitAsync(TestTimeout);
            fixture.Click(SlackMessageFormatter.EscalationConfirmActionId);
            await fixture.Slack.WaitForAsync("EscalationExpired");
            Assert.Single(fixture.Backend.EscalationCalls);
        }
        finally
        {
            release.TrySetResult(true);
        }
        await fixture.Slack.WaitForAsync($"Confirmed: {EscalationId}");
        Assert.Single(fixture.Backend.EscalationCalls);
    }

    [Fact]
    public async Task LostResponseAfterSimulatedCommit_RetryPreservesIdempotencyAndCorrelation()
    {
        // The destination is simulated: a successful remote commit can precede
        // a lost HTTP response. This verifies the client retry identity, not a real DB.
        var records = new ConcurrentDictionary<string, string>();
        var attempts = 0;
        using var fixture = new Fixture((call, _) =>
        {
            var key = call.IdempotencyKey ?? throw new InvalidOperationException("Missing idempotency key");
            var id = records.GetOrAdd(key, _ => EscalationId);
            if (Interlocked.Increment(ref attempts) == 1)
                throw new HttpRequestException("Response lost after simulated commit");
            return Task.FromResult(SuccessResponse(id));
        });
        await fixture.OfferAsync();
        fixture.Click(SlackMessageFormatter.EscalationConfirmActionId);
        await fixture.Slack.WaitForAsync("EscalationSubmitFailed");
        Assert.True(fixture.Store.TryGet(QuestionId, out _));
        Assert.Single(fixture.Backend.EscalationCalls);

        fixture.Click(SlackMessageFormatter.EscalationConfirmActionId);
        await fixture.Slack.WaitForAsync($"Confirmed: {EscalationId}");
        var calls = fixture.Backend.EscalationCalls.ToArray();
        Assert.Equal(2, calls.Length);
        Assert.False(string.IsNullOrWhiteSpace(calls[0].IdempotencyKey));
        Assert.Equal(calls[0].IdempotencyKey, calls[1].IdempotencyKey);
        Assert.Equal(CorrelationId, calls[0].CorrelationId);
        Assert.Equal(calls[0].CorrelationId, calls[1].CorrelationId);
        foreach (var call in calls)
        {
            using var body = JsonDocument.Parse(call.Body);
            AssertRequestIdentityAndConsent(body.RootElement);
        }
        Assert.Single(records);
        Assert.False(fixture.Store.TryGet(QuestionId, out _));
    }

    [Fact]
    public async Task RetryOfferThenDecline_PreventsAnyFurtherSubmission()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(
            JsonResponse("{}", HttpStatusCode.ServiceUnavailable)));
        await fixture.OfferAsync();
        fixture.Click(SlackMessageFormatter.EscalationConfirmActionId);
        await fixture.Slack.WaitForAsync("EscalationSubmitFailed");
        Assert.Single(fixture.Backend.EscalationCalls);
        Assert.True(fixture.Store.TryGet(QuestionId, out _));

        fixture.Click(SlackMessageFormatter.EscalationDeclineActionId);
        await fixture.Slack.WaitForAsync("EscalationDeclined");
        fixture.Click(SlackMessageFormatter.EscalationConfirmActionId);
        await fixture.Slack.WaitForAsync("EscalationExpired");

        Assert.Single(fixture.Backend.EscalationCalls);
        Assert.False(fixture.Store.TryGet(QuestionId, out _));
    }

    [Fact]
    public async Task UnknownAction_IsNotImplicitConsent()
    {
        using var fixture = new Fixture();
        await fixture.OfferAsync();
        fixture.Click("unrelated_action");
        Assert.Empty(fixture.Backend.EscalationCalls);
        Assert.True(fixture.Store.TryGet(QuestionId, out _));
    }

    [Fact]
    public async Task CurrentJavaCoreContract_ConfirmationUsesOriginalQueryIdAndChatEndpoint()
    {
        // java-service main exposes POST /api/v1/chat/escalations with query_id.
        // Its question response contains query_id, not question_id. This test
        // deliberately detects the cross-service contract mismatch in PR #18.
        const string originalQueryId = "req-scrum-98-contract";
        var coreQuestionResponse = JsonSerializer.Serialize(new
        {
            query_id = originalQueryId,
            status = "ESCALATED_TO_EXPERT",
            reply_text = "No reliable answer.",
            citations = Array.Empty<object>()
        });
        using var fixture = new Fixture(questionResponse: coreQuestionResponse, useCoreDefaultRoute: true, queryId: originalQueryId);
        await fixture.OfferAsync();
        var offer = Assert.Single(fixture.Slack.Replies.Where(r => r.Text == "EscalationOfferPrompt"));
        using var blocks = JsonDocument.Parse(JsonSerializer.Serialize(offer.Blocks));
        var confirmButton = blocks.RootElement.EnumerateArray()
            .Where(b => b.GetProperty("type").GetString() == "actions")
            .SelectMany(b => b.GetProperty("elements").EnumerateArray())
            .Single(b => b.GetProperty("action_id").GetString() == SlackMessageFormatter.EscalationConfirmActionId);
        var offeredId = confirmButton.GetProperty("value").GetString()!;

        Assert.Empty(fixture.Backend.EscalationCalls);
        fixture.Click(SlackMessageFormatter.EscalationConfirmActionId, offeredId);
        await fixture.Slack.WaitForAsync($"Confirmed: {EscalationId}");
        var call = Assert.Single(fixture.Backend.EscalationCalls);
        using var body = JsonDocument.Parse(call.Body);
        // Collect all mismatches into one actionable failure rather than hiding
        // the later endpoint/body checks behind the first failed assertion.
        var errors = new List<string>();
        if (offeredId != originalQueryId)
            errors.Add($"Button value must preserve query_id; received {offeredId}.");
        if (call.Path != "/api/v1/chat/escalations")
            errors.Add($"Expected /api/v1/chat/escalations; received {call.Path}.");
        if (!body.RootElement.TryGetProperty("query_id", out var queryId) || queryId.GetString() != originalQueryId)
            errors.Add("POST body must contain the original query_id.");
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    private static void AssertRequestIdentityAndConsent(JsonElement body)
    {
        // The gate tests work against either wire shape. The separate Core
        // contract regression requires the current query_id endpoint/shape.
        var id = body.TryGetProperty("query_id", out var queryId)
            ? queryId.GetString()
            : body.GetProperty("question_id").GetString();
        Assert.Equal(QuestionId, id);
        // In the legacy payload consent is explicit in JSON. The current Core
        // contract uses the dedicated POST itself as confirmation.
        if (body.TryGetProperty("question_id", out _))
        {
            Assert.True(body.TryGetProperty("user_confirmation", out var consent));
            Assert.True(consent.GetProperty("confirmed").GetBoolean());
            Assert.Equal(Email, consent.GetProperty("confirmed_by").GetString());
            Assert.Equal("slack", consent.GetProperty("confirmed_via").GetString());
        }
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage SuccessResponse(string id = EscalationId) =>
        JsonResponse(JsonSerializer.Serialize(new
        {
            escalation_id = id,
            query_id = CorrelationId,
            status = "PENDING",
            created_at = "2026-11-24T12:00:00Z",
            updated_at = "2026-11-24T12:00:00Z"
        }));

    private sealed record BackendCall(HttpMethod Method, string Path, string Body, string? IdempotencyKey, string? CorrelationId);
    private sealed record SlackReply(string Text, object[]? Blocks);

    private sealed class RecordingBackend(
        Func<BackendCall, CancellationToken, Task<HttpResponseMessage>>? respond,
        string questionResponse) : HttpMessageHandler
    {
        public ConcurrentQueue<BackendCall> EscalationCalls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v1/chat/question")
                return JsonResponse(questionResponse);

            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            string? Header(string name) => request.Headers.TryGetValues(name, out var values) ? values.SingleOrDefault() : null;
            var call = new BackendCall(request.Method, path, body, Header("Idempotency-Key"), Header("X-Correlation-ID"));
            EscalationCalls.Enqueue(call);
            return respond is null ? SuccessResponse() : await respond(call, ct);
        }
    }

    private sealed class RecordingSlack : ISlackApiClient
    {
        public ConcurrentQueue<SlackReply> Replies { get; } = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _signals = new();

        private TaskCompletionSource<bool> Signal(string text) =>
            _signals.GetOrAdd(text, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

        public Task WaitForAsync(string text) => Signal(text).Task.WaitAsync(TestTimeout);
        public Task<string?> PostMessageAsync(string channel, string text, object[]? blocks = null) => Task.FromResult<string?>(MessageTs);
        public Task<bool> UpdateMessageAsync(string channel, string ts, string text, object[]? blocks = null) => Task.FromResult(true);
        public Task<string?> GetUserEmailAsync(string userId) => Task.FromResult<string?>(Email);
        public Task DeliverReplyAsync(string channel, string? ts, string text, object[]? blocks = null)
        {
            Replies.Enqueue(new(text, blocks));
            Signal(text).TrySetResult(true);
            return Task.CompletedTask;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _client;
        private readonly string _queryId;
        private readonly SlackController _questions;
        private readonly SlackInteractionsController _interactions;
        public InMemoryPendingEscalationStore Store { get; } = new();
        public RecordingSlack Slack { get; } = new();
        public RecordingBackend Backend { get; }

        public Fixture(Func<BackendCall, CancellationToken, Task<HttpResponseMessage>>? respond = null,
            string? questionResponse = null, bool useCoreDefaultRoute = false, string queryId = CorrelationId)
        {
            _queryId = queryId;
            questionResponse ??= JsonSerializer.Serialize(new UserQuestionResponse
            {
                QueryId = CorrelationId,
                QuestionId = QuestionId,
                Status = "ESCALATED_TO_EXPERT",
                ReplyText = "No reliable answer.",
                Citations = new List<Citation>()
            });
            Backend = new RecordingBackend(respond, questionResponse);
            _client = new HttpClient(Backend);
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(_client);
            var values = new Dictionary<string, string?>
            {
                ["JavaCore:ChatApiUrl"] = "http://java-service:8080/api/v1/chat/question",
                ["JavaCore:TimeoutSeconds"] = "15"
            };
            if (!useCoreDefaultRoute)
                values["JavaCore:EscalationApiUrl"] = "http://java-service:8080/api/v1/escalations";
            var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            var localizer = new Mock<IStringLocalizer<BotMessages>>();
            localizer.Setup(l => l[It.IsAny<string>()]).Returns((string key) =>
                new LocalizedString(key, key == "EscalationConfirmed" ? "Confirmed: {0}" : key));
            _questions = new SlackController(NullLogger<SlackController>.Instance, factory.Object, config, localizer.Object, Store, Slack)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
            _interactions = new SlackInteractionsController(NullLogger<SlackInteractionsController>.Instance, factory.Object, config, localizer.Object, Store, Slack)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
        }

        public async Task OfferAsync()
        {
            var payload = JsonSerializer.SerializeToElement(new
            {
                type = "event_callback",
                event_id = _queryId,
                @event = new { type = "app_mention", user = UserId, channel = ChannelId, text = "<@U01BOT> How do I access finance?" }
            });
            Assert.IsType<OkResult>(_questions.ReceiveEvent(payload));
            await Slack.WaitForAsync("EscalationOfferPrompt");
        }

        public void Click(string actionId, string questionId = QuestionId)
        {
            var payload = JsonSerializer.Serialize(new
            {
                type = "block_actions",
                user = new { id = UserId },
                channel = new { id = ChannelId },
                message = new { ts = MessageTs },
                actions = new[] { new { action_id = actionId, value = questionId } }
            });
            Assert.IsType<OkResult>(_interactions.ReceiveInteraction(payload));
        }

        public void Dispose() => _client.Dispose();
    }
}
