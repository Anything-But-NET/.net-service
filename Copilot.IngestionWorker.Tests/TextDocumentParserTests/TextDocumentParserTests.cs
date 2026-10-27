using Copilot.IngestionWorker.Services.Parsing.Parsers;
using System;
using System.Collections.Generic;
using System.Text;

namespace Copilot.IngestionWorker.Tests.TextDocumentParserTests
{
    public class TextDocumentParserTests
    {
        private readonly TextDocumentParser _parser = new();

        [Fact]
        public void CanParse_WithTextPlainContentType_ReturnsTrue()
        {
            var result = _parser.CanParse("text/plain", "unknown_file.bin");
            Assert.True(result);
        }

        [Fact]
        public void CanParse_WithTxtExtension_ReturnsTrue()
        {
            var result = _parser.CanParse("application/octet-stream", "document.txt");
            Assert.True(result);
        }

        [Fact]
        public void CanParse_WithPdfFormat_ReturnsFalse()
        {
            var result = _parser.CanParse("application/pdf", "document.pdf");
            Assert.False(result);
        }

        [Fact]
        public async Task ParseAsync_WithValidStream_ReturnsParsedTextAndId()
        {
            var documentId = "doc-123";
            var expectedText = "Hello, this is a test document.";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(expectedText));

            var result = await _parser.ParseAsync(stream, documentId, CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.Equal(expectedText, result.ParsedText);
            Assert.Equal(documentId, result.DocumentId);
        }
    }
}
