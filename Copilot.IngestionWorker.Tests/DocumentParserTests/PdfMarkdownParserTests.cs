using Copilot.IngestionWorker.Services.Parsing.Parsers;
using System;
using System.Collections.Generic;
using System.Text;

namespace Copilot.IngestionWorker.Tests.DocumentParserTests
{
    public class PdfMarkdownParserTests
    {
        private readonly PdfMarkdownParser _parser = new();

        [Fact]
        public void CanParse_ValidPdfContentType_ReturnsTrue()
        {
            Assert.True(_parser.CanParse("application/pdf", null));
            Assert.True(_parser.CanParse(null, "document.pdf"));
        }

        [Fact]
        public void CanParse_InvalidFormat_ReturnsFalse()
        {
            Assert.False(_parser.CanParse("image/png", "image.png"));
            Assert.False(_parser.CanParse("text/plain", "report.txt"));
        }

        [Fact]
        public async Task ParseAsync_ValidPdfFile_ReturnsParsedMarkdown()
        {
            using var stream = File.OpenRead("TestData/dummy.pdf");

            var result = await _parser.ParseAsync(stream, "pdf-doc-1", CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.NotNull(result.ParsedText);
            Assert.NotEmpty(result.ParsedText);

         
            Assert.Contains("#", result.ParsedText); 
            Assert.Contains("---", result.ParsedText);
        }

        [Fact]
        public async Task ParseAsync_EmptyStream_ReturnsFailure()
        {
            using var stream = new MemoryStream();
            var result = await _parser.ParseAsync(stream, "pdf-doc-1", CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Contains("empty", result.ErrorMessage);
        }

        [Fact]
        public async Task ParseAsync_NullStream_ReturnsFailure()
        {
            var result = await _parser.ParseAsync(null, "pdf-doc-1", CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Contains("empty", result.ErrorMessage);
        }
    }
}
