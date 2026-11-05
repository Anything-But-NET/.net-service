using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace Copilot.Contracts
{
    public record AclPayload
    {
        [JsonPropertyName("allowed_groups")]
        public List<string> AllowedGroups { get; set; } = new();
    }
}
