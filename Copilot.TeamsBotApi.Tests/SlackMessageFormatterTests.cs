using Copilot.Contracts;
using Copilot.TeamsBotApi.Helpers;
using System;
using System.Collections.Generic;
using System.Text;

namespace Copilot.IngestionWorker.Tests
{
    public class SlackMessageFormatterTests
    {
        private const string MissingSourceText = "Source unavailable";
        private const string SourcesLabel = "Available sources:";
        private const string NoAnswerText = "No answer found.";

        [Fact]
        public void FormatLinksForSlack_ValidLink_FormatsCorrectly()
        {
            var input = "Check the [Policy](https://example.com/policy).";
            var expected = "Check the <https://example.com/policy|Policy>.";

            var result = SlackMessageFormatter.FormatLinksForSlack(input, MissingSourceText);

            Assert.Equal(expected, result);
        }

        [Fact]
        public void FormatLinksForSlack_BrokenOrMissingLink_HandlesClearly()
        {
            var input = "Here is [Doc 1]() and [Doc 2](not-a-valid-url).";
            var expected = "Here is Doc 1 _(Source unavailable)_ and Doc 2 _(Source unavailable)_.";

            var result = SlackMessageFormatter.FormatLinksForSlack(input, MissingSourceText);

            Assert.Equal(expected, result);
        }

        [Fact]
        public void FormatLinksForSlack_MultipleCitationsInOneResponse_FormatsAll()
        {
            var input = "Data from [Site A](http://site-a.com) and [Site B](https://site-b.com/doc).";
            var expected = "Data from <http://site-a.com|Site A> and <https://site-b.com/doc|Site B>.";

            var result = SlackMessageFormatter.FormatLinksForSlack(input, MissingSourceText);

            Assert.Equal(expected, result);
        }

        [Fact]
        public void BuildFullSlackResponse_ValidResponseWithCitations_FormatsCorrectly()
        {
            var response = new UserQuestionResponse
            {
                ReplyText = "See the [Guide](https://docs.company.com/guide) for details.",
                Citations = new List<Citation>
                {
                    new() { Title = "Security Policy", Url = "https://docs.company.com/sec" },
                    new() { Title = "Internal Memo", Url = "invalid-url" }
                }
            };

            var result = SlackMessageFormatter.BuildFullSlackResponse(
                response, SourcesLabel, MissingSourceText, NoAnswerText);

            Assert.Contains("See the <https://docs.company.com/guide|Guide> for details.", result);
            Assert.Contains($"*{SourcesLabel}*", result);
            Assert.Contains("[1] <https://docs.company.com/sec|Security Policy>", result);
            Assert.Contains($"[2] Internal Memo ({MissingSourceText})", result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void BuildFullSlackResponse_EmptyOrNullReply_ReturnsSafeNoAnswer(string? replyText)
        {
            var response = new UserQuestionResponse { ReplyText = replyText! };

            var result = SlackMessageFormatter.BuildFullSlackResponse(
                response, SourcesLabel, MissingSourceText, NoAnswerText);

            Assert.Equal(NoAnswerText, result);
        }

        [Fact]
        public void BuildFullSlackResponse_MalformedCharactersInCitationTitle_EscapesCorrectly()
        {
            var response = new UserQuestionResponse
            {
                ReplyText = "Here is the result.",
                Citations = new List<Citation>
                {
                    new() { Title = "File | Section <A> & <B>", Url = "https://example.com/doc" }
                }
            };

            var result = SlackMessageFormatter.BuildFullSlackResponse(
                response, SourcesLabel, MissingSourceText, NoAnswerText);

            // Перевіряємо екранування символів | та < >
            Assert.Contains("[1] <https://example.com/doc|File ¦ Section &lt;A&gt; & &lt;B&gt;>", result);
        }

        [Fact]
        public void BuildFullSlackResponse_ExtremelyLongResponse_TruncatesWithoutBreakingCitations()
        {
            var longText = new string('A', 5000);
            var response = new UserQuestionResponse
            {
                ReplyText = longText,
                Citations = new List<Citation>
                {
                    new() { Title = "Source 1", Url = "https://example.com/1" }
                }
            };

            var result = SlackMessageFormatter.BuildFullSlackResponse(
                response, SourcesLabel, MissingSourceText, NoAnswerText);

            Assert.True(result.Length <= SlackMessageFormatter.MaxSlackMessageLength);
            Assert.Contains("response truncated", result);
            Assert.Contains("[1] <https://example.com/1|Source 1>", result);
        }
    }
}
