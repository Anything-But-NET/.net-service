using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Copilot.TeamsBotApi.Services;

public interface ISlackApiClient
{
    Task<string?> PostMessageAsync(string channel, string text, object[]? blocks = null);
    Task<bool> UpdateMessageAsync(string channel, string ts, string text, object[]? blocks = null);
    Task DeliverReplyAsync(string channel, string? ts, string text, object[]? blocks = null);
    Task<string?> GetUserEmailAsync(string userId);
}

public sealed class SlackApiClient : ISlackApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger _logger;

    public SlackApiClient(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<SlackApiClient> logger)
        : this(httpClientFactory, configuration, (ILogger)logger)
    {
    }

    internal SlackApiClient(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task DeliverReplyAsync(string channel, string? ts, string text, object[]? blocks = null)
    {
        if (!string.IsNullOrWhiteSpace(ts) && await UpdateMessageAsync(channel, ts, text, blocks))
        {
            return;
        }

        await PostMessageAsync(channel, text, blocks);
    }

    public async Task<string?> PostMessageAsync(string channel, string text, object[]? blocks = null)
    {
        object payload = blocks is { Length: > 0 }
            ? new { channel, text, blocks }
            : new { channel, text };

        var result = await CallSlackApiAsync("chat.postMessage", payload);
        if (result is { } content && content.TryGetProperty("ts", out var ts))
        {
            return ts.GetString();
        }

        return null;
    }

    public async Task<bool> UpdateMessageAsync(string channel, string ts, string text, object[]? blocks = null)
    {
        object payload = blocks is not null
            ? new { channel, ts, text, blocks }
            : new { channel, ts, text };

        return await CallSlackApiAsync("chat.update", payload) is not null;
    }

    public async Task<string?> GetUserEmailAsync(string userId)
    {
        var token = _configuration["Slack:BotToken"];
        var client = _httpClientFactory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://slack.com/api/users.info?user={userId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var content = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (content.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True &&
            content.TryGetProperty("user", out var user) &&
            user.TryGetProperty("profile", out var profile) &&
            profile.TryGetProperty("email", out var email))
        {
            return email.GetString();
        }

        return null;
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
