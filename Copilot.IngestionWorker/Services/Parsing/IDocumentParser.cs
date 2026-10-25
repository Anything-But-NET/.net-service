using Copilot.IngestionWorker.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace Copilot.IngestionWorker.Services.Parsing
{
    public interface IDocumentParser
    {
        bool CanParse(string contentType, string fileName);
        Task<ParsedDocumentResult> ParseAsync(Stream fileStream, string documentId, CancellationToken cancellationToken);
    }
}
