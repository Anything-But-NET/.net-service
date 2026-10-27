using Copilot.IngestionWorker.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace Copilot.IngestionWorker.Services.Parsing
{
    public class DocumentParsingOrchestrator
    {
        private readonly IEnumerable<IDocumentParser> parsers;
        private readonly ILogger<DocumentParsingOrchestrator> logger;

        public DocumentParsingOrchestrator(IEnumerable<IDocumentParser> parsers, ILogger<DocumentParsingOrchestrator> logger)
        {
            this.parsers = parsers;
            this.logger = logger;
        }

        public async Task<ParsedDocumentResult> ProcessDocumentAsync(Stream fileStream, string documentId, string contentType, string fileName, CancellationToken cancellationToken)
        {
            if (fileStream == null || fileStream.Length == 0)
            {
                logger.LogWarning("Document {DocumentId} is empty. Skipping.", documentId);
                return ParsedDocumentResult.Failure(documentId, "File is empty.");
            }

            var parser = parsers.FirstOrDefault(p => p.CanParse(contentType, fileName));
            if (parser == null)
            {
                logger.LogWarning("Unsupported format for document {DocumentId}: {FileName}", documentId, fileName);
                return ParsedDocumentResult.Failure(documentId, $"Unsupported format: {contentType}");
            }

            try
            {
                if (fileStream.CanSeek)
                {
                    fileStream.Position = 0;
                }

                return await parser.ParseAsync(fileStream, documentId, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to parse document {DocumentId}", documentId);
                return ParsedDocumentResult.Failure(documentId, "Internal parsing error.");
            }
        }
    }
}
