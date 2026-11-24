using System.Collections.Concurrent;

namespace Copilot.TeamsBotApi.Services;

public sealed record PendingEscalationContext(
    string QueryId,
    string SlackUserId,
    string Channel,
    DateTimeOffset ExpiresAt);

public interface IPendingEscalationStore
{
    void Save(PendingEscalationContext context);
    bool TryGet(string queryId, out PendingEscalationContext? context);
    bool TryTake(string queryId, out PendingEscalationContext? context);
    bool Remove(string queryId);
}

public sealed class InMemoryPendingEscalationStore : IPendingEscalationStore
{
    private readonly ConcurrentDictionary<string, PendingEscalationContext> _entries = new(StringComparer.OrdinalIgnoreCase);

    public void Save(PendingEscalationContext context)
    {
        PruneExpired();
        _entries[context.QueryId] = context;
    }

    public bool TryGet(string queryId, out PendingEscalationContext? context)
    {
        if (string.IsNullOrWhiteSpace(queryId) || !_entries.TryGetValue(queryId, out var found))
        {
            context = null;
            return false;
        }

        if (found.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _entries.TryRemove(queryId, out _);
            context = null;
            return false;
        }

        context = found;
        return true;
    }

    public bool TryTake(string queryId, out PendingEscalationContext? context)
    {
        if (string.IsNullOrWhiteSpace(queryId) || !_entries.TryRemove(queryId, out var removed))
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

    public bool Remove(string queryId)
    {
        if (string.IsNullOrWhiteSpace(queryId))
            return false;

        return _entries.TryRemove(queryId, out _);
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
