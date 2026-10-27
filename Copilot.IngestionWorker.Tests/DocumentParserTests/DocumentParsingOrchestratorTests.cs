using Copilot.IngestionWorker.Models;
using Copilot.IngestionWorker.Services.Parsing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System;
using System.Collections.Generic;
using System.Text;

namespace Copilot.IngestionWorker.Tests.TextDocumentParserTests
{
    public class DocumentParsingOrchestratorTests
    {
        private readonly Mock<IDocumentParser> _mockParser;
        private readonly DocumentParsingOrchestrator _orchestrator;

        public DocumentParsingOrchestratorTests()
        {
            _mockParser = new Mock<IDocumentParser>();

            _orchestrator = new DocumentParsingOrchestrator(
                new[] { _mockParser.Object },
                NullLogger<DocumentParsingOrchestrator>.Instance);
        }

        [Fact]
        public async Task ProcessDocumentAsync_WithEmptyStream_ReturnsFailure()
        {
            var documentId = "doc-empty";
            using var emptyStream = new MemoryStream(); 

            var result = await _orchestrator.ProcessDocumentAsync(
                emptyStream, documentId, "text/plain", "test.txt", CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Equal(documentId, result.DocumentId);
            Assert.Contains("empty", result.ErrorMessage);
        }

        [Fact]
        public async Task ProcessDocumentAsync_WithUnsupportedFormat_ReturnsFailure()
        {
            var documentId = "doc-unsupported";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data"));

            _mockParser.Setup(p => p.CanParse(It.IsAny<string>(), It.IsAny<string>())).Returns(false);

            var result = await _orchestrator.ProcessDocumentAsync(
                stream, documentId, "image/png", "image.png", CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Contains("Unsupported format", result.ErrorMessage);
        }

        [Fact]
        public async Task ProcessDocumentAsync_WhenParserThrowsException_ReturnsFailureDoesNotCrash()
        {
            var documentId = "doc-error";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data"));

            _mockParser.Setup(p => p.CanParse(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
            _mockParser.Setup(p => p.ParseAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                       .ThrowsAsync(new InvalidOperationException("Something went terribly wrong"));

            var result = await _orchestrator.ProcessDocumentAsync(
                stream, documentId, "text/plain", "test.txt", CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Equal("Internal parsing error.", result.ErrorMessage);
        }

        [Fact]
        public async Task ProcessDocumentAsync_WithValidDocument_ReturnsSuccess()
        {
            var documentId = "doc-valid";
            var expectedText = "Parsed successfully.";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("raw data"));

            var expectedResult = ParsedDocumentResult.Success(documentId, expectedText);

            _mockParser.Setup(p => p.CanParse(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
            _mockParser.Setup(p => p.ParseAsync(stream, documentId, CancellationToken.None))
                       .ReturnsAsync(expectedResult);

            var result = await _orchestrator.ProcessDocumentAsync(
                stream, documentId, "text/plain", "test.txt", CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.Equal(expectedText, result.ParsedText);
        }
    }
}
