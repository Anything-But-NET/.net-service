using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Channels;

namespace Copilot.IngestionWorker.Services
{
    public class IngestionTriggerService
    {
        private readonly Channel<string> _channel = Channel.CreateBounded<string>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite
        });

        public bool TriggerSync(string reason = "Manual API Trigger")
        {
            return _channel.Writer.TryWrite(reason);
        }

        public IAsyncEnumerable<string> ReadTriggersAsync(CancellationToken ct)
        {
            return _channel.Reader.ReadAllAsync(ct);
        }
    }
}
