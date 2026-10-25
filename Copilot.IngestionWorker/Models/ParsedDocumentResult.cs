using System;
using System.Collections.Generic;
using System.Text;

namespace Copilot.IngestionWorker.Models
{
    public record ParsedDocumentResult
    {
        public string DocumentId { get; init; }
        public bool IsSuccess { get; init; }
        public string ParsedText { get; init; } = string.Empty;
        public string ErrorMessage { get; init; } = string.Empty;

        public static ParsedDocumentResult Success(string id, string text) =>
            new() { DocumentId = id, IsSuccess = true, ParsedText = text };

        public static ParsedDocumentResult Failure(string id, string error) =>
            new() { DocumentId = id, IsSuccess = false, ErrorMessage = error };
    }
}
