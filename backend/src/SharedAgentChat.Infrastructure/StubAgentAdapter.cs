using Microsoft.Extensions.Options;
using SharedAgentChat.Application.Ports;
using SharedAgentChat.Domain;

namespace SharedAgentChat.Infrastructure;

public sealed class StubAgentAdapter : IAgentAdapter
{
    private readonly AgentOptions _options;

    public StubAgentAdapter(IOptions<AgentOptions> options)
    {
        _options = options.Value;
    }

    public async Task<string> GetReplyAsync(
        string sessionId,
        IReadOnlyList<Message> transcript,
        string senderDisplayName,
        string text,
        string credential,
        CancellationToken cancellationToken)
    {
        _ = sessionId;
        _ = transcript;
        _ = senderDisplayName;
        _ = credential;
        var delay = Math.Max(0, _options.StubDelayMilliseconds);
        if (delay > 0)
        {
            await Task.Delay(delay, cancellationToken);
        }

        return $"Stub reply to: {text}";
    }
}
