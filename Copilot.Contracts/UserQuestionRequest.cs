using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace Copilot.Contracts
{
    public class UserQuestionRequest
    {
        [JsonPropertyName("query_id")]
        public string QueryId { get; set; }

        [JsonPropertyName("channel")]
        public string Channel { get; set; }

        [JsonPropertyName("user_email")]
        public string UserEmail { get; set; }

        [JsonPropertyName("query")]
        public string Query { get; set; }
    }
}
