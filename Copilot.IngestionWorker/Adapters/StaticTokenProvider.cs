using Microsoft.Kiota.Abstractions.Authentication;
using System;
using System.Collections.Generic;
using System.Text;

namespace Copilot.IngestionWorker.Adapters
{
    public sealed class StaticTokenProvider : IAccessTokenProvider
    {
        public AllowedHostsValidator AllowedHostsValidator { get; } = new();
        public Task<string> GetAuthorizationTokenAsync(Uri uri, Dictionary<string, object>? additionalAuthContext = null, CancellationToken ct = default)
            => Task.FromResult("app");
    }
}
