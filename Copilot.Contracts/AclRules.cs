using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace Copilot.Contracts
{
    public record AclRules(
        [property: JsonPropertyName("allowed_groups")] string[] AllowedGroups
    );
}
