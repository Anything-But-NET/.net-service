using Copilot.Contracts;
using System.Text;
using System.Text.RegularExpressions;

namespace Copilot.TeamsBotApi.Helpers
{
    public static class SlackMessageFormatter
    {
        public const int MaxSlackMessageLength = 3500;
        private const string TruncationSuffix = "\n\n_... (response truncated due to length limits)_";

        private static readonly Regex MarkdownLinkRegex = new(
            @"\[(?<text>[^\]]+)\]\((?<url>[^)]*)\)",
            RegexOptions.Compiled);

        public static string FormatLinksForSlack(string? text, string missingSourceText)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            return MarkdownLinkRegex.Replace(text, match =>
            {
                var linkText = SanitizeSlackText(match.Groups["text"].Value);
                var url = match.Groups["url"].Value.Trim();

                if (string.IsNullOrWhiteSpace(linkText))
                    linkText = missingSourceText;

                if (!string.IsNullOrWhiteSpace(url) && Uri.IsWellFormedUriString(url, UriKind.Absolute))
                {
                    return $"<{url}|{linkText}>";
                }

                return $"{linkText} _({missingSourceText})_";
            });
        }

        public static string BuildFullSlackResponse(
            UserQuestionResponse? response,
            string availableSourcesLabel,
            string missingSourceLabel,
            string noAnswerFallbackText)
        {
            if (response == null || string.IsNullOrWhiteSpace(response.ReplyText))
            {
                return noAnswerFallbackText;
            }

            var formattedReply = FormatLinksForSlack(response.ReplyText.Trim(), missingSourceLabel);
            var builder = new StringBuilder(formattedReply);

            if (response.Citations != null && response.Citations.Count > 0)
            {
                var citationsBuilder = new StringBuilder();
                citationsBuilder.AppendLine($"\n\n*{availableSourcesLabel}*");

                for (int i = 0; i < response.Citations.Count; i++)
                {
                    var citation = response.Citations[i];
                    if (citation == null) continue;

                    var rawTitle = !string.IsNullOrWhiteSpace(citation.Title)
                        ? citation.Title.Trim()
                        : missingSourceLabel;

                    var safeTitle = SanitizeSlackText(rawTitle);

                    if (!string.IsNullOrWhiteSpace(citation.Url) &&
                        Uri.IsWellFormedUriString(citation.Url.Trim(), UriKind.Absolute))
                    {
                        citationsBuilder.AppendLine($"[{i + 1}] <{citation.Url.Trim()}|{safeTitle}>");
                    }
                    else
                    {
                        citationsBuilder.AppendLine($"[{i + 1}] {safeTitle} ({missingSourceLabel})");
                    }
                }

                var citationsBlock = citationsBuilder.ToString().TrimEnd();

                if (builder.Length + citationsBlock.Length > MaxSlackMessageLength)
                {
                    int allowedBodyLength = MaxSlackMessageLength - citationsBlock.Length - TruncationSuffix.Length;
                    if (allowedBodyLength > 100)
                    {
                        var truncatedBody = TruncateSafely(builder.ToString(), allowedBodyLength);
                        return truncatedBody + TruncationSuffix + citationsBlock;
                    }
                }

                builder.Append(citationsBlock);
            }

            var finalMessage = builder.ToString().TrimEnd();
            if (finalMessage.Length > MaxSlackMessageLength)
            {
                return TruncateSafely(finalMessage, MaxSlackMessageLength - TruncationSuffix.Length) + TruncationSuffix;
            }

            return finalMessage;
        }

        private static string SanitizeSlackText(string text)
        {
            return text
                .Replace("|", "¦")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
        }

        private static string TruncateSafely(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
                return text;

            var slice = text[..maxLength];

            int lastOpenBracket = slice.LastIndexOf('<');
            int lastCloseBracket = slice.LastIndexOf('>');
            if (lastOpenBracket > lastCloseBracket)
            {
                slice = slice[..lastOpenBracket];
            }

            return slice.TrimEnd();
        }
    }
}
