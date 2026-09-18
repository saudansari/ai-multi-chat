namespace SharedAgentChat.Domain;

public sealed record TypingEvent(string SessionId, string DisplayName, bool IsTyping);
