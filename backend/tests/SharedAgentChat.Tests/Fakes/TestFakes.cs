using SharedAgentChat.Application;
using SharedAgentChat.Application.Ports;
using SharedAgentChat.Domain;

namespace SharedAgentChat.Tests.Fakes;

public sealed class FakeSessionRepository : ISessionRepository
{
    public ChatSession Session { get; } = new(ChatSession.WellKnownId);

    public bool Saved { get; private set; }

    public ChatSession GetOrCreate(string sessionId)
    {
        SessionGuard.EnsureWellKnown(sessionId);
        return Session;
    }

    public void Save(ChatSession session)
    {
        _ = session;
        Saved = true;
    }
}

public sealed class FakeAgentWorkQueue : IAgentWorkQueue
{
    public List<AgentWorkItem> Items { get; } = [];

    public void Enqueue(AgentWorkItem item) => Items.Add(item);
}

public sealed class RecordingNotifier : IRealtimeNotifier
{
    public List<Message> Messages { get; } = [];
    public List<string> TypingStartedNames { get; } = [];
    public List<string> TypingStoppedNames { get; } = [];
    public List<string> Status { get; } = [];

    public Task MessageReceived(string sessionId, Message message)
    {
        Messages.Add(message);
        return Task.CompletedTask;
    }

    Task IRealtimeNotifier.TypingStarted(string sessionId, string displayName, string? excludeConnectionId)
    {
        TypingStartedNames.Add(displayName);
        return Task.CompletedTask;
    }

    Task IRealtimeNotifier.TypingStopped(string sessionId, string displayName, string? excludeConnectionId)
    {
        TypingStoppedNames.Add(displayName);
        return Task.CompletedTask;
    }

    public Task AgentResponding(string sessionId)
    {
        Status.Add("AgentResponding");
        return Task.CompletedTask;
    }

    public Task AgentIdle(string sessionId)
    {
        Status.Add("AgentIdle");
        return Task.CompletedTask;
    }
}
