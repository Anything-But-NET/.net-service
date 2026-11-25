using System.Text.Json.Serialization;

namespace Copilot.Contracts
{
    public class CreateEscalationRequest
    {
        [JsonPropertyName("query_id")]
        public string QueryId { get; set; } = string.Empty;
    }

    public class EscalationResponse
    {
        [JsonPropertyName("escalation_id")]
        public string EscalationId { get; set; } = string.Empty;

        [JsonPropertyName("query_id")]
        public string QueryId { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; } = string.Empty;

        [JsonPropertyName("updated_at")]
        public string UpdatedAt { get; set; } = string.Empty;
    }
}
