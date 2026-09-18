using Microsoft.AspNetCore.SignalR;
using SharedAgentChat.Application;
using SharedAgentChat.Application.Ports;
using SharedAgentChat.Domain;
using SharedAgentChat.Web.Hubs;

namespace SharedAgentChat.Web.Realtime;

public sealed class SignalRRealtimeNotifier : IRealtimeNotifier
{
    private readonly IHubContext<ChatHub> _hub;

    public SignalRRealtimeNotifier(IHubContext<ChatHub> hub)
    {
        _hub = hub;
    }

    public Task MessageReceived(string sessionId, Message message) =>
        _hub.Clients.Group(sessionId).SendAsync("MessageReceived", MessageDto.From(message));

    public Task TypingStarted(string sessionId, string displayName, string? excludeConnectionId) =>
        ClientsFor(sessionId, excludeConnectionId).SendAsync("TypingStarted", displayName);

    public Task TypingStopped(string sessionId, string displayName, string? excludeConnectionId) =>
        ClientsFor(sessionId, excludeConnectionId).SendAsync("TypingStopped", displayName);

    public Task AgentResponding(string sessionId) =>
        _hub.Clients.Group(sessionId).SendAsync("AgentResponding");

    public Task AgentIdle(string sessionId) =>
        _hub.Clients.Group(sessionId).SendAsync("AgentIdle");

    private IClientProxy ClientsFor(string sessionId, string? excludeConnectionId)
    {
        if (string.IsNullOrEmpty(excludeConnectionId))
        {
            return _hub.Clients.Group(sessionId);
        }

        return _hub.Clients.GroupExcept(sessionId, [excludeConnectionId]);
    }
}
