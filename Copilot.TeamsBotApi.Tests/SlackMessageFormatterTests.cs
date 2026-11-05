using Copilot.TeamsBotApi.Helpers;
using System;
using System.Collections.Generic;
using System.Text;

namespace Copilot.IngestionWorker.Tests
{
    public class SlackMessageFormatterTests
    {
        private const string MissingSourceText = "Source unavailable";

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
    }
}
