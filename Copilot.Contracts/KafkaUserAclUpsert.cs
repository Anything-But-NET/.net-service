using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace Copilot.Contracts
{
    public record KafkaUserAclUpsert
    {
        [JsonPropertyName("email")]
        public required string Email { get; init; }

        [JsonPropertyName("last_updated")]
        public required string LastUpdated { get; init; }

        [JsonPropertyName("acl")]
        public required AclPayload Acl { get; init; }
    }
}
