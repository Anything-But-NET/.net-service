using Copilot.IngestionWorker.Services.Parsing.Parsers;
using System;
using System.Collections.Generic;
using System.Text;

namespace Copilot.IngestionWorker.Tests.DocumentParserTests
{
    public class DocxMarkdownParserTests
    {
        private readonly DocxMarkdownParser _parser = new();

        [Fact]
        public void CanParse_ValidDocxContentType_ReturnsTrue()
        {
            Assert.True(_parser.CanParse("application/vnd.openxmlformats-officedocument.wordprocessingml.document", null));
            Assert.True(_parser.CanParse(null, "report.docx"));
        }

        [Fact]
        public void CanParse_InvalidFormat_ReturnsFalse()
        {
            Assert.False(_parser.CanParse("text/plain", "report.txt"));
        }

        [Fact]
        public async Task ParseAsync_ValidDocxFile_ReturnsParsedMarkdown()
        {
            using var stream = File.OpenRead("TestData/dummy.docx");

            var result = await _parser.ParseAsync(stream, "docx-doc-1", CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.NotNull(result.ParsedText);
            Assert.NotEmpty(result.ParsedText);
            Assert.Contains("# Test Document for Ingestion", result.ParsedText);
        }

        [Fact]
        public async Task ParseAsync_EmptyStream_ReturnsFailure()
        {
            using var stream = new MemoryStream();
            var result = await _parser.ParseAsync(stream, "docx-doc-1", CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Contains("empty", result.ErrorMessage);
        }

        [Fact]
        public async Task ParseAsync_NullStream_ReturnsFailure()
        {
            var result = await _parser.ParseAsync(null, "docx-doc-1", CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Contains("empty", result.ErrorMessage);
        }
    }
}
