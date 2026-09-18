using SharedAgentChat.Domain;

namespace SharedAgentChat.Application.Ports;

public interface IRealtimeNotifier
{
    Task MessageReceived(string sessionId, Message message);

    Task TypingStarted(string sessionId, string displayName, string? excludeConnectionId);

    Task TypingStopped(string sessionId, string displayName, string? excludeConnectionId);

    Task AgentResponding(string sessionId);

    Task AgentIdle(string sessionId);
}
