using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Copilot.TeamsBotApi.Services;

public sealed record EscalationActionContext(
    [property: JsonPropertyName("q")] string QueryId,
    [property: JsonPropertyName("u")] string SlackUserId,
    [property: JsonPropertyName("c")] string Channel);

public static class EscalationActionToken
{
    public static string Encode(EscalationActionContext context, string? signingSecret)
    {
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(context);
        var payloadPart = Base64UrlEncode(jsonBytes);

        if (string.IsNullOrEmpty(signingSecret))
        {
            return payloadPart;
        }

        var signature = ComputeSignature(payloadPart, signingSecret);
        return $"{payloadPart}.{signature}";
    }

    public static bool TryDecode(string? token, string? signingSecret, out EscalationActionContext? context)
    {
        context = null;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        string payloadPart;
        if (!string.IsNullOrEmpty(signingSecret))
        {
            var dotIndex = token.IndexOf('.');
            if (dotIndex <= 0 || dotIndex == token.Length - 1)
            {
                return false;
            }

            payloadPart = token[..dotIndex];
            var providedSig = token[(dotIndex + 1)..];
            var expectedSig = ComputeSignature(payloadPart, signingSecret);

            var providedBytes = Encoding.UTF8.GetBytes(providedSig);
            var expectedBytes = Encoding.UTF8.GetBytes(expectedSig);
            if (!CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes))
            {
                return false;
            }
        }
        else
        {
            var dotIndex = token.IndexOf('.');
            payloadPart = dotIndex > 0 ? token[..dotIndex] : token;
        }

        try
        {
            var jsonBytes = Base64UrlDecode(payloadPart);
            var parsed = JsonSerializer.Deserialize<EscalationActionContext>(jsonBytes);
            if (parsed is null ||
                string.IsNullOrWhiteSpace(parsed.QueryId) ||
                string.IsNullOrWhiteSpace(parsed.SlackUserId) ||
                string.IsNullOrWhiteSpace(parsed.Channel))
            {
                return false;
            }

            context = parsed;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }

    public static string? ResolveSigningSecret(IConfiguration configuration)
    {
        var secret = configuration["Slack:SigningSecret"];
        if (!string.IsNullOrWhiteSpace(secret))
        {
            return secret;
        }

        var botToken = configuration["Slack:BotToken"];
        return !string.IsNullOrWhiteSpace(botToken) ? botToken : null;
    }

    private static string ComputeSignature(string payloadPart, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var dataBytes = Encoding.UTF8.GetBytes(payloadPart);
        var hash = HMACSHA256.HashData(keyBytes, dataBytes);
        return Base64UrlEncode(hash);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] Base64UrlDecode(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
            case 1: throw new FormatException("Invalid base64url length.");
        }

        return Convert.FromBase64String(padded);
    }
}
