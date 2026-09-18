using SharedAgentChat.Domain;

namespace SharedAgentChat.Application;

public static class SessionGuard
{
    public static void EnsureWellKnown(string sessionId)
    {
        if (!string.Equals(sessionId, ChatSession.WellKnownId, StringComparison.Ordinal))
        {
            throw new ChatException($"Unknown session '{sessionId}'. This POC only supports '{ChatSession.WellKnownId}'.");
        }
    }
}
