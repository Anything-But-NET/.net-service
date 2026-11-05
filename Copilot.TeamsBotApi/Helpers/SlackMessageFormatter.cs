using System.Text.RegularExpressions;

namespace Copilot.TeamsBotApi.Helpers
{
    public static class SlackMessageFormatter
    {
        private static readonly Regex MarkdownLinkRegex = new(@"\[([^\]]+)\]\(([^)]*)\)", RegexOptions.Compiled);

        public static string FormatLinksForSlack(string input, string missingSourceText)
        {
            if (string.IsNullOrEmpty(input)) return input;

            return MarkdownLinkRegex.Replace(input, match =>
            {
                var text = match.Groups[1].Value;
                var url = match.Groups[2].Value.Trim();

                if (string.IsNullOrWhiteSpace(url) || !Uri.IsWellFormedUriString(url, UriKind.Absolute))
                {
                    return $"{text} _({missingSourceText})_";
                }

                return $"<{url}|{text}>";
            });
        }
    }
}
