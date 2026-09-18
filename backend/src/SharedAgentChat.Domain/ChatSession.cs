namespace SharedAgentChat.Domain;

public sealed class ChatSession
{
    public const string WellKnownId = "poc-session";

    private readonly object _gate = new();
    private readonly List<Participant> _participants = [];
    private readonly List<Message> _transcript = [];

    public ChatSession(string id)
    {
        Id = id;
    }

    public string Id { get; }

    public void AddOrUpdateParticipant(string connectionId, string displayName, string? providerCredential = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        lock (_gate)
        {
            var existing = _participants.Find(p => p.ConnectionId == connectionId);
            if (existing is null)
            {
                _participants.Add(new Participant(connectionId, displayName.Trim(), providerCredential));
                return;
            }

            existing.DisplayName = displayName.Trim();
            if (providerCredential is not null)
            {
                existing.ProviderCredential = providerCredential;
            }
        }
    }

    public void RemoveParticipant(string connectionId)
    {
        lock (_gate)
        {
            _participants.RemoveAll(p => p.ConnectionId == connectionId);
        }
    }

    public Participant? FindParticipant(string connectionId)
    {
        lock (_gate)
        {
            return _participants.Find(p => p.ConnectionId == connectionId);
        }
    }

    public Message AppendMessage(string senderDisplayName, string text, DateTimeOffset? timestamp = null, string? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(senderDisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var message = new Message(
            id ?? Guid.NewGuid().ToString("N"),
            senderDisplayName,
            text,
            timestamp ?? DateTimeOffset.UtcNow);

        lock (_gate)
        {
            _transcript.Add(message);
        }

        return message;
    }

    public IReadOnlyList<Message> GetTranscriptCopy()
    {
        lock (_gate)
        {
            return [.. _transcript];
        }
    }
}
