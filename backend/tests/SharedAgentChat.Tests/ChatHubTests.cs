using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using SharedAgentChat.Application;
using SharedAgentChat.Domain;

namespace SharedAgentChat.Tests;

public sealed class ChatHubTests
{
    [Fact]
    public async Task Static_index_and_hub_negotiate_succeed()
    {
        await using var factory = new ChatWebApplicationFactory();
        var client = factory.CreateClient();
        var index = await client.GetAsync("/");
        index.EnsureSuccessStatusCode();
        var html = await index.Content.ReadAsStringAsync();
        Assert.Contains("/hubs/chat", html, StringComparison.Ordinal);

        var negotiate = await client.PostAsync("/hubs/chat/negotiate?negotiateVersion=1", null);
        negotiate.EnsureSuccessStatusCode();

        var agent = await client.GetAsync("/api/agent");
        agent.EnsureSuccessStatusCode();
        var agentJson = await agent.Content.ReadAsStringAsync();
        Assert.Contains("Stub", agentJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Two_clients_share_transcript_typing_and_group_scope()
    {
        await using var factory = new ChatWebApplicationFactory();
        await using var alice = await ConnectAsync(factory);
        await using var bob = await ConnectAsync(factory);
        await using var outsider = await ConnectAsync(factory);

        var aliceMessages = SubscribeMessages(alice);
        var bobMessages = SubscribeMessages(bob);
        var outsiderMessages = SubscribeMessages(outsider);
        var bobTyping = new List<string>();
        var aliceTyping = new List<string>();
        var bobAgent = new List<string>();
        bob.On<string>("TypingStarted", name => bobTyping.Add(name));
        alice.On<string>("TypingStarted", name => aliceTyping.Add(name));
        bob.On("AgentResponding", () => bobAgent.Add("responding"));
        bob.On("AgentIdle", () => bobAgent.Add("idle"));

        var aliceJoin = await alice.InvokeAsync<List<MessageDto>>("JoinSession", ChatSession.WellKnownId, "Alice");
        Assert.Empty(aliceJoin);

        var bobJoin = await bob.InvokeAsync<List<MessageDto>>("JoinSession", ChatSession.WellKnownId, "Bob");
        Assert.Empty(bobJoin);

        await alice.InvokeAsync("SendMessage", ChatSession.WellKnownId, "hello from Alice", "alice-key");
        await WaitUntilAsync(() =>
            aliceMessages.Any(m => m.Text == "hello from Alice")
            && bobMessages.Any(m => m.Text == "hello from Alice"));

        await using var lateJoiner = await ConnectAsync(factory);
        var lateTranscript = await lateJoiner.InvokeAsync<List<MessageDto>>("JoinSession", ChatSession.WellKnownId, "Cara");
        Assert.Contains(lateTranscript, m => m.Text == "hello from Alice");

        await alice.InvokeAsync("Typing", ChatSession.WellKnownId, "Alice");
        await WaitUntilAsync(() => bobTyping.Contains("Alice"));
        Assert.DoesNotContain("Alice", aliceTyping);

        await WaitUntilAsync(() => bobAgent.Contains("responding") && bobAgent.Contains("idle"));
        await WaitUntilAsync(() =>
            aliceMessages.Any(m => m.SenderDisplayName == Message.AgentSenderName)
            && bobMessages.Any(m => m.SenderDisplayName == Message.AgentSenderName));

        Assert.Empty(outsiderMessages);
        Assert.All(aliceMessages.Concat(bobMessages).Concat(lateTranscript), m =>
        {
            var json = JsonSerializer.Serialize(m);
            Assert.DoesNotContain("alice-key", json, StringComparison.Ordinal);
            Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static async Task<HubConnection> ConnectAsync(ChatWebApplicationFactory factory)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(factory.Server.BaseAddress + "hubs/chat", options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            })
            .Build();
        await connection.StartAsync();
        return connection;
    }

    private static List<MessageDto> SubscribeMessages(HubConnection connection)
    {
        var messages = new List<MessageDto>();
        connection.On<MessageDto>("MessageReceived", message => messages.Add(message));
        return messages;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Condition was not met in time.");
    }
}
