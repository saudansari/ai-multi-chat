using SharedAgentChat.Domain;

namespace SharedAgentChat.Application.Ports;

public interface IAgentAdapter
{
    Task<string> GetReplyAsync(
        string sessionId,
        IReadOnlyList<Message> transcript,
        string senderDisplayName,
        string text,
        string credential,
        CancellationToken cancellationToken);
}
