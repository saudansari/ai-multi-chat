using SharedAgentChat.Domain;

namespace SharedAgentChat.Application.Ports;

public interface ISessionRepository
{
    ChatSession GetOrCreate(string sessionId);

    void Save(ChatSession session);
}
