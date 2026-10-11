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
        DateTime CreatedAt
    );
}
