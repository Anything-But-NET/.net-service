using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace Copilot.Contracts
{
    public class UserQuestionResponse
    {
        [JsonPropertyName("query_id")]
        public string QueryId { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; }

        [JsonPropertyName("reply_text")]
        public string ReplyText { get; set; }

        [JsonPropertyName("citations")]
        public List<Citation> Citations { get; set; }
    }
}
