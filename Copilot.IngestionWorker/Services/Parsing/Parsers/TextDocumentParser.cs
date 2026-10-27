using Copilot.IngestionWorker.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace Copilot.IngestionWorker.Services.Parsing.Parsers
{
    public class TextDocumentParser : IDocumentParser
    {
        public bool CanParse(string contentType, string fileName)
        {
            return contentType.Contains("text/plain", StringComparison.OrdinalIgnoreCase) ||
                   fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<ParsedDocumentResult> ParseAsync(Stream fileStream, string documentId, CancellationToken cancellationToken)
        {
            using var reader = new StreamReader(fileStream, leaveOpen: true);
            var text = await reader.ReadToEndAsync(cancellationToken);

            return ParsedDocumentResult.Success(documentId, text);
        }
    }
}
