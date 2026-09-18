using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharedAgentChat.Application.Ports;
using SharedAgentChat.Domain;

namespace SharedAgentChat.Infrastructure;

public sealed class SessionAgentWorkQueue : BackgroundService, IAgentWorkQueue
{
    private readonly Channel<AgentWorkItem> _channel = Channel.CreateUnbounded<AgentWorkItem>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly ISessionRepository _sessions;
    private readonly IAgentAdapter _agent;
    private readonly IRealtimeNotifier _notifier;
    private readonly ILogger<SessionAgentWorkQueue> _logger;

    public SessionAgentWorkQueue(
        ISessionRepository sessions,
        IAgentAdapter agent,
        IRealtimeNotifier notifier,
        ILogger<SessionAgentWorkQueue> logger)
    {
        _sessions = sessions;
        _agent = agent;
        _notifier = notifier;
        _logger = logger;
    }

    public void Enqueue(AgentWorkItem item)
    {
        if (!_channel.Writer.TryWrite(item))
        {
            throw new InvalidOperationException("Agent work queue is closed.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            await ProcessAsync(item, stoppingToken);
        }
    }

    internal async Task ProcessAsync(AgentWorkItem item, CancellationToken cancellationToken)
    {
        try
        {
            await _notifier.AgentResponding(item.SessionId);
            var session = _sessions.GetOrCreate(item.SessionId);
            var snapshot = session.GetTranscriptCopy();
            string text;
            try
            {
                var reply = await _agent.GetReplyAsync(
                    item.SessionId,
                    snapshot,
                    item.SenderDisplayName,
                    item.Text,
                    item.TriggeringCredential,
                    cancellationToken);
                text = string.IsNullOrWhiteSpace(reply)
                    ? "The agent finished without a reply."
                    : reply.Trim();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent work failed for session {SessionId}.", item.SessionId);
                text = UserFacingAgentError(ex);
            }

            var message = session.AppendMessage(Message.AgentSenderName, text);
            _sessions.Save(session);
            await _notifier.MessageReceived(item.SessionId, message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent work failed for session {SessionId}.", item.SessionId);
        }
        finally
        {
            try
            {
                await _notifier.AgentIdle(item.SessionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to broadcast AgentIdle for session {SessionId}.", item.SessionId);
            }
        }
    }

    private static string UserFacingAgentError(Exception ex)
    {
        var detail = ex.Message?.Trim() ?? "";
        if (detail.Length is 0 or > 280)
        {
            return "The agent hit an error and could not reply.";
        }

        return $"The agent could not reply: {detail}";
    }
}
