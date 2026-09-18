using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SharedAgentChat.Domain;
using SharedAgentChat.Infrastructure;

namespace SharedAgentChat.Tests;

[Trait("Category", "Integration")]
public sealed class ComposerLiveIntegrationTests
{
    [LiveCursorFact]
    public async Task Create_agent_returns_reply_then_deletes_agent()
    {
        var credential = Environment.GetEnvironmentVariable("CURSOR_API_KEY")!;
        var sessionId = ChatSession.WellKnownId + "-live-" + Guid.NewGuid().ToString("N")[..8];
        var adapter = new ComposerAgentAdapter(
            new LiveHttpClientFactory(),
            Options.Create(new AgentOptions
            {
                Provider = "Composer",
                Composer = new ComposerOptions
                {
                    BaseUrl = "https://api.cursor.com",
                    ModelId = "composer-2.5",
                    Mode = "Fast"
                }
            }),
            NullLogger<ComposerAgentAdapter>.Instance);

        var transcript = new List<Message>
        {
            new(Guid.NewGuid().ToString("N"), "Alice", "Reply with the single word pong. Do not use tools.", DateTimeOffset.UtcNow)
        };

        try
        {
            var reply = await adapter.GetReplyAsync(
                sessionId,
                transcript,
                "Alice",
                transcript[0].Text,
                credential,
                CancellationToken.None);

            Assert.False(string.IsNullOrWhiteSpace(reply));
            Assert.True(adapter.TryGetBoundAgentId(sessionId, out var agentId));
            Assert.StartsWith("bc-", agentId);
            Assert.Contains("pong", reply, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await adapter.DeleteBoundAgentAsync(sessionId, credential, CancellationToken.None);
            Assert.False(adapter.TryGetBoundAgentId(sessionId, out _));
        }
    }

    private sealed class LiveCursorFactAttribute : FactAttribute
    {
        public LiveCursorFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CURSOR_API_KEY")))
            {
                Skip = "Set CURSOR_API_KEY to run live Composer tests.";
            }

            Timeout = 600_000;
        }
    }

    private sealed class LiveHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new()
            {
                BaseAddress = new Uri("https://api.cursor.com/"),
                Timeout = TimeSpan.FromMinutes(10)
            };
    }
}
