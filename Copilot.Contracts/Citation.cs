using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace Copilot.Contracts
{
    public class Citation
    {
        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("url")]
        public string Url { get; set; }

        [JsonPropertyName("document_id")]
        public string DocumentId { get; set; }

        [JsonPropertyName("chunk_id")]
        public string ChunkId { get; set; }

        [JsonPropertyName("score")]
        public float Score { get; set; }
    }
}
