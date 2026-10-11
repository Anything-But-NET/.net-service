using Copilot.IngestionWorker.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace Copilot.IngestionWorker.Adapters
{
    internal interface IMockSharePointAdapter
    {
        Task<IEnumerable<MockDocument>> GetDocumentsAsync(CancellationToken cancellationToken);
    }
}
