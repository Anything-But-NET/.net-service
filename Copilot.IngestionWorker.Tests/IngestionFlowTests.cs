using Copilot.Contracts;
using Copilot.IngestionWorker.Adapters;
using Copilot.IngestionWorker.Models;
using Copilot.IngestionWorker.Services;
using Copilot.IngestionWorker.Services.Parsing;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Copilot.IngestionWorker.Tests
{
    public class IngestionFlowTests
    {
        private readonly Mock<IMockSharePointAdapter> _adapterMock;
        private readonly Mock<ITopicProducer<DocumentSyncEvent>> _producerMock;
        private readonly Mock<IDocumentParser> _parserMock; 
        private readonly IServiceCollection _services;

        public IngestionFlowTests()
        {
            _adapterMock = new Mock<IMockSharePointAdapter>();
            _producerMock = new Mock<ITopicProducer<DocumentSyncEvent>>();
            _parserMock = new Mock<IDocumentParser>(); 

            _parserMock.Setup(p => p.CanParse(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
            _parserMock.Setup(p => p.ParseAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync((Stream stream, string docId, CancellationToken ct) =>
                           ParsedDocumentResult.Success(docId, "Test parsed content"));

            _services = new ServiceCollection();
            _services.AddSingleton(_adapterMock.Object);
            _services.AddSingleton(_producerMock.Object);

            _services.AddSingleton<IDocumentParser>(_parserMock.Object);
            _services.AddTransient<DocumentParsingOrchestrator>();

            _services.AddLogging();
        }

        [Fact]
        public async Task Ingestion_ValidDocument_PublishesFullyPopulatedEvent()
        {
            var validDoc = new MockDocument(
                Id: "01BBAD792B24A6D2954DD06DAA5C2208D1",
                Title: "Scrum_Sprint_Plan.docx",
                Content: "Test content",
                SourceUrl: "https://url",
                CreatedAt: DateTime.UtcNow
            );

            _adapterMock.Setup(a => a.GetDocumentsAsync(It.IsAny<CancellationToken>()))
                        .ReturnsAsync(new List<MockDocument> { validDoc });

            var provider = _services.BuildServiceProvider(true);
            var worker = new DocumentIngestionWorker(provider, provider.GetRequiredService<ILogger<DocumentIngestionWorker>>());

            await worker.ProcessDocumentsOnceAsync(CancellationToken.None);

            _producerMock.Verify(p => p.Produce(
                It.Is<DocumentSyncEvent>(e => e.DocumentId == validDoc.Id && e.RawContent == "Test parsed content"), 
                It.IsAny<CancellationToken>()
            ), Times.Once);
        }

        [Fact]
        public async Task Ingestion_DuplicateDocument_DoesNotPublishTwice()
        {
            var duplicateDoc = new MockDocument("DUP_ID", "Dup.pdf", "data", "url", DateTime.UtcNow);

            _adapterMock.Setup(a => a.GetDocumentsAsync(It.IsAny<CancellationToken>()))
                        .ReturnsAsync(new List<MockDocument> { duplicateDoc, duplicateDoc });

            var provider = _services.BuildServiceProvider(true);
            var worker = new DocumentIngestionWorker(provider, provider.GetRequiredService<ILogger<DocumentIngestionWorker>>());

            await worker.ProcessDocumentsOnceAsync(CancellationToken.None);

            _producerMock.Verify(p => p.Produce(
                It.Is<DocumentSyncEvent>(e => e.DocumentId == duplicateDoc.Id),
                It.IsAny<CancellationToken>()
            ), Times.Once);
        }

        [Fact]
        public async Task Ingestion_DocumentFailure_ContinuesProcessingRemainingDocuments()
        {
            var validDoc1 = new MockDocument("DOC_1", "Valid1.pdf", "Content", "url", DateTime.UtcNow);
            var brokenDoc = new MockDocument(null, null, null, null, DateTime.MinValue);
            var validDoc2 = new MockDocument("DOC_2", "Valid2.pdf", "Content", "url", DateTime.UtcNow);

            _adapterMock.Setup(a => a.GetDocumentsAsync(It.IsAny<CancellationToken>()))
                        .ReturnsAsync(new List<MockDocument> { validDoc1, brokenDoc, validDoc2 });

            var provider = _services.BuildServiceProvider(true);
            var worker = new DocumentIngestionWorker(provider, provider.GetRequiredService<ILogger<DocumentIngestionWorker>>());

            await worker.ProcessDocumentsOnceAsync(CancellationToken.None);

            _producerMock.Verify(p => p.Produce(It.Is<DocumentSyncEvent>(e => e.DocumentId == validDoc1.Id), It.IsAny<CancellationToken>()), Times.Once);
            _producerMock.Verify(p => p.Produce(It.Is<DocumentSyncEvent>(e => e.DocumentId == validDoc2.Id), It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
