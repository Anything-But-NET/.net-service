
using System;
using System.Text.Json.Serialization;

namespace Copilot.Contracts
{
    public record NewFileDataEvent(
        [property: JsonPropertyName("document_id")] string DocumentId,
        [property: JsonPropertyName("source_system")] string SourceSystem,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("raw_content")] string RawContent,
        [property: JsonPropertyName("last_updated")] DateTime LastUpdated,
        [property: JsonPropertyName("acl")] AclRules Acl,
        [property: JsonPropertyName("is_deleted")] bool IsDeleted = false
    );
}
