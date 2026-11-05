using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace Copilot.Contracts
{
    public record DocumentUpdateAclEvent
    {
        [JsonPropertyName("sync_id")]
        public required string SyncId { get; init; }

        [JsonPropertyName("document_id")]
        public required string DocumentId { get; init; }

        [JsonPropertyName("action")]
        public required string Action { get; init; } 

        [JsonPropertyName("last_updated")]
        public required string LastUpdated { get; init; }

        [JsonPropertyName("acl")]
        public required AclPayload Acl { get; init; }
    }
}
