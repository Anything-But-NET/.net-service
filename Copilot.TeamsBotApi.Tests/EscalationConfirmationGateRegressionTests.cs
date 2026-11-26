using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
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

// SCRUM-98: real Slack controllers, simulated Java Core and Slack transport.
// The fake models Core's query_id idempotency contract; real DB uniqueness
// must be verified separately by the integration suite.
public class EscalationConfirmationGateRegressionTests
{
    private const string QueryId = "req-scrum-98-regression";
    private const string EscalationId = "987e6543-e21b-12d3-a456-426614174000";
    private const string UserId = "U123";
    private const string ChannelId = "C123";
    private const string MessageTs = "1700000000.000100";
    private const string SigningSecret = "scrum-98-test-signing-secret";
    private const string Endpoint = "/api/v1/chat/escalations";
    private const string Confirmed = "Confirmed: " + EscalationId;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task NoUserResponse_OffersConfirmationWithoutSubmitting()
    {
        using var f = new Fixture();
        await f.OfferAsync();
        Assert.NotEmpty(f.ButtonValue(SlackMessageFormatter.EscalationConfirmActionId));
        Assert.NotEmpty(f.ButtonValue(SlackMessageFormatter.EscalationDeclineActionId));
        Assert.Empty(f.Backend.Calls);
        Assert.Empty(f.Backend.Records);
    }

    [Fact]
    public async Task Decline_ClearsButtonsWithoutSubmitting()
    {
        using var f = new Fixture();
        await f.OfferAsync();
        f.Click(SlackMessageFormatter.EscalationDeclineActionId);
        await f.Slack.WaitForAsync("EscalationDeclined");
        Assert.Empty(Assert.Single(f.Slack.Replies.Where(r => r.Text == "EscalationDeclined")).Blocks!);
        Assert.Empty(f.Backend.Calls);
        Assert.Empty(f.Backend.Records);
    }

    [Fact]
    public async Task ExplicitConfirmation_SubmitsOriginalQueryIdExactlyOnce()
    {
        using var f = new Fixture();
        await f.OfferAsync();
        Assert.Empty(f.Backend.Calls);
        f.Click(SlackMessageFormatter.EscalationConfirmActionId);
        await f.Slack.WaitForAsync(Confirmed);
        AssertCall(Assert.Single(f.Backend.Calls));
        Assert.Equal(EscalationId, Assert.Single(f.Backend.Records).Value);
    }

    [Fact]
    public async Task RepeatedConfirmation_PreservesIdentityAndReturnsSameReference()
    {
        using var f = new Fixture();
        await f.OfferAsync();
        f.Click(SlackMessageFormatter.EscalationConfirmActionId);
        await f.Slack.WaitForAsync(Confirmed);
        f.Click(SlackMessageFormatter.EscalationConfirmActionId);
        await f.Slack.WaitForAsync(Confirmed, 2);
        Assert.Equal(2, f.Backend.Calls.Count);
        Assert.All(f.Backend.Calls, AssertCall);
        Assert.Equal(EscalationId, Assert.Single(f.Backend.Records).Value);
    }

