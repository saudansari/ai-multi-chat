using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SharedAgentChat.Application.Ports;
using SharedAgentChat.Domain;
using SharedAgentChat.Infrastructure;
using SharedAgentChat.Tests.Fakes;

namespace SharedAgentChat.Tests;

public sealed class AgentQueueTests
{
    [Fact]
    public async Task Overlapping_sends_run_agent_calls_sequentially()
    {
        var sessions = new FakeSessionRepository();
        var notifier = new RecordingNotifier();
        var adapter = new SlowTrackingAdapter(TimeSpan.FromMilliseconds(200));
        var queue = new SessionAgentWorkQueue(
            sessions,
            adapter,
            notifier,
            NullLogger<SessionAgentWorkQueue>.Instance);

        sessions.Session.AppendMessage("Alice", "A");
        queue.Enqueue(new AgentWorkItem(ChatSession.WellKnownId, "key-a", "Alice", "A"));
        sessions.Session.AppendMessage("Bob", "B");
        queue.Enqueue(new AgentWorkItem(ChatSession.WellKnownId, "key-b", "Bob", "B"));

        await queue.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => sessions.Session.GetTranscriptCopy().Count(m => m.SenderDisplayName == Message.AgentSenderName) == 2);
        }
        finally
        {
            await queue.StopAsync(CancellationToken.None);
        }

        Assert.Equal(1, adapter.MaxInFlight);
        Assert.Equal(["key-a", "key-b"], adapter.Credentials);
        Assert.Equal(
            ["Alice", "Bob", Message.AgentSenderName, Message.AgentSenderName],
            sessions.Session.GetTranscriptCopy().Select(m => m.SenderDisplayName));
        Assert.Contains("AgentResponding", notifier.Status);
        Assert.Equal("AgentIdle", notifier.Status.Last());
    }

    [Fact]
    public async Task Adapter_failure_still_broadcasts_idle()
    {
        var sessions = new FakeSessionRepository();
        var notifier = new RecordingNotifier();
        var queue = new SessionAgentWorkQueue(
            sessions,
            new ThrowingAdapter(),
            notifier,
            NullLogger<SessionAgentWorkQueue>.Instance);

        await queue.ProcessAsync(new AgentWorkItem(ChatSession.WellKnownId, "key", "Alice", "hello"), CancellationToken.None);

        Assert.Equal(["AgentResponding", "AgentIdle"], notifier.Status);
        var agentMessages = sessions.Session.GetTranscriptCopy()
            .Where(m => m.SenderDisplayName == Message.AgentSenderName)
            .ToList();
        Assert.Single(agentMessages);
        Assert.Contains("could not reply", agentMessages[0].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(agentMessages[0].Id, notifier.Messages.Single().Id);
    }

    [Fact]
    public async Task Empty_adapter_reply_still_appends_agent_message()
    {
        var sessions = new FakeSessionRepository();
        var notifier = new RecordingNotifier();
        var queue = new SessionAgentWorkQueue(
            sessions,
            new EmptyAdapter(),
            notifier,
            NullLogger<SessionAgentWorkQueue>.Instance);

        await queue.ProcessAsync(new AgentWorkItem(ChatSession.WellKnownId, "key", "Alice", "hello"), CancellationToken.None);

        Assert.Contains(
            sessions.Session.GetTranscriptCopy(),
            m => m.SenderDisplayName == Message.AgentSenderName && m.Text.Contains("without a reply", StringComparison.OrdinalIgnoreCase));
        Assert.Single(notifier.Messages);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("Condition was not met in time.");
    }

    private sealed class SlowTrackingAdapter : IAgentAdapter
    {
        private readonly TimeSpan _delay;
        private int _inFlight;

        public SlowTrackingAdapter(TimeSpan delay) => _delay = delay;

        public int MaxInFlight { get; private set; }

        public List<string> Credentials { get; } = [];

        public async Task<string> GetReplyAsync(
            string sessionId,
            IReadOnlyList<Message> transcript,
            string senderDisplayName,
            string text,
            string credential,
            CancellationToken cancellationToken)
        {
            _ = sessionId;
            _ = senderDisplayName;
            Credentials.Add(credential);
            var current = Interlocked.Increment(ref _inFlight);
            lock (this)
            {
                MaxInFlight = Math.Max(MaxInFlight, current);
            }

            try
            {
                await Task.Delay(_delay, cancellationToken);
                _ = transcript;
                return $"reply-to-{text}";
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    private sealed class ThrowingAdapter : IAgentAdapter
    {
        public Task<string> GetReplyAsync(
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
            _ = text;
            _ = credential;
            _ = cancellationToken;
            return Task.FromException<string>(new InvalidOperationException("boom"));
        }
    }

    private sealed class EmptyAdapter : IAgentAdapter
    {
        public Task<string> GetReplyAsync(
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
            _ = text;
            _ = credential;
            _ = cancellationToken;
            return Task.FromResult("   ");
        }
    }
}
