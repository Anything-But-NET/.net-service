using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace Copilot.Contracts
{
    public class GatewayErrorResponse
    {
        [JsonPropertyName("query_id")]
        public string QueryId { get; set; }

        [JsonPropertyName("error_code")]
        public string ErrorCode { get; set; }

        [JsonPropertyName("user_message")]
        public string UserMessage { get; set; }
    }
}
