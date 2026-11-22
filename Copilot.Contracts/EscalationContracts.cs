using System.Text.Json.Serialization;

namespace Copilot.Contracts
{
    public class EscalationCreateRequest
    {
        [JsonPropertyName("question_id")]
        public string QuestionId { get; set; } = string.Empty;

        [JsonPropertyName("correlation_id")]
        public string CorrelationId { get; set; } = string.Empty;

        [JsonPropertyName("question")]
        public EscalationQuestionPayload Question { get; set; } = new();

        [JsonPropertyName("requester")]
        public EscalationRequesterPayload Requester { get; set; } = new();

        [JsonPropertyName("user_confirmation")]
        public EscalationUserConfirmationPayload UserConfirmation { get; set; } = new();

        [JsonPropertyName("answer_context")]
        public EscalationAnswerContextPayload AnswerContext { get; set; } = new();

        [JsonPropertyName("escalation_reason")]
        public string EscalationReason { get; set; } = "NO_RELEVANT_CONTEXT";

        [JsonPropertyName("escalation_reason_details")]
        public string? EscalationReasonDetails { get; set; }
    }

    public class EscalationQuestionPayload
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("channel")]
        public string Channel { get; set; } = "slack";

        [JsonPropertyName("asked_at")]
        public string AskedAt { get; set; } = string.Empty;
    }

    public class EscalationRequesterPayload
    {
        [JsonPropertyName("user_email")]
        public string UserEmail { get; set; } = string.Empty;
    }

    public class EscalationUserConfirmationPayload
    {
        [JsonPropertyName("confirmed")]
        public bool Confirmed { get; set; }

        [JsonPropertyName("confirmed_at")]
        public string ConfirmedAt { get; set; } = string.Empty;

        [JsonPropertyName("confirmed_by")]
        public string ConfirmedBy { get; set; } = string.Empty;

        [JsonPropertyName("confirmed_via")]
        public string ConfirmedVia { get; set; } = "slack";
    }

    public class EscalationAnswerContextPayload
    {
        [JsonPropertyName("answer_text")]
        public string? AnswerText { get; set; }

        [JsonPropertyName("confidence_score")]
        public float? ConfidenceScore { get; set; }

        [JsonPropertyName("source_references")]
        public List<EscalationSourceReference> SourceReferences { get; set; } = new();
    }

    public class EscalationSourceReference
    {
        [JsonPropertyName("document_id")]
        public string DocumentId { get; set; } = string.Empty;

        [JsonPropertyName("chunk_id")]
        public string ChunkId { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("score")]
        public float? Score { get; set; }
    }

    public class EscalationCreateResponse
    {
        [JsonPropertyName("escalation_id")]
        public string EscalationId { get; set; } = string.Empty;

        [JsonPropertyName("question_id")]
        public string QuestionId { get; set; } = string.Empty;

        [JsonPropertyName("correlation_id")]
        public string CorrelationId { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("destination_reference")]
        public string? DestinationReference { get; set; }

        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; } = string.Empty;
    }

    public class EscalationErrorResponse
    {
        [JsonPropertyName("error_code")]
        public string ErrorCode { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("correlation_id")]
        public string? CorrelationId { get; set; }

        [JsonPropertyName("question_id")]
        public string? QuestionId { get; set; }
    }
}
