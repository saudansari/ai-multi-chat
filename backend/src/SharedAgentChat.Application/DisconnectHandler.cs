using SharedAgentChat.Application.Ports;
using SharedAgentChat.Domain;

namespace SharedAgentChat.Application;

public sealed class DisconnectHandler
{
    private readonly ISessionRepository _sessions;

    public DisconnectHandler(ISessionRepository sessions)
    {
        _sessions = sessions;
    }

    public void Handle(string connectionId)
    {
        var session = _sessions.GetOrCreate(ChatSession.WellKnownId);
        session.RemoveParticipant(connectionId);
    }
}