    [Fact]
    public async Task OverlappingConfirmations_SendSameQueryIdToIdempotentCore()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int count = 0;
        using var f = new Fixture(async (backend, call, ct) =>
        {
            if (Interlocked.Increment(ref count) == 1)
            {
                entered.TrySetResult(true);
                await release.Task.WaitAsync(ct);
            }
            return backend.CreateOrGet(call);
        });
        await f.OfferAsync();
        f.Click(SlackMessageFormatter.EscalationConfirmActionId);
        try
        {
            await entered.Task.WaitAsync(TestTimeout);
            f.Click(SlackMessageFormatter.EscalationConfirmActionId);
            await f.Slack.WaitForAsync(Confirmed);
            Assert.Equal(2, f.Backend.Calls.Count);
        }
        finally { release.TrySetResult(true); }
        await f.Slack.WaitForAsync(Confirmed, 2);
        Assert.All(f.Backend.Calls, AssertCall);
        Assert.Equal(EscalationId, Assert.Single(f.Backend.Records).Value);
    }

    [Fact]
    public async Task LostResponseAfterSimulatedCommit_RetryPreservesQueryId()
    {
        int attempts = 0;
        using var f = new Fixture((backend, call, _) =>
        {
            var response = backend.CreateOrGet(call);
            if (Interlocked.Increment(ref attempts) == 1)
            {
                response.Dispose();
                throw new HttpRequestException("Simulated lost response after commit");
            }
            return Task.FromResult(response);
        });
        await f.OfferAsync();
        var original = f.ButtonValue(SlackMessageFormatter.EscalationConfirmActionId);
        f.Click(SlackMessageFormatter.EscalationConfirmActionId);
        await f.Slack.WaitForAsync("EscalationSubmitFailed");
        var retry = f.ButtonValue(SlackMessageFormatter.EscalationConfirmActionId, "EscalationSubmitFailed");
        Assert.Equal(original, retry);
        Assert.Single(f.Backend.Records);
        f.Click(SlackMessageFormatter.EscalationConfirmActionId, retry);
        await f.Slack.WaitForAsync(Confirmed);
        Assert.Equal(2, f.Backend.Calls.Count);
        Assert.All(f.Backend.Calls, AssertCall);
        Assert.Equal(EscalationId, Assert.Single(f.Backend.Records).Value);
    }

    [Fact]
    public async Task RetryOfferThenDecline_DoesNotSubmitAgain()
    {
        using var f = new Fixture((_, _, _) => Task.FromResult(Json("{}", HttpStatusCode.ServiceUnavailable)));
        await f.OfferAsync();
        f.Click(SlackMessageFormatter.EscalationConfirmActionId);
        await f.Slack.WaitForAsync("EscalationSubmitFailed");
        var retryDecline = f.ButtonValue(SlackMessageFormatter.EscalationDeclineActionId, "EscalationSubmitFailed");
        f.Click(SlackMessageFormatter.EscalationDeclineActionId, retryDecline);
        await f.Slack.WaitForAsync("EscalationDeclined");
        Assert.Single(f.Backend.Calls);
        Assert.Empty(f.Backend.Records);
    }

    [Fact]
    public async Task ExpiredDraft_ReportsGoneWithoutCreatingRecord()
    {
        using var f = new Fixture((_, _, _) => Task.FromResult(Json("{}", HttpStatusCode.Gone)));
        await f.OfferAsync();
        f.Click(SlackMessageFormatter.EscalationConfirmActionId);
        await f.Slack.WaitForAsync("EscalationExpired");
        AssertCall(Assert.Single(f.Backend.Calls));
        Assert.Empty(f.Backend.Records);
        Assert.Empty(Assert.Single(f.Slack.Replies.Where(r => r.Text == "EscalationExpired")).Blocks!);
    }

    [Fact]
    public async Task UnrelatedAction_IsNotConsent()
    {
        using var f = new Fixture();
        await f.OfferAsync();
        f.Click("unrelated_action", f.ButtonValue(SlackMessageFormatter.EscalationConfirmActionId));
        Assert.Empty(f.Backend.Calls);
        Assert.Empty(f.Backend.Records);
    }

    [Fact]
    public async Task TamperedToken_IsRejectedBeforeCallingCore()
    {
        using var f = new Fixture();
        await f.OfferAsync();
        var token = f.ButtonValue(SlackMessageFormatter.EscalationConfirmActionId);
        var result = f.Interact(SlackMessageFormatter.EscalationConfirmActionId, token + "tampered");
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(f.Backend.Calls);
        Assert.Empty(f.Backend.Records);
    }

    private static void AssertCall(BackendCall call)
    {
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal(Endpoint, call.Path);
        Assert.Equal(QueryId, call.CorrelationId);
        using var document = JsonDocument.Parse(call.Body);
        Assert.Equal(QueryId, document.RootElement.GetProperty("query_id").GetString());
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record BackendCall(HttpMethod Method, string Path, string Body, string? CorrelationId);
    private sealed record Reply(string Text, object[]? Blocks);

    private sealed class BackendHandler(Func<BackendHandler, BackendCall, CancellationToken, Task<HttpResponseMessage>>? respond) : HttpMessageHandler
    {
        public ConcurrentQueue<BackendCall> Calls { get; } = new();
        public ConcurrentDictionary<string, string> Records { get; } = new();

        public HttpResponseMessage CreateOrGet(BackendCall call)
        {
            using var body = JsonDocument.Parse(call.Body);
            var key = body.RootElement.GetProperty("query_id").GetString()!;
            var id = Records.GetOrAdd(key, _ => EscalationId);
            return Json(JsonSerializer.Serialize(new
            {
                escalation_id = id,
                query_id = key,
                status = "PENDING",
                created_at = "2026-11-24T12:00:00Z",
                updated_at = "2026-11-24T12:00:00Z"
            }));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v1/chat/question")
                return Json(JsonSerializer.Serialize(new
                {
                    query_id = QueryId,
                    status = "ESCALATED_TO_EXPERT",
                    reply_text = "No reliable answer.",
                    citations = Array.Empty<object>()
                }));

            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var correlation = request.Headers.TryGetValues("X-Correlation-ID", out var values) ? values.SingleOrDefault() : null;
            var call = new BackendCall(request.Method, path, body, correlation);
            Calls.Enqueue(call);
            if (path != Endpoint || request.Method != HttpMethod.Post)
                return Json("{}", HttpStatusCode.NotFound);
            return respond is null ? CreateOrGet(call) : await respond(this, call, ct);
        }
    }

    private sealed class RecordingSlack : ISlackApiClient
    {
        public ConcurrentQueue<Reply> Replies { get; } = new();
        private readonly SemaphoreSlim _changed = new(0);

        public Task<string?> PostMessageAsync(string channel, string text, object[]? blocks = null) => Task.FromResult<string?>(MessageTs);
        public Task<bool> UpdateMessageAsync(string channel, string ts, string text, object[]? blocks = null) => Task.FromResult(true);
        public Task<string?> GetUserEmailAsync(string userId) => Task.FromResult<string?>("employee@example.com");
        public Task DeliverReplyAsync(string channel, string? ts, string text, object[]? blocks = null)
        {
            Replies.Enqueue(new Reply(text, blocks));
            _changed.Release();
            return Task.CompletedTask;
        }

        public async Task WaitForAsync(string text, int count = 1)
        {
            using var timeout = new CancellationTokenSource(TestTimeout);
            while (Replies.Count(r => r.Text == text) < count)
                await _changed.WaitAsync(timeout.Token);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _client;
        private readonly SlackController _questions;
        private readonly SlackInteractionsController _interactions;
        public BackendHandler Backend { get; }
        public RecordingSlack Slack { get; } = new();

        public Fixture(Func<BackendHandler, BackendCall, CancellationToken, Task<HttpResponseMessage>>? respond = null)
        {
            Backend = new BackendHandler(respond);
            _client = new HttpClient(Backend);
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(_client);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JavaCore:ChatApiUrl"] = "http://java-service:8080/api/v1/chat/question",
                ["JavaCore:TimeoutSeconds"] = "30",
                ["Slack:SigningSecret"] = SigningSecret
            }).Build();
            var localizer = new Mock<IStringLocalizer<BotMessages>>();
            localizer.Setup(l => l[It.IsAny<string>()]).Returns((string key) =>
                new LocalizedString(key, key == "EscalationConfirmed" ? "Confirmed: {0}" : key));
            _questions = new SlackController(NullLogger<SlackController>.Instance, factory.Object, config, localizer.Object, Slack)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
            _interactions = new SlackInteractionsController(NullLogger<SlackInteractionsController>.Instance, factory.Object, config, localizer.Object, Slack)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
        }

        public async Task OfferAsync()
        {
            var payload = JsonSerializer.SerializeToElement(new
            {
                type = "event_callback",
                event_id = QueryId,
                @event = new { type = "app_mention", user = UserId, channel = ChannelId, text = "<@U01BOT> How do I access finance?" }
            });
            Assert.IsType<OkResult>(_questions.ReceiveEvent(payload));
            await Slack.WaitForAsync("EscalationOfferPrompt");
        }

        public string ButtonValue(string actionId, string replyText = "EscalationOfferPrompt")
        {
            var reply = Assert.Single(Slack.Replies.Where(r => r.Text == replyText));
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(reply.Blocks));
            return doc.RootElement.EnumerateArray()
                .Where(b => b.GetProperty("type").GetString() == "actions")
                .SelectMany(b => b.GetProperty("elements").EnumerateArray())
                .Single(b => b.GetProperty("action_id").GetString() == actionId)
                .GetProperty("value").GetString()!;
        }

        public IActionResult Interact(string actionId, string token) => _interactions.ReceiveInteraction(JsonSerializer.Serialize(new
        {
            type = "block_actions",
            user = new { id = UserId },
            channel = new { id = ChannelId },
            message = new { ts = MessageTs },
            actions = new[] { new { action_id = actionId, value = token } }
        }));

        public void Click(string actionId, string? token = null) =>
            Assert.IsType<OkResult>(Interact(actionId, token ?? ButtonValue(actionId)));

        public void Dispose() => _client.Dispose();
    }
}
