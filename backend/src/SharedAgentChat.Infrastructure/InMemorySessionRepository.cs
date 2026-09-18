using System.Collections.Concurrent;
using SharedAgentChat.Application;
using SharedAgentChat.Application.Ports;
using SharedAgentChat.Domain;

namespace SharedAgentChat.Infrastructure;

public sealed class InMemorySessionRepository : ISessionRepository
{
    private readonly ConcurrentDictionary<string, ChatSession> _sessions = new(StringComparer.Ordinal);

    public ChatSession GetOrCreate(string sessionId)
    {
        SessionGuard.EnsureWellKnown(sessionId);
        return _sessions.GetOrAdd(sessionId, id => new ChatSession(id));
    }

    public void Save(ChatSession session)
    {
        // Live transcript is already the source of truth.
        _ = session;
    }
}
