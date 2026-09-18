namespace SharedAgentChat.Application.Ports;

public sealed record AgentWorkItem(
    string SessionId,
    string TriggeringCredential,
    string SenderDisplayName,
    string Text);
