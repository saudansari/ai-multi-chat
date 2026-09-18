using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SharedAgentChat.Domain;
using SharedAgentChat.Infrastructure;

namespace SharedAgentChat.Tests;

public sealed class ComposerAgentAdapterTests
{
    [Fact]
    public async Task First_turn_creates_agent_without_repos_or_env()
    {
        string? createBody = null;
        string? authParameter = null;
        var paths = new List<string>();

        var adapter = CreateAdapter(async request =>
        {
            paths.Add($"{request.Method.Method} {request.RequestUri!.AbsolutePath}");
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v1/agents")
            {
                authParameter = request.Headers.Authorization?.Parameter;
                createBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
                return Json("""
                    {
                      "agent": { "id": "bc-1", "latestRunId": "run-1" },
                      "run": { "id": "run-1", "status": "CREATING" }
                    }
                    """);
            }

            return Json("""
                {
                  "id": "run-1",
                  "agentId": "bc-1",
                  "status": "FINISHED",
                  "result": "Composer says hi"
                }
                """);
        });

        var transcript = new List<Message>
        {
            new("1", "Alice", "hello from Alice", DateTimeOffset.UtcNow)
        };

        var reply = await adapter.GetReplyAsync(
            ChatSession.WellKnownId,
            transcript,
            "Alice",
            "hello from Alice",
            "crsr_alice",
            CancellationToken.None);

        Assert.Equal("Composer says hi", reply);
        Assert.Equal("crsr_alice", authParameter);
        Assert.Contains("POST /v1/agents", paths);
        Assert.Contains(paths, p => p.StartsWith("GET /v1/agents/bc-1/runs/run-1", StringComparison.Ordinal));

        using var doc = JsonDocument.Parse(createBody!);
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("repos", out _));
        Assert.False(root.TryGetProperty("env", out _));
        Assert.Equal("composer-2.5", root.GetProperty("model").GetProperty("id").GetString());
        Assert.Equal("agent", root.GetProperty("mode").GetString());
        Assert.Contains("Alice: hello from Alice", root.GetProperty("prompt").GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("crsr_alice", createBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Second_turn_reuses_agent_with_follow_up_run()
    {
        var paths = new List<string>();
        string? followUpBody = null;

        var adapter = CreateAdapter(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            paths.Add($"{request.Method.Method} {path}");

            if (request.Method == HttpMethod.Post && path == "/v1/agents")
            {
                return Json("""
                    {
                      "agent": { "id": "bc-1" },
                      "run": { "id": "run-1", "status": "CREATING" }
                    }
                    """);
            }

            if (request.Method == HttpMethod.Post && path == "/v1/agents/bc-1/runs")
            {
                followUpBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
                return Json("""
                    {
                      "run": { "id": "run-2", "agentId": "bc-1", "status": "CREATING" }
                    }
                    """);
            }

            if (path.EndsWith("/run-1", StringComparison.Ordinal))
            {
                return Json("""{ "id": "run-1", "status": "FINISHED", "result": "first" }""");
            }

            return Json("""{ "id": "run-2", "status": "FINISHED", "result": "second" }""");
        });

        await adapter.GetReplyAsync(
            ChatSession.WellKnownId,
            [new Message("1", "Alice", "hello", DateTimeOffset.UtcNow)],
            "Alice",
            "hello",
            "crsr_alice",
            CancellationToken.None);

        var reply = await adapter.GetReplyAsync(
            ChatSession.WellKnownId,
            [
                new Message("1", "Alice", "hello", DateTimeOffset.UtcNow),
                new Message("2", Message.AgentSenderName, "first", DateTimeOffset.UtcNow),
                new Message("3", "Bob", "follow up", DateTimeOffset.UtcNow)
            ],
            "Bob",
            "follow up",
            "crsr_bob",
            CancellationToken.None);

        Assert.Equal("second", reply);
        Assert.Contains("POST /v1/agents/bc-1/runs", paths);
        Assert.Equal(1, paths.Count(p => p == "POST /v1/agents"));
        using var doc = JsonDocument.Parse(followUpBody!);
        Assert.Equal("Bob: follow up", doc.RootElement.GetProperty("prompt").GetProperty("text").GetString());
        Assert.False(doc.RootElement.TryGetProperty("repos", out _));
        Assert.False(doc.RootElement.TryGetProperty("env", out _));
    }

    [Fact]
    public async Task Lost_agent_creates_a_new_one_from_full_transcript()
    {
        var created = 0;
        var adapter = CreateAdapter(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/v1/agents")
            {
                created++;
                var id = created == 1 ? "bc-1" : "bc-2";
                var run = created == 1 ? "run-1" : "run-2";
                return Task.FromResult(Json($$"""
                    {
                      "agent": { "id": "{{id}}" },
                      "run": { "id": "{{run}}", "status": "CREATING" }
                    }
                    """));
            }

            if (request.Method == HttpMethod.Post && path == "/v1/agents/bc-1/runs")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(Json("""{ "id": "run-2", "status": "FINISHED", "result": "recreated" }"""));
        });

        await adapter.GetReplyAsync(
            ChatSession.WellKnownId,
            [new Message("1", "Alice", "hello", DateTimeOffset.UtcNow)],
            "Alice",
            "hello",
            "crsr_alice",
            CancellationToken.None);

        var reply = await adapter.GetReplyAsync(
            ChatSession.WellKnownId,
            [
                new Message("1", "Alice", "hello", DateTimeOffset.UtcNow),
                new Message("2", Message.AgentSenderName, "hi", DateTimeOffset.UtcNow),
                new Message("3", "Alice", "again", DateTimeOffset.UtcNow)
            ],
            "Alice",
            "again",
            "crsr_alice",
            CancellationToken.None);

        Assert.Equal("recreated", reply);
        Assert.Equal(2, created);
    }

    private static ComposerAgentAdapter CreateAdapter(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) =>
        new(
            new StaticHttpClientFactory(new StubHttpMessageHandler(handler)),
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

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StaticHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StaticHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) =>
            new(_handler, disposeHandler: false)
            {
                BaseAddress = new Uri("https://api.cursor.com/")
            };
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            _handler(request);
    }
}
