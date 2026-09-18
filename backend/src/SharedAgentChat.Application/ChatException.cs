namespace SharedAgentChat.Application;

public sealed class ChatException : Exception
{
    public ChatException(string message) : base(message)
    {
    }
}
