using SharedAgentChat.Application.Ports;

namespace SharedAgentChat.Application;

public sealed class SendMessageHandler
{
    private readonly ISessionRepository _sessions;
    private readonly IRealtimeNotifier _notifier;
    private readonly IAgentWorkQueue _agentQueue;

    public SendMessageHandler(
        ISessionRepository sessions,
        IRealtimeNotifier notifier,
        IAgentWorkQueue agentQueue)
    {
        _sessions = sessions;
        _notifier = notifier;
        _agentQueue = agentQueue;
    }

    public async Task HandleAsync(string sessionId, string text, string credential, string connectionId)
    {
        SessionGuard.EnsureWellKnown(sessionId);

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ChatException("Message text is required.");
        }

        if (string.IsNullOrWhiteSpace(credential))
        {
            throw new ChatException("API key is required.");
        }

        var session = _sessions.GetOrCreate(sessionId);
        var participant = session.FindParticipant(connectionId)
            ?? throw new ChatException("Join the session before sending a message.");

        session.AddOrUpdateParticipant(connectionId, participant.DisplayName, credential.Trim());
        var message = session.AppendMessage(participant.DisplayName, text.Trim());
        _sessions.Save(session);

        await _notifier.MessageReceived(sessionId, message);
        _agentQueue.Enqueue(new AgentWorkItem(
            sessionId,
            credential.Trim(),
            participant.DisplayName,
            text.Trim()));
    }
}
