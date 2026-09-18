using SharedAgentChat.Application.Ports;
using SharedAgentChat.Domain;

namespace SharedAgentChat.Application;

public sealed class TypingHandler
{
    private readonly ISessionRepository _sessions;
    private readonly IRealtimeNotifier _notifier;

    public TypingHandler(ISessionRepository sessions, IRealtimeNotifier notifier)
    {
        _sessions = sessions;
        _notifier = notifier;
    }

    public Task TypingAsync(string sessionId, string displayName, string connectionId)
    {
        return RelayAsync(sessionId, displayName, connectionId, isTyping: true);
    }

    public Task StoppedTypingAsync(string sessionId, string displayName, string connectionId)
    {
        return RelayAsync(sessionId, displayName, connectionId, isTyping: false);
    }

    private async Task RelayAsync(string sessionId, string displayName, string connectionId, bool isTyping)
    {
        SessionGuard.EnsureWellKnown(sessionId);

        var name = ResolveDisplayName(sessionId, connectionId, displayName);
        _ = new TypingEvent(sessionId, name, isTyping);

        if (isTyping)
        {
            await _notifier.TypingStarted(sessionId, name, connectionId);
        }
        else
        {
            await _notifier.TypingStopped(sessionId, name, connectionId);
        }
    }

    private string ResolveDisplayName(string sessionId, string connectionId, string fallback)
    {
        var participant = _sessions.GetOrCreate(sessionId).FindParticipant(connectionId);
        if (participant is not null)
        {
            return participant.DisplayName;
        }

        if (string.IsNullOrWhiteSpace(fallback))
        {
            throw new ChatException("Display name is required.");
        }

        return fallback.Trim();
    }
}
