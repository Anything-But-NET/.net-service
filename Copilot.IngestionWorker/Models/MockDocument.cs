using System;
using System.Collections.Generic;
using System.Text;

namespace Copilot.IngestionWorker.Models
{
    public record MockDocument(
        string Id,
        string Title,
        string Content,
        string SourceUrl,
        DateTime CreatedAt,
        IReadOnlyList<string>? AllowedGroups = null,
        byte[]? RawBytes = null,
        string? ContentType = null,
        Action? OnSynced = null
    );
}

