using SharedAgentChat.Domain;

namespace SharedAgentChat.Application;

public sealed record MessageDto(string Id, string SenderDisplayName, string Text, DateTimeOffset Timestamp)
{
    public static MessageDto From(Message message) =>
        new(message.Id, message.SenderDisplayName, message.Text, message.Timestamp);
}
