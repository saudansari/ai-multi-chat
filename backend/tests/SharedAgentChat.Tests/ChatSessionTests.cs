using SharedAgentChat.Domain;

namespace SharedAgentChat.Tests;

public sealed class ChatSessionTests
{
    [Fact]
    public void AppendMessage_preserves_order()
    {
        var session = new ChatSession(ChatSession.WellKnownId);
        session.AppendMessage("Alice", "one");
        session.AppendMessage("Bob", "two");
        session.AppendMessage(Message.AgentSenderName, "three");

        var copy = session.GetTranscriptCopy();
        Assert.Equal(["one", "two", "three"], copy.Select(m => m.Text));
        Assert.NotSame(copy, session.GetTranscriptCopy());
    }

    [Fact]
    public void AddOrUpdateParticipant_upserts_by_connection_id()
    {
        var session = new ChatSession(ChatSession.WellKnownId);
        session.AddOrUpdateParticipant("c1", "Alice");
        session.AddOrUpdateParticipant("c1", "Alice 2", "secret-key");
        session.AddOrUpdateParticipant("c2", "Bob");

        var alice = session.FindParticipant("c1");
        Assert.Equal("Alice 2", alice?.DisplayName);
        Assert.Equal("secret-key", alice?.ProviderCredential);
        Assert.Equal("Bob", session.FindParticipant("c2")?.DisplayName);
    }

    [Fact]
    public void Message_does_not_carry_credentials()
    {
        var names = typeof(Message).GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain("ProviderCredential", names);
        Assert.DoesNotContain("Credential", names);
        Assert.DoesNotContain("ApiKey", names);

        var message = new Message("id", "Alice", "hello", DateTimeOffset.UtcNow);
        Assert.Equal("hello", message.Text);
    }

    [Fact]
    public void RemoveParticipant_does_not_wipe_transcript()
    {
        var session = new ChatSession(ChatSession.WellKnownId);
        session.AddOrUpdateParticipant("c1", "Alice");
        session.AppendMessage("Alice", "stays");
        session.RemoveParticipant("c1");

        Assert.Null(session.FindParticipant("c1"));
        Assert.Single(session.GetTranscriptCopy());
    }
}
