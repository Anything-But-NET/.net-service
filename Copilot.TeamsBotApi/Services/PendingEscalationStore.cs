using System.Collections.Concurrent;
using Copilot.Contracts;

namespace Copilot.TeamsBotApi.Services;

public sealed record PendingEscalationContext(
    string QuestionId,
    string CorrelationId,
    string IdempotencyKey,
    string QuestionText,
    DateTimeOffset AskedAt,
    string UserEmail,
    string SlackUserId,
    string Channel,
    string? AnswerText,
    float? ConfidenceScore,
    List<EscalationSourceReference> SourceReferences,
    string EscalationReason,
    DateTimeOffset ExpiresAt);

public interface IPendingEscalationStore
{
    void Save(PendingEscalationContext context);
    bool TryGet(string questionId, out PendingEscalationContext? context);
    bool TryTake(string questionId, out PendingEscalationContext? context);
    bool Remove(string questionId);
}

public sealed class InMemoryPendingEscalationStore : IPendingEscalationStore
{
    private readonly ConcurrentDictionary<string, PendingEscalationContext> _entries = new(StringComparer.OrdinalIgnoreCase);

    public void Save(PendingEscalationContext context)
    {
        PruneExpired();
        _entries[context.QuestionId] = context;
    }

    public bool TryGet(string questionId, out PendingEscalationContext? context)
    {
        if (string.IsNullOrWhiteSpace(questionId) || !_entries.TryGetValue(questionId, out var found))
        {
            context = null;
            return false;
        }

        if (found.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _entries.TryRemove(questionId, out _);
            context = null;
            return false;
        }

        context = found;
        return true;
    }

    public bool TryTake(string questionId, out PendingEscalationContext? context)
    {
        if (string.IsNullOrWhiteSpace(questionId) || !_entries.TryRemove(questionId, out var removed))
        {
            context = null;
            return false;
        }

        if (removed.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            context = null;
            return false;
        }

        context = removed;
        return true;
    }

    public bool Remove(string questionId)
    {
        if (string.IsNullOrWhiteSpace(questionId))
            return false;

        return _entries.TryRemove(questionId, out _);
    }

    private void PruneExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, value) in _entries)
        {
            if (value.ExpiresAt <= now)
            {
                _entries.TryRemove(key, out _);
            }
        }
    }
}
