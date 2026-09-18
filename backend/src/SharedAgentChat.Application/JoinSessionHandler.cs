using SharedAgentChat.Application.Ports;

namespace SharedAgentChat.Application;

public sealed class JoinSessionHandler
{
    private readonly ISessionRepository _sessions;

    public JoinSessionHandler(ISessionRepository sessions)
    {
        _sessions = sessions;
    }

    public IReadOnlyList<MessageDto> Handle(string sessionId, string displayName, string connectionId)
    {
        SessionGuard.EnsureWellKnown(sessionId);

        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ChatException("Display name is required.");
        }

        if (string.IsNullOrWhiteSpace(connectionId))
        {
            throw new ChatException("Connection id is required.");
        }

        var session = _sessions.GetOrCreate(sessionId);
        session.AddOrUpdateParticipant(connectionId, displayName.Trim());
        return session.GetTranscriptCopy().Select(MessageDto.From).ToList();
    }
}
