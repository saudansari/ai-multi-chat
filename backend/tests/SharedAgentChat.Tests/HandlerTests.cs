using SharedAgentChat.Application;
using SharedAgentChat.Application.Ports;
using SharedAgentChat.Domain;
using SharedAgentChat.Tests.Fakes;

namespace SharedAgentChat.Tests;

public sealed class HandlerTests
{
    [Fact]
    public async Task SendMessage_appends_once_and_enqueues_once()
    {
        var sessions = new FakeSessionRepository();
        var notifier = new RecordingNotifier();
        var queue = new FakeAgentWorkQueue();
        sessions.Session.AddOrUpdateParticipant("conn-a", "Alice");

        var handler = new SendMessageHandler(sessions, notifier, queue);
        await handler.HandleAsync(ChatSession.WellKnownId, "hello", "alice-key", "conn-a");

        var transcript = sessions.Session.GetTranscriptCopy();
        Assert.Single(transcript);
        Assert.Equal("Alice", transcript[0].SenderDisplayName);
        Assert.Equal("hello", transcript[0].Text);
        Assert.True(sessions.Saved);
        Assert.Single(queue.Items);
        Assert.Equal("alice-key", queue.Items[0].TriggeringCredential);
        Assert.Equal("Alice", queue.Items[0].SenderDisplayName);
        Assert.Equal("hello", queue.Items[0].Text);
        Assert.Single(notifier.Messages);
    }

    [Fact]
    public async Task SendMessage_rejects_empty_text_and_missing_credential()
    {
        var sessions = new FakeSessionRepository();
        sessions.Session.AddOrUpdateParticipant("conn-a", "Alice");
        var handler = new SendMessageHandler(sessions, new RecordingNotifier(), new FakeAgentWorkQueue());

        await Assert.ThrowsAsync<ChatException>(() =>
            handler.HandleAsync(ChatSession.WellKnownId, " ", "key", "conn-a"));
        await Assert.ThrowsAsync<ChatException>(() =>
            handler.HandleAsync(ChatSession.WellKnownId, "hi", " ", "conn-a"));
        Assert.Empty(sessions.Session.GetTranscriptCopy());
    }

    [Fact]
    public async Task Typing_does_not_touch_transcript()
    {
        var sessions = new FakeSessionRepository();
        sessions.Session.AddOrUpdateParticipant("conn-a", "Alice");
        sessions.Session.AppendMessage("Alice", "existing");
        var notifier = new RecordingNotifier();
        var handler = new TypingHandler(sessions, notifier);

        await handler.TypingAsync(ChatSession.WellKnownId, "ignored", "conn-a");
        await handler.StoppedTypingAsync(ChatSession.WellKnownId, "ignored", "conn-a");

        Assert.Single(sessions.Session.GetTranscriptCopy());
        Assert.False(sessions.Saved);
        Assert.Equal(["Alice", "Alice"], notifier.TypingStartedNames.Concat(notifier.TypingStoppedNames));
    }

    [Fact]
    public void JoinSession_returns_transcript_copy()
    {
        var sessions = new FakeSessionRepository();
        sessions.Session.AppendMessage("Alice", "backfill");
        var handler = new JoinSessionHandler(sessions);

        var dto = handler.Handle(ChatSession.WellKnownId, "Bob", "conn-b");

        Assert.Single(dto);
        Assert.Equal("backfill", dto[0].Text);
        Assert.Equal("Bob", sessions.Session.FindParticipant("conn-b")?.DisplayName);
        Assert.Null(sessions.Session.FindParticipant("conn-b")?.ProviderCredential);
    }
}
