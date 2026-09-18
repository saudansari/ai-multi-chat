using Microsoft.AspNetCore.SignalR;
using SharedAgentChat.Application;

namespace SharedAgentChat.Web.Hubs;

public sealed class ChatHub : Hub
{
    private readonly JoinSessionHandler _join;
    private readonly SendMessageHandler _send;
    private readonly TypingHandler _typing;
    private readonly DisconnectHandler _disconnect;

    public ChatHub(
        JoinSessionHandler join,
        SendMessageHandler send,
        TypingHandler typing,
        DisconnectHandler disconnect)
    {
        _join = join;
        _send = send;
        _typing = typing;
        _disconnect = disconnect;
    }

    public async Task<IReadOnlyList<MessageDto>> JoinSession(string sessionId, string displayName)
    {
        try
        {
            var transcript = _join.Handle(sessionId, displayName, Context.ConnectionId);
            await Groups.AddToGroupAsync(Context.ConnectionId, sessionId);
            return transcript;
        }
        catch (ChatException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    public async Task SendMessage(string sessionId, string text, string credential)
    {
        try
        {
            await _send.HandleAsync(sessionId, text, credential, Context.ConnectionId);
        }
        catch (ChatException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    public async Task Typing(string sessionId, string displayName)
    {
        try
        {
            await _typing.TypingAsync(sessionId, displayName, Context.ConnectionId);
        }
        catch (ChatException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    public async Task StoppedTyping(string sessionId, string displayName)
    {
        try
        {
            await _typing.StoppedTypingAsync(sessionId, displayName, Context.ConnectionId);
        }
        catch (ChatException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _disconnect.Handle(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
