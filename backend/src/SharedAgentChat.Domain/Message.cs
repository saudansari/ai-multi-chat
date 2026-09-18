namespace SharedAgentChat.Domain;

public sealed class Message
{
    public const string AgentSenderName = "Agent";

    public Message(string id, string senderDisplayName, string text, DateTimeOffset timestamp)
    {
        Id = id;
        SenderDisplayName = senderDisplayName;
        Text = text;
        Timestamp = timestamp;
    }

    public string Id { get; }
    public string SenderDisplayName { get; }
    public string Text { get; }
    public DateTimeOffset Timestamp { get; }
}
