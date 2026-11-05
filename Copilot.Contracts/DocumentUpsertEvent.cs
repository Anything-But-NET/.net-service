using System.Text.Json.Serialization;

namespace Copilot.Contracts
{
    public record DocumentUpsertEvent
    {
        [JsonPropertyName("sync_id")]
        public required string SyncId { get; init; }

        [JsonPropertyName("document_id")]
        public required string DocumentId { get; init; }

        [JsonPropertyName("action")]
        public required string Action { get; init; }

        [JsonPropertyName("source_system")]
        public required string SourceSystem { get; init; } 

        [JsonPropertyName("title")]
        public required string Title { get; init; }

        [JsonPropertyName("url")]
        public required string Url { get; init; }

        [JsonPropertyName("raw_content")]
        public required string RawContent { get; init; }

        [JsonPropertyName("last_updated")]
        public required string LastUpdated { get; init; }

        [JsonPropertyName("acl")]
        public required AclPayload Acl { get; init; }
    }
}
